using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OpenTuyaSync.Core.Ipc;

namespace OpenTuyaSync.Providers.Tuya;

/// <summary>
/// Поиск устройств Tuya в локальной сети широковещательным UDP-запросом.
///
/// Находит Device ID, IP, версию протокола и признак уже выполненных
/// сопряжений. Local Key так получить НЕЛЬЗЯ: Tuya создаёт его на стороне
/// облака при сопряжении и не передаёт в сеть в открытом виде -- это
/// ограничение протокола, а не реализации. Ключ берётся из приложения
/// Smart Life (настройки устройства → информация об устройстве).
/// </summary>
public static class TuyaScanner
{
    private const int TuyaPort = 6666;
    private const int TuyaPortV3 = 6667;

    /// <summary>
    /// Запускает сканирование. Занимает указанное время: ответы приходят
    /// не мгновенно, устройства отвечают вразнобой.
    /// </summary>
    public static List<DiscoveredDevice> Scan(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + timeout;

        using var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };
        udp.Client.ReceiveTimeout = 250;

        byte[] payload = BuildScanPayload(EncryptionKey());
        IPEndPoint broadcast = new(IPAddress.Broadcast, TuyaPort);

        int attempts = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                udp.Send(payload, payload.Length, broadcast);
                ++attempts;
            }
            catch (SocketException)
            {
                // Широковещание может быть запрещено политикой сети --
                // это не повод падать, просто вернём то, что успели найти.
            }

            var slice = new List<UdpReceiveResult>();
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (!udp.Client.Poll(200_000, SelectMode.SelectRead)) break;
                    IPEndPoint remote = new(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref remote);
                    slice.Add(new UdpReceiveResult(data, remote));
                }
                catch (SocketException)
                {
                    break;
                }
            }

            foreach (var result in slice)
                ParseResponse(result, found);

            if (attempts >= 8) break;
            Thread.Sleep(150);
        }

        return found.Values.ToList();
    }

    private static void ParseResponse(UdpReceiveResult result, Dictionary<string, DiscoveredDevice> found)
    {
        byte[] data = result.Buffer;
        if (data.Length < 20) return;

        // Ответ начинается с 0x000055aa, затем идёт код команды udpNew (19)
        // или udp (20), за ним -- JSON с полями gwId, ip, version.
        uint prefix = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        if (prefix != 0x000055AA) return;

        int jsonStart = FindJsonStart(data);
        if (jsonStart < 0) return;

        string json = Encoding.UTF8.GetString(data, jsonStart, data.Length - jsonStart);
        int end = json.LastIndexOf('}');
        if (end < 0) return;
        json = json[..(end + 1)];

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string devId = GetString(root, "gwId") ?? GetString(root, "id") ?? "";
            if (devId.Length == 0) return;

            double version = GetDouble(root, "version") ?? 3.3;

            string ip = GetString(root, "ip")
                        ?? ((IPEndPoint)result.RemoteEndPoint).Address.ToString();

            found[devId] = new DiscoveredDevice
            {
                Id = devId,
                Address = ip,
                Version = version,
                IsBluetooth = false,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            // Мусорный ответ -- пропускаем.
        }
    }

    private static int FindJsonStart(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (data[i] == (byte)'{') return i;
        return -1;
    }

    private static string? GetString(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? GetDouble(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetDouble()
            : null;

    /// <summary>
    /// Ключ для широковещательного запроса. Он не секретный: это
    /// общеизвестная константа протокола Tuya, одинаковая у всех
    /// устройств и утилит.
    /// </summary>
    private static byte[] EncryptionKey() => MD5.HashData(new byte[]
    {
        0x79, 0x34, 0x38, 0x39, 0x30, 0x39, 0x33, 0x32,
        0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
    });

    private static byte[] BuildScanPayload(byte[] key)
    {
        // Идентификатор сообщения: стандартный набор байт, который
        // используют все инструменты для Tuya.
        byte[] messageId = MD5.HashData(new byte[]
        {
            0x79, 0x34, 0x38, 0x39, 0x30, 0x39, 0x33, 0x32, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
        });

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["from"] = "app",
            ["ip"] = LocalIp(),
        };

        byte[] encrypted = EncryptAes(Encoding.UTF8.GetBytes(body.ToJsonString()), key);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        int length = 28 + encrypted.Length;
        WriteBigEndian(w, (uint)length);       // длина всего пакета
        WriteBigEndian(w, 0x000055AAu);        // префикс
        WriteBigEndian(w, 0u);                 // версия протокола

        w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        w.Write(0x0000000Au);                  // команда: запрос обнаружения
        w.Write(encrypted);
        WriteBigEndian(w, 0x0000AA55u);        // суффикс
        w.Write(ComputeHmac(key, ms.ToArray()));

        return ms.ToArray();
    }

    private static byte[] EncryptAes(byte[] data, byte[] key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] ComputeHmac(byte[] key, byte[] data)
    {
        // HMAC считается без 4 байт суффикса, как в протоколе.
        return HMACSHA256.HashData(key, data.AsSpan(0, data.Length - 4));
    }

    private static void WriteBigEndian(BinaryWriter w, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        w.Write(buf);
    }

    private static string LocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException)
        {
            return "0.0.0.0";
        }
    }
}
