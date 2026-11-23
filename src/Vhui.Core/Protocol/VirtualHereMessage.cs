using System.Buffers.Binary;

namespace Vhui.Core.Protocol;

/// <summary>
/// Заголовок сообщения 33 байта. TokenA/B/C и Reserved сохраняются без
/// интерпретации: их назначение нельзя обобщать на все команды по одному обработчику.
/// </summary>
public sealed record VirtualHereMessage(byte Type, uint Reserved, ulong TokenA, uint TokenB,
    ulong TokenC, uint DeviceAddress, byte[] Payload)
{
    public const int HeaderSize = 33;
    public static VirtualHereMessage Create(byte type, byte[]? payload = null)
        => new(type, 0, 0, 0, 0, 0, payload ?? []);

    public byte[] ToBytes()
    {
        byte[] bytes = new byte[checked(HeaderSize + Payload.Length)];
        bytes[0] = Type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), Reserved);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(5), TokenA);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13), TokenB);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(17), TokenC);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(25), DeviceAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(29), (uint)Payload.Length);
        Payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    public static VirtualHereMessage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes[29..]) != bytes.Length - HeaderSize)
        {
            throw new InvalidDataException("Длина сообщения не совпадает с заголовком VirtualHere.");
        }
        return new(bytes[0], BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[5..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[13..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[17..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[25..]),
            bytes[HeaderSize..].ToArray());
    }
}
