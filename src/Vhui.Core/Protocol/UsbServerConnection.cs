using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

internal sealed class UsbServerConnection(UsbServerHost host, IPAddress address)
{
    public Guid Id { get; } = Guid.NewGuid();
    private readonly ConcurrentDictionary<uint, DeviceConnection> devices = [];
    private readonly Channel<VirtualHereMessage> outbound = Channel.CreateBounded<VirtualHereMessage>(new BoundedChannelOptions(512) { SingleReader = true });
    private CancellationTokenSource? stop;
    private long lastKeepAlive = Stopwatch.GetTimestamp();
    private string Serial => host.Options.ServerId.ToString("D");
    public void DeviceChanged(UsbDeviceEntry entry, bool includeOwner = false)
    {
        if (!entry.Present && devices.TryGetValue(entry.Address, out DeviceConnection? session)) { session.Cancel(); }
        if (includeOwner || entry.Owner?.ConnectionId != Id) { Send(entry.Information(VirtualHereMessage.Create(9), host.Options.Enabled("HideClientInfo"))); }
    }
    private void Send(VirtualHereMessage message)
    {
        if (!outbound.Writer.TryWrite(message))
        { try { stop?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    internal VirtualHereMessage ServerInformation(VirtualHereMessage request)
    {
        UsbPhysicalDevice device = host.Registry.Devices.FirstOrDefault()?.Device ?? new("", 1, 0, 0, 0, false, null, null, null, [], []);
        VirtualHereMessage response = VirtualHereServer.BuildServerInformation(request, device, Id, host.ShareUsb);
        response.Payload.AsSpan(1, 128).Clear(); WriteString(response.Payload, 1, 128, host.Options.ServerName);
        response.Payload.AsSpan(129, 37).Clear(); WriteString(response.Payload, 129, 37, Serial);
        BinaryPrimitives.WriteUInt16LittleEndian(response.Payload.AsSpan(166), ushort.MaxValue); // Capacity; no licensing checks.
        response.Payload[187] = host.Options.Enabled("RemoteAdmin") ? (byte)1 : (byte)0;
        return response;
    }
    public async Task RunAsync(Stream stream, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop = lifetime;
        var context = new Dictionary<string, string> { ["CLIENT_ID"] = Id.ToString("D"), ["CLIENT_IP"] = address.ToString(), ["CLIENT_HOSTNAME"] = address.ToString(), ["CONNECTION_ID"] = Id.ToString("D") };
        Task writer = WriteAsync(stream, lifetime);
        Task heartbeat = Task.CompletedTask;
        try
        {
            await host.Options.HookAsync(host.Options.Configuration.GetString("onClientConnect", expandEnvironment: false), context, lifetime.Token);
            Interlocked.Exchange(ref lastKeepAlive, Stopwatch.GetTimestamp());
            heartbeat = HeartbeatAsync(lifetime);
            while (await VirtualHereFrameCodec.ReadAsync(stream, lifetime.Token) is { } request)
            {
                Trace("Received", request);
                switch (request.Type)
                {
                    case 0: Send(ServerInformation(request)); continue;
                    case 9:
                        foreach (UsbDeviceEntry entry in host.Registry.Devices) { Send(entry.Information(request, host.Options.Enabled("HideClientInfo"))); }
                        continue;
                    case 2: Send(request with { Type = 17, Payload = [] }); continue;
                    case 10: Send(request with { Type = 11, Payload = [] }); continue;
                    case 15: Interlocked.Exchange(ref lastKeepAlive, Stopwatch.GetTimestamp()); continue;
                }
                if (await AdminAsync(request, lifetime.Token)) { continue; }
                UsbDeviceEntry? target = host.Registry.Find(request.DeviceAddress);
                if (host.ShareUsb && WindowsUsbProtocolSession.Supports(request.Type) && target is not null)
                {
                    DeviceConnection session = devices.GetOrAdd(target.Address, _ => new(new WindowsUsbProtocolSession(target,
                        new UsbOwner(Id, address, Id.ToString("D"), address.ToString()), Serial, host.Options, host.Capture, host.Log), lifetime.Token, host));
                    if (session.Stopped)
                    {
                        await session.StopAsync(); devices.TryRemove(target.Address, out _);
                        if (!target.Present) { continue; }
                        session = devices.GetOrAdd(target.Address, _ => new(new WindowsUsbProtocolSession(target,
                            new UsbOwner(Id, address, Id.ToString("D"), address.ToString()), Serial, host.Options, host.Capture, host.Log), lifetime.Token, host));
                    }
                    _ = session.Execute(request, Send);
                    continue;
                }
                // Original dispatcher drops unregistered opcodes; no invented response frame.
                host.Event("UnsupportedRequest", new { request.Type, request.DeviceAddress });
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            foreach (DeviceConnection session in devices.Values) { session.Cancel(); }
            await Task.WhenAll(devices.Values.Select(value => value.StopAsync()));
            outbound.Writer.TryComplete();
            await Task.WhenAll(writer, heartbeat);
            try { await host.Options.HookAsync(host.Options.Configuration.GetString("onClientDisconnect", expandEnvironment: false), context, CancellationToken.None); }
            catch (Exception error) { host.Event("DisconnectHookFailed", error.Message); }
            stop = null;
        }
    }
    private async Task WriteAsync(Stream stream, CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (VirtualHereMessage message in outbound.Reader.ReadAllAsync(lifetime.Token))
            { await VirtualHereFrameCodec.WriteAsync(stream, message, lifetime.Token); Trace("Sent", message); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { lifetime.Cancel(); }
    }
    private async Task HeartbeatAsync(CancellationTokenSource lifetime)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Min(host.Options.PingInterval, host.Options.PingTimeout)));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastKeepAlive)).TotalSeconds > host.Options.PingTimeout)
                { host.Event("PingTimeout", Id); lifetime.Cancel(); break; }
                byte[] timestamp = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(timestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                Send(VirtualHereMessage.Create(12, timestamp));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task<bool> AdminAsync(VirtualHereMessage request, CancellationToken token)
    {
        if (request.Type == 0x4e)
        { Send(VirtualHereMessage.Create(0x4f, Encoding.UTF8.GetBytes(host.Options.Configuration.GetString("IgnoredDevices", "") + "\0"))); return true; }
        if (request.Type == 0x51)
        {
            byte[] duration = new byte[8];
            DateTimeOffset? start = host.Registry.Find(request.DeviceAddress)?.CapturedAt;
            if (start is null) { return true; }
            BinaryPrimitives.WriteInt64LittleEndian(duration, (long)(DateTimeOffset.UtcNow - start.Value).TotalSeconds);
            Send(request with { Type = 0x52, Payload = duration }); return true;
        }
        if (request.Type is not (0x28 or 0x2a or 0x2f or 0x3e or 0x3f or 0x40)) { return false; }
        // Native administrative-password handshake remains unclassified. Do not silently accept that setting.
        bool permitted = host.Options.Enabled("RemoteAdmin") &&
            UsbServerOptions.MatchesAddress(host.Options.Configuration.GetString("AdminAllowedClients", "127.0.0.1,::1")!, address);
        if (!permitted) { host.Event("AdminDenied", new { Id, request.Type }); return true; }
        if (request.Payload.Length > 4096) { throw new InvalidDataException("Administrative value is too long."); }
        string text = Encoding.UTF8.GetString(request.Payload).TrimEnd('\0');
        if (text.Contains('\r') || text.Contains('\n')) { throw new InvalidDataException("Administrative value contains a newline."); }
        if (request.Type == 0x28) { host.Options.Configuration.Set("ServerName", text); Send(ServerInformation(request)); }
        else if (request.Type == 0x2a) { host.Event("UnsupportedRequest", new { request.Type }); return true; }
        else if (request.Type == 0x2f)
        {
            int index = text.IndexOf('=');
            if (index > 0)
            {
                string key = text[..index], value = text[(index + 1)..];
                if (value.Equals("REMOVE", StringComparison.OrdinalIgnoreCase)) { host.Options.Configuration.Remove(key); }
                else if (value.Length == 0) { host.Options.Configuration.Set(key, ""); }
                else { host.Event("CustomEventRefused", key); return true; }
            }
        }
        else if (request.Type == 0x40)
        {
            string key = host.Options.Configuration.GetString("SSLCert") is { Length: > 0 } ? "SSLReverseClients" : "ReverseClients";
            Send(VirtualHereMessage.Create(0x41, Encoding.UTF8.GetBytes(host.Options.Configuration.GetString(key, "") + "\0")));
        }
        else { host.Event("UnsupportedRequest", new { request.Type }); return true; } // Reverse editing packet layout is not yet recovered.
        host.SaveConfiguration();
        await Task.CompletedTask; return true;
    }
    private void Trace(string direction, VirtualHereMessage message)
    {
        if (!host.Options.Enabled("TraceMsgs")) { return; }
        bool secret = message.Type is 0x42 or 0x13 or 0x25 or 0x4c or 0x12 or 0x4a or 0x24 or 0x4d;
        host.Log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Event = direction, Peer = address.ToString(), message.Type,
            Endpoint = message.Type is 0x12 or 0x4a or 0x24 or 0x4d && message.Payload.Length > 0 ? (int?)message.Payload[0] : null,
            message.DeviceAddress, PayloadLength = message.Payload.Length, PayloadBase64 = !secret && message.Payload.Length <= 4096 ? Convert.ToBase64String(message.Payload) : null,
            MessageBase64 = !secret && message.Payload.Length <= 4096 ? Convert.ToBase64String(message.ToBytes()) : null, DataOmitted = secret || message.Payload.Length > 4096 }));
    }
    private static void WriteString(byte[] bytes, int offset, int length, string value)
        => Encoding.UTF8.GetEncoder().Convert(value, bytes.AsSpan(offset, length - 1), true, out _, out _, out _);

    private sealed class DeviceConnection(WindowsUsbProtocolSession session, CancellationToken parent, UsbServerHost host)
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
        private readonly object sync = new();
        private readonly HashSet<Task> pending = [];
        private Task? disposing;
        private Task barrier = Task.CompletedTask;
        private CancellationTokenSource? operations;
        public bool Stopped => stop.IsCancellationRequested;
        public void Cancel() { try { stop.Cancel(); } catch (ObjectDisposedException) { } session.CancelPending(); _ = StopAsync(); }
        public Task Execute(VirtualHereMessage request, Action<VirtualHereMessage> send)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task before;
            CancellationToken token;
            CancellationTokenSource? retired = null;
            lock (sync)
            {
                if (Stopped) { return Task.CompletedTask; }
                if (pending.Count >= 256) { throw new InvalidDataException("Too many outstanding USB requests."); }
                operations ??= CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                before = barrier;
                if (request.Type is 0x19 or 0x23)
                {
                    if (request.Type == 0x23)
                    {
                        retired = operations;
                        operations = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        retired.Cancel(); session.CancelPending();
                    }
                    before = Task.WhenAll(pending);
                    barrier = completion.Task;
                }
                token = operations.Token;
                pending.Add(completion.Task);
            }
            _ = Run(); return completion.Task;
            async Task Run()
            {
                try
                {
                    await before;
                    foreach (VirtualHereMessage reply in await session.HandleAsync(request, token)) { send(reply); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) { host.Event("UsbRequestFailed", new { request.Type, Error = error.ToString() }); stop.Cancel(); session.CancelPending(); }
                finally { retired?.Dispose(); completion.TrySetResult(); lock (sync) { pending.Remove(completion.Task); } if (Stopped) { _ = StopAsync(); } }
            }
        }
        public async Task DrainAsync()
        {
            session.CancelPending();
            Task[] tasks; lock (sync) { tasks = pending.ToArray(); }
            await Task.WhenAll(tasks);
        }
        public Task StopAsync() { lock (sync) { return disposing ??= DisposeCore(); } }
        private async Task DisposeCore()
        {
            stop.Cancel(); await DrainAsync();
            try { await session.DisposeAsync(); } catch (Exception error) { host.Event("DeviceRestoreFailed", error.ToString()); }
            operations?.Dispose();
        }
    }
}
