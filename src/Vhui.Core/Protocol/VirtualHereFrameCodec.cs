using System.Buffers.Binary;

namespace Vhui.Core.Protocol;

/// <summary>TCP-кадр: две длины UInt32 LE, затем обычное или LZ4-сжатое сообщение.</summary>
public static class VirtualHereFrameCodec
{
    public const int MaximumMessageLength = 50 * 1024 * 1024;

    public static async Task<VirtualHereMessage?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[8];
        int first = await input.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (first == 0) { return null; }
        await input.ReadExactlyAsync(header.AsMemory(first), cancellationToken).ConfigureAwait(false);
        uint compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (compressedLength > MaximumMessageLength || messageLength is < VirtualHereMessage.HeaderSize or > MaximumMessageLength)
        {
            throw new InvalidDataException("Длина кадра VirtualHere выходит за допустимые границы.");
        }
        int wireLength = (int)(compressedLength == 0 ? messageLength : compressedLength);
        byte[] bytes = new byte[wireLength];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (compressedLength != 0) { bytes = Lz4BlockDecoder.Decode(bytes, (int)messageLength); }
        return VirtualHereMessage.Parse(bytes);
    }

    public static async Task WriteAsync(Stream output, VirtualHereMessage message, CancellationToken cancellationToken = default)
    {
        byte[] bytes = message.ToBytes();
        if (bytes.Length > MaximumMessageLength) { throw new InvalidDataException("Слишком большое сообщение VirtualHere."); }
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)bytes.Length);
        // Нулевая первая длина означает несжатый кадр; этот режим поддерживает оригинал.
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
