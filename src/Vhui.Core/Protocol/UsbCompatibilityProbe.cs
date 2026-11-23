using System.Buffers.Binary;
using System.Net.Sockets;

namespace Vhui.Core.Protocol;

public sealed record UsbProbeExchange(string Name, byte RequestType, byte ResponseType, string RequestBase64, string ResponseBase64, string PayloadBase64);
public sealed record UsbCompatibilityResult(uint Address, bool Captured, bool Released, IReadOnlyList<UsbProbeExchange> Exchanges);

/// <summary>Bounded differential probe: no vendor writes, no port power operations, always releases its own capture.</summary>
public static class UsbCompatibilityProbe
{
    public static async Task<UsbCompatibilityResult> RunAsync(string host, int port, ushort vid, ushort pid, CancellationToken token)
    {
        using var client = new TcpClient { NoDelay = true }; await client.ConnectAsync(host, port, token);
        using NetworkStream stream = client.GetStream();
        var exchanges = new List<UsbProbeExchange>();
        ulong sequence = 1; uint address = 0; bool captured = false, released = false;
        async Task<VirtualHereMessage> Read(byte type)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (await VirtualHereFrameCodec.ReadAsync(stream, deadline.Token) is { } message)
            {
                if (message.Type == 12) { await VirtualHereFrameCodec.WriteAsync(stream, message with { Type = 15 }, deadline.Token); continue; }
                if (message.Type == type) { return message; }
            }
            throw new EndOfStreamException();
        }
        async Task<VirtualHereMessage> Exchange(string name, byte type, byte response, byte[]? payload = null)
        {
            VirtualHereMessage request = VirtualHereMessage.Create(type, payload) with { DeviceAddress = address, TokenA = sequence++ };
            await VirtualHereFrameCodec.WriteAsync(stream, request, token);
            VirtualHereMessage reply = await Read(response);
            exchanges.Add(new(name, type, reply.Type, Convert.ToBase64String(request.ToBytes()), Convert.ToBase64String(reply.ToBytes()), Convert.ToBase64String(reply.Payload)));
            return reply;
        }
        await Exchange("server", 0, 1);
        await VirtualHereFrameCodec.WriteAsync(stream, VirtualHereMessage.Create(9), token);
        while (true)
        {
            VirtualHereMessage device = await Read(16);
            if (device.Payload.Length >= 136 && BinaryPrimitives.ReadUInt16LittleEndian(device.Payload) == vid
                && BinaryPrimitives.ReadUInt16LittleEndian(device.Payload.AsSpan(2)) == pid)
            { address = BinaryPrimitives.ReadUInt32LittleEndian(device.Payload.AsSpan(132)); break; }
        }
        try
        {
            VirtualHereMessage capture = await Exchange("capture", 0x19, 0x27);
            captured = BinaryPrimitives.ReadInt32LittleEndian(capture.Payload) == 1;
            if (!captured) { throw new IOException("Differential probe could not capture device: " + BinaryPrimitives.ReadInt32LittleEndian(capture.Payload)); }
            byte endpoint = 0;
            byte[] configuration = capture.Payload.AsSpan(23).ToArray(); bool alternateZero = false;
            for (int offset = 0; offset + 2 <= configuration.Length && configuration[offset] >= 2;)
            {
                int length = configuration[offset]; if (offset + length > configuration.Length) { break; }
                if (configuration[offset + 1] == 4 && length >= 9) { alternateZero = configuration[offset + 3] == 0; }
                if (configuration[offset + 1] == 5 && length >= 7 && alternateZero && (configuration[offset + 3] & 3) == 3)
                { endpoint = configuration[offset + 2]; break; }
                offset += length;
            }
            await Exchange("port-status", 0x14, 0x15);
            await Exchange("reset", 0x16, 0x17);
            byte[] extended = new byte[18]; extended[0] = 1; extended[1] = Math.Min(configuration[4], (byte)16);
            await Exchange("extended-reset", 0x33, 0x17, extended);
            if (endpoint != 0)
            {
                byte[] pipe = [endpoint, 0, 0, 0];
                await Exchange("clear-halt", 0x1a, 0x1b, pipe);
                await Exchange("reset-endpoint", 0x1c, 0x1d, pipe);
                await Exchange("abort-endpoint", 0x1e, 0x1f, pipe);
                await Exchange("compound-reset", 0x32, 0x20, pipe);
            }
            await Exchange("cancel-ack", 0x21, 0x22, new byte[8]);
            byte[] invalidPipe = new byte[13]; invalidPipe[0] = 0x8f; BinaryPrimitives.WriteInt32LittleEndian(invalidPipe.AsSpan(9), 1);
            await Exchange("invalid-pipe-12", 0x12, 0x13, invalidPipe);
            BinaryPrimitives.WriteInt32LittleEndian(invalidPipe.AsSpan(5), 1234);
            await Exchange("invalid-pipe-4A", 0x4a, 0x13, invalidPipe);
            byte[] control = new byte[16]; new byte[] { 0x80, 6, 0, 0xff, 0, 0, 1, 0 }.CopyTo(control, 8);
            await Exchange("invalid-descriptor", 0x0d, 0x0e, control);
            await Exchange("unsupported-worker-6", 0x46, 0x47, new byte[8]);
            await Exchange("unsupported-worker-7", 0x48, 0x49, new byte[8]);
        }
        finally
        {
            if (captured)
            {
                // Use a separate bounded cleanup deadline even if the caller's operation timed out.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await VirtualHereFrameCodec.WriteAsync(stream, VirtualHereMessage.Create(0x23) with { DeviceAddress = address, TokenA = sequence++ }, cleanup.Token);
                while (await VirtualHereFrameCodec.ReadAsync(stream, cleanup.Token) is { } reply)
                {
                    if (reply.Type == 12) { await VirtualHereFrameCodec.WriteAsync(stream, reply with { Type = 15 }, cleanup.Token); }
                    if (reply.Type == 7) { released = BinaryPrimitives.ReadInt32LittleEndian(reply.Payload) == 1; break; }
                }
            }
        }
        return new(address, captured, released, exchanges);
    }
}
