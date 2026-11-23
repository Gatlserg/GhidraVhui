using System.Buffers.Binary;

namespace Vhui.Core.Protocol;

/// <summary>Декодирование LZ4 block, встроенного в ReceiveWireFrames (0x140748ac0).</summary>
public static class Lz4BlockDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outputLength);
        byte[] output = new byte[outputLength];
        int source = 0, destination = 0;
        while (source < input.Length)
        {
            byte token = input[source++];
            int literalLength = ReadLength(input, ref source, token >> 4);
            if (literalLength > input.Length - source || literalLength > output.Length - destination)
            {
                throw new InvalidDataException("Литералы LZ4 выходят за границы блока.");
            }
            input.Slice(source, literalLength).CopyTo(output.AsSpan(destination));
            source += literalLength;
            destination += literalLength;
            if (source == input.Length) { break; }
            if (input.Length - source < 2) { throw new InvalidDataException("Оборвано расстояние LZ4."); }
            int distance = BinaryPrimitives.ReadUInt16LittleEndian(input[source..]);
            source += 2;
            int matchLength = ReadLength(input, ref source, token & 15);
            if (matchLength > int.MaxValue - 4) { throw new InvalidDataException("Переполнение длины повтора LZ4."); }
            matchLength += 4;
            if (distance == 0 || distance > destination || matchLength > output.Length - destination)
            {
                throw new InvalidDataException("Некорректный словарный повтор LZ4.");
            }
            for (int index = 0; index < matchLength; index++)
            {
                output[destination] = output[destination - distance];
                destination++;
            }
        }
        if (destination != outputLength) { throw new InvalidDataException("Длина результата LZ4 не совпадает с кадром."); }
        return output;
    }

    private static int ReadLength(ReadOnlySpan<byte> input, ref int offset, int length)
    {
        if (length != 15) { return length; }
        byte extension;
        do
        {
            if (offset == input.Length) { throw new InvalidDataException("Оборвана длина LZ4."); }
            extension = input[offset++];
            if (length > int.MaxValue - extension) { throw new InvalidDataException("Переполнение длины LZ4."); }
            length += extension;
        } while (extension == 255);
        return length;
    }
}
