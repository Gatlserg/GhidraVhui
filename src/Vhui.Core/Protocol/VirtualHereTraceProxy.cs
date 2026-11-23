using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Vhui.Core.Protocol;

/// <summary>Development transport recorder. The native server executes USB operations.</summary>
public static class VirtualHereTraceProxy
{
    public static async Task RunAsync(int listenPort, string host, int serverPort, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(directory, "trace.jsonl"), false) { AutoFlush = true });
        var listener = new TcpListener(IPAddress.Loopback, listenPort);
        var connections = new List<Task>();
        long sequence = 0, stored = 0;
        int connection = 0;
        listener.Start();
        try
        {
            Console.Error.WriteLine($"Trace proxy 127.0.0.1:{listenPort} -> {host}:{serverPort}");
            while (!token.IsCancellationRequested)
            {
                TcpClient incoming = await listener.AcceptTcpClientAsync(token);
                int id = ++connection;
                connections.Add(ForwardConnectionAsync(incoming, id));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { listener.Stop(); await Task.WhenAll(connections); }

        async Task ForwardConnectionAsync(TcpClient incoming, int id)
        {
            using (incoming)
            using (var outgoing = new TcpClient { NoDelay = true })
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                incoming.NoDelay = true;
                try
                {
                    await outgoing.ConnectAsync(host, serverPort, lifetime.Token);
                    using NetworkStream left = incoming.GetStream();
                    using NetworkStream right = outgoing.GetStream();
                    Task requests = PumpAsync(left, right, "client");
                    Task responses = PumpAsync(right, left, "server");
                    await Task.WhenAny(requests, responses);
                    lifetime.Cancel();
                    await Task.WhenAll(requests, responses);

                    async Task PumpAsync(Stream input, Stream output, string direction)
                    {
                        try
                        {
                            while (await VirtualHereFrameCodec.ReadAsync(input, lifetime.Token) is { } message)
                            {
                                long index = Interlocked.Increment(ref sequence);
                                string? filename = null;
                                byte[] bytes = message.ToBytes();
                                if (Interlocked.Add(ref stored, bytes.Length) <= 128 * 1024 * 1024)
                                {
                                    filename = $"{index:D7}-{id}-{direction}-{message.Type:X2}.bin";
                                    await File.WriteAllBytesAsync(Path.Combine(directory, filename), bytes, lifetime.Token);
                                }
                                log.WriteLine(JsonSerializer.Serialize(new { Sequence = index, Connection = id,
                                    Direction = direction, message.Type, message.DeviceAddress, message.TokenA, message.TokenB,
                                    message.TokenC, PayloadLength = message.Payload.Length, File = filename }));
                                await VirtualHereFrameCodec.WriteAsync(output, message, lifetime.Token);
                            }
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                        catch (IOException) { }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception exception) when (exception is IOException or SocketException or InvalidDataException)
                {
                    log.WriteLine(JsonSerializer.Serialize(new { Connection = id, Error = exception.Message }));
                }
            }
        }
    }
}
