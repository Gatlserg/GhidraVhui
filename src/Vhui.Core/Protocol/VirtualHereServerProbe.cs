using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Vhui.Core.Protocol;

public sealed record ProtocolCapture(byte Type, uint DeviceAddress, int PayloadLength, string PayloadBase64);
public sealed record ServerIdentity(byte ProtocolVersion, string Name, string Serial, string Version);
public sealed record ServerProbeResult(string Host, int Port, ServerIdentity? Identity, IReadOnlyList<ProtocolCapture> Messages);

/// <summary>Информационные команды к серверу: описание, список устройств и барьер конца списка.</summary>
public static class VirtualHereServerProbe
{
    public static async Task<ServerProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > 65535) { throw new ArgumentOutOfRangeException(nameof(port)); }
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        var messages = new List<ProtocolCapture>();
        await VirtualHereFrameCodec.WriteAsync(stream, VirtualHereMessage.Create(0), cancellationToken).ConfigureAwait(false);
        VirtualHereMessage information = await ReadUntilAsync(stream, 1, messages, cancellationToken).ConfigureAwait(false);
        ServerIdentity? identity = null;
        if (information.Payload.Length >= 187)
        {
            byte[] bytes = information.Payload;
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(184));
            identity = new(bytes[0], ReadAscii(bytes, 1, 128), ReadAscii(bytes, 129, 37),
                $"{version & 255}.{version >> 8}.{bytes[186]}");
        }
        await VirtualHereFrameCodec.WriteAsync(stream, VirtualHereMessage.Create(9), cancellationToken).ConfigureAwait(false);
        await VirtualHereFrameCodec.WriteAsync(stream, VirtualHereMessage.Create(2), cancellationToken).ConfigureAwait(false);
        await ReadUntilAsync(stream, 17, messages, cancellationToken).ConfigureAwait(false);
        return new(host, port, identity, messages);
    }

    private static async Task<VirtualHereMessage> ReadUntilAsync(Stream stream, byte expected,
        List<ProtocolCapture> captures, CancellationToken token)
    {
        for (int index = 0; index < 512; index++)
        {
            VirtualHereMessage message = await VirtualHereFrameCodec.ReadAsync(stream, token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Сервер закрыл соединение до ожидаемого ответа.");
            captures.Add(new(message.Type, message.DeviceAddress, message.Payload.Length, Convert.ToBase64String(message.Payload)));
            if (message.Type == expected) { return message; }
        }
        throw new InvalidDataException("Превышено число сообщений при ожидании ответа сервера.");
    }

    private static string ReadAscii(byte[] bytes, int offset, int count)
    {
        ReadOnlySpan<byte> text = bytes.AsSpan(offset, count);
        int zero = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(zero >= 0 ? text[..zero] : text);
    }
}
