using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTuyaSync.Providers.Tuya;

/// <summary>
/// Протокол Tuya версий 3.1-3.5 поверх одного долгоживущего TCP-соединения.
///
/// Реализация повторяет формат кадров, который использует tinytuya
/// (Python-версия проекта) -- он проверен на реальной лампе Ledvance:
///
///   кадр:  4 байта длины (big-endian) | 3 байта 00 00 55 | payload | 00 00 aa 55 | hmac(32)
///   payload: команда(4) | seq(4) | данные(AES-ECB) | crc32(4)
///
/// Для версий 3.4+ ключ шифрования и IV выводятся из Local Key через MD5,
/// а весь кадр дополнительно подписывается HMAC-SHA256.
///
/// ВАЖНО: раздел про версии 3.4+ выведен из формата tinytuya, а не из
/// официальной документации Tuya (её не существует). Если лампа
/// отказывается отвечать, в первую очередь проверяйте версию протокола в
/// настройках устройства: 3.1/3.2/3.3 и 3.4/3.5 отличаются именно
/// криптографией.
/// </summary>
public sealed class TuyaClient : IDisposable
{
    private const int HeaderSize = 4;
    private const int PrefixSize = 3;
    private const int SuffixSize = 4;
    private const int HmacSize = 32;
    private const uint Suffix = 0x0000AA55;
    private static readonly byte[] Prefix = { 0x00, 0x00, 0x55 };

    private const byte CommandControl = 7;
    private const byte CommandStatus = 8;
    private const byte CommandHeartbeat = 9;
    private const byte CommandDpQuery = 10;
    private const byte CommandDpQueryNew = 13;

    private readonly string _devId;
    private readonly string _ip;
    private readonly string _localKey;
    private readonly double _version;

    private readonly object _sync = new();
    private System.Net.Sockets.TcpClient? _client;
    private NetworkStream? _stream;
    private uint _sequence;

    public TuyaClient(string devId, string ip, string localKey, double version)
    {
        _devId = devId;
        _ip = ip;
        _localKey = localKey;
        _version = version;
    }

    public bool UseHmac => _version >= 3.4;

    /// <summary>Ключ шифрования: для 3.4+ это MD5(Local Key).</summary>
    private byte[] EncryptionKey
    {
        get
        {
            byte[] raw = Encoding.UTF8.GetBytes(_localKey);
            return UseHmac ? MD5.HashData(raw) : raw;
        }
    }

    public void Connect(int timeoutSeconds = 3)
    {
        lock (_sync)
        {
            CloseInternal();

            var client = new System.Net.Sockets.TcpClient
            {
                NoDelay = true,   // управление светом чувствительно к задержке
            };

            if (!client.ConnectAsync(_ip, 6668).Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                client.Dispose();
                throw new TimeoutException($"Не удалось подключиться к {_ip}:6668 за {timeoutSeconds} с");
            }

            _client = client;
            _stream = client.GetStream();
            _stream.ReadTimeout = timeoutSeconds * 1000;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _client?.Connected == true && _stream != null;
            }
        }
    }

    /// <summary>
    /// Отправляет набор состояний (dps) на устройство. Значения -- сырой
    /// JSON: числа и строки, как их понимает Tuya.
    /// </summary>
    public JsonObject SendDps(IDictionary<string, object> dps, bool nowait = true)
    {
        var dpsNode = new JsonObject();
        foreach (var kv in dps)
            dpsNode[kv.Key] = JsonValue.Create(kv.Value);

        var payload = new JsonObject
        {
            ["devId"] = _devId,
            ["uid"] = "",
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["dps"] = dpsNode,
        };

        byte[] encoded = Encode(CommandControl, Encoding.UTF8.GetBytes(payload.ToJsonString()));

        lock (_sync)
        {
            if (_stream == null) throw new InvalidOperationException("Устройство не подключено");
            _stream.Write(encoded, 0, encoded.Length);
            _stream.Flush();

            if (nowait) return new JsonObject();

            var response = ReadFrame();
            return response ?? new JsonObject();
        }
    }

    /// <summary>Запрашивает текущее состояние (dp 20, 21, 22, 24, 25).</summary>
    public JsonObject? QueryStatus()
    {
        byte[] encoded = Encode(CommandDpQueryNew, ReadOnlySpan<byte>.Empty);

        lock (_sync)
        {
            if (_stream == null) return null;
            _stream.Write(encoded, 0, encoded.Length);
            _stream.Flush();
            return ReadFrame();
        }
    }

    /// <summary>
    /// Читает один кадр ответа, пропуская heartbeat-запросы устройства
    /// (их нужно подтверждать, иначе лампа разрывает соединение).
    /// </summary>
    private JsonObject? ReadFrame()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            byte[]? frame = ReadExactOrNull(HeaderSize);
            if (frame == null) return null;

            int length = BinaryPrimitives.ReadInt32BigEndian(frame);
            if (length < PrefixSize + SuffixSize || length > 4096)
                throw new InvalidDataException($"Некорректная длина кадра Tuya: {length}");

            byte[] body = new byte[length];
            if (ReadExactOrNull(body, body.Length) == null) return null;

            int payloadSize = length - PrefixSize - SuffixSize - (UseHmac ? HmacSize : 0);
            if (payloadSize < 0) return null;

            byte[] payload = new byte[payloadSize];
            Array.Copy(body, PrefixSize, payload, 0, payloadSize);

            byte command = payload.Length > 0 ? payload[0] : (byte)0;

            if (command == CommandHeartbeat)
            {
                // Подтверждаем heartbeat пустым ответом -- так же делает
                // tinytuya, иначе лампа закрывает соединение через ~10 с.
                var ack = Encode(CommandHeartbeat, ReadOnlySpan<byte>.Empty);
                _stream!.Write(ack, 0, ack.Length);
                _stream.Flush();
                continue;
            }

            if (command is CommandControl or CommandStatus or CommandDpQuery or CommandDpQueryNew)
            {
                if (payloadSize <= 4) return new JsonObject();

                byte[] encrypted = new byte[payloadSize - 4];
                Array.Copy(payload, 4, encrypted, 0, encrypted.Length);

                byte[] clear = Decrypt(encrypted);
                string json = Encoding.UTF8.GetString(clear);
                int end = json.LastIndexOf('}');
                if (end >= 0) json = json[..(end + 1)];

                try
                {
                    return JsonNode.Parse(json) as JsonObject;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private byte[]? ReadExactOrNull(int count)
    {
        byte[] buffer = new byte[count];
        return ReadExactOrNull(buffer, count);
    }

    private byte[]? ReadExactOrNull(byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = _stream!.Read(buffer, offset, count - offset);
            }
            catch (IOException)
            {
                return null;   // таймаут или разрыв -- вызывающий переподключится
            }

            if (read <= 0) return null;
            offset += read;
        }
        return buffer;
    }

    /// <summary>Собирает полный кадр протокола.</summary>
    private byte[] Encode(byte command, ReadOnlySpan<byte> data)
    {
        byte[] payload = BuildPayload(command, data);

        int total = HeaderSize + PrefixSize + payload.Length + SuffixSize + (UseHmac ? HmacSize : 0);
        byte[] frame = new byte[total];

        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), total - HeaderSize);
        Prefix.CopyTo(frame.AsSpan(4, PrefixSize));
        payload.CopyTo(frame.AsSpan(4 + PrefixSize));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(total - SuffixSize - (UseHmac ? HmacSize : 0), 4), Suffix);

        if (UseHmac)
        {
            byte[] key = Encoding.UTF8.GetBytes(_localKey);
            byte[] mac = HMACSHA256.HashData(key, frame.AsSpan(0, total - HmacSize));
            mac.CopyTo(frame.AsSpan(total - HmacSize));
        }

        return frame;
    }

    private byte[] BuildPayload(byte command, ReadOnlySpan<byte> data)
    {
        _sequence++;

        byte[] body = data.Length == 0 ? Array.Empty<byte>() : Encrypt(data.ToArray());

        byte[] payload = new byte[1 + 4 + body.Length + 4];
        payload[0] = command;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), _sequence);
        body.CopyTo(payload.AsSpan(5));

        uint crc = Crc32.Compute(data);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5 + body.Length, 4), crc);

        return payload;
    }

    private byte[] Encrypt(byte[] plain)
    {
        byte[] key = EncryptionKey;
        using var aes = Aes.Create();
        aes.Key = UseHmac ? key[..16] : key[..16];
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        byte[] iv = UseHmac ? MD5.HashData(plain) : key[..16];
        using var enc = aes.CreateEncryptor(aes.Key, iv);
        return enc.TransformFinalBlock(plain, 0, plain.Length);
    }

    private byte[] Decrypt(byte[] cipher)
    {
        byte[] key = EncryptionKey;
        using var aes = Aes.Create();
        aes.Key = key[..16];
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;

        byte[] iv = UseHmac ? MD5.HashData(cipher) : key[..16];
        using var dec = aes.CreateDecryptor(aes.Key, iv);
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            CloseInternal();
        }
    }

    private void CloseInternal()
    {
        try { _stream?.Dispose(); } catch (Exception) { }
        try { _client?.Dispose(); } catch (Exception) { }
        _stream = null;
        _client = null;
    }
}
