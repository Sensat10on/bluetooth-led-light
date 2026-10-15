using System.Buffers.Binary;

namespace OpenTuyaSync.Core.Ipc;

/// <summary>
/// Кадрирование сообщений поверх именованного канала. Формат: 4 байта
/// длины (little-endian), затем полезная нагрузка UTF-8 JSON.
/// </summary>
public static class PipeFraming
{
    public const int MaxMessageSize = 1 << 20;   // 1 МБ -- с запасом

    public static async Task WriteMessageAsync(
        Stream stream, IpcMessage message, CancellationToken ct = default)
    {
        byte[] payload = IpcJson.Serialize(message);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        // Одна запись на сообщение: два раздельных Write могут быть
        // перемешаны с другим потоком, пишущим в тот же канал.
        byte[] frame = new byte[4 + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, 4);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Читает одно сообщение. Возвращает null, если канал закрыт штатно.
    /// </summary>
    public static async Task<IpcMessage?> ReadMessageAsync(
        Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageSize)
            throw new InvalidDataException($"Некорректная длина сообщения: {length}");

        byte[] payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null;

        return IpcJson.Deserialize(payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return false;   // канал закрыт
            offset += read;
        }
        return true;
    }
}
