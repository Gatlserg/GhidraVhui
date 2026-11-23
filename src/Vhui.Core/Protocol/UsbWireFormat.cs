using System.Buffers.Binary;
using System.Text;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

/// <summary>Layouts confirmed against native 4.8.8 capture and completion handlers.</summary>
public static class UsbWireFormat
{
    public static VirtualHereMessage CaptureReply(VirtualHereMessage request, UsbPhysicalDevice device, string serverSerial, int status)
    {
        byte[] payload = new byte[0x10156];
        BinaryPrimitives.WriteInt32LittleEndian(payload, status);
        payload[4] = device.Speed;
        device.DeviceDescriptor.CopyTo(payload, 5);
        device.ConfigurationDescriptor.CopyTo(payload, 23);
        WriteString(payload, 0x10016, 128, device.Product ?? "");
        WriteString(payload, 0x10096, 128, device.SerialNumber ?? "");
        WriteString(payload, 0x10116, 64, serverSerial);
        return request with { Type = 0x27, Reserved = 0, Payload = payload };
    }

    public static (byte[] Setup, byte[] Buffer, bool Input) ParseControl(VirtualHereMessage request)
    {
        ReadOnlySpan<byte> payload = request.Payload;
        if (payload.Length < 16) { throw new InvalidDataException("Control request header is truncated."); }
        byte[] setup = payload.Slice(8, 8).ToArray();
        int length = BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6));
        bool input = (setup[0] & 128) != 0;
        if (!input && payload.Length - 16 != length) { throw new InvalidDataException("Control OUT length does not match setup packet."); }
        if (input && payload.Length != 16) { throw new InvalidDataException("Control IN request contains unexpected data."); }
        byte[] buffer = new byte[length];
        if (!input) { payload[16..].CopyTo(buffer); }
        return (setup, buffer, input);
    }

    public static VirtualHereMessage ControlReply(VirtualHereMessage request, byte[] setup, byte[] buffer, bool input, int status, int actual)
    {
        if ((uint)actual > buffer.Length) { throw new InvalidDataException("Invalid control completion length."); }
        byte[] payload = new byte[16 + (input ? buffer.Length : 0)];
        setup.CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), status);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), actual);
        if (input) { buffer.CopyTo(payload, 16); }
        return request with { Type = 14, Reserved = 0, Payload = payload };
    }

    public static VirtualHereMessage StatusReply(VirtualHereMessage request, byte type, int status, int length = 4)
    {
        byte[] payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, status);
        return request with { Type = type, Reserved = 0, Payload = payload };
    }

    public static VirtualHereMessage IsochronousReply(VirtualHereMessage request, byte[] buffer, byte[] packets, bool input, int status, int actual)
    {
        if (packets.Length % 12 != 0 || (uint)actual > buffer.Length) { throw new InvalidDataException("Invalid isochronous completion."); }
        bool compact = request.Type == 0x4d && status >= 0;
        int dataLength = input ? (compact ? actual : buffer.Length) : 0;
        byte[] payload = new byte[12 + packets.Length + dataLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, status < 0 ? status : actual);
        // Native Windows completion reports frame 0 and no aggregate error count.
        packets.CopyTo(payload, 12);
        if (input && !compact) { buffer.CopyTo(payload, 12 + packets.Length); }
        if (input && compact)
        {
            int source = 0, target = 12 + packets.Length;
            for (int index = 0; index < packets.Length; index += 12)
            {
                int requested = BinaryPrimitives.ReadInt32LittleEndian(packets.AsSpan(index));
                int completed = BinaryPrimitives.ReadInt32LittleEndian(packets.AsSpan(index + 4));
                if (requested < 0 || requested > buffer.Length - source || completed < 0 || completed > requested || completed > payload.Length - target)
                { throw new InvalidDataException("Invalid compact isochronous completion bounds."); }
                buffer.AsSpan(source, completed).CopyTo(payload.AsSpan(target));
                source += requested; target += completed;
            }
            if (target != payload.Length) { throw new InvalidDataException("Isochronous total length does not match its packets."); }
        }
        return request with { Type = compact ? (byte)0x4c : (byte)0x25, Reserved = 0, Payload = payload };
    }

    private static void WriteString(byte[] bytes, int offset, int capacity, string value)
        => Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), bytes.AsSpan(offset, capacity - 1), true, out _, out _, out _);
}
