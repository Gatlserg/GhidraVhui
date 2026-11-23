using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

/// <summary>One registry and owner table shared by TCP, TLS, and outgoing reverse sessions.</summary>
public sealed class UsbServerHost
{
    internal UsbServerOptions Options { get; }
    public UsbDeviceRegistry Registry { get; }
    internal bool ShareUsb { get; }
    internal TextWriter Log { get; }
    internal Func<UsbPhysicalDevice, CancellationToken, Task<IUsbDeviceBackend>> Capture { get; }
    private readonly Func<UsbInventoryResult> inventory;
    private readonly bool nativeBackend;
    private readonly ConcurrentDictionary<Guid, UsbServerConnection> connections = [];
    private readonly ConcurrentDictionary<long, Task> running = [];
    private readonly object configSync = new();
    private long sequence;

    public void NotifyDeviceNameChanged(UsbPhysicalDevice device)
    {
        UsbDeviceEntry? entry = Registry.Devices.FirstOrDefault(value => UsbDeviceRegistry.Key(value.Device).Equals(UsbDeviceRegistry.Key(device), StringComparison.OrdinalIgnoreCase));
        if (entry is null) { return; }
        foreach (UsbServerConnection connection in connections.Values) { connection.DeviceChanged(entry, includeOwner: true); }
    }

    public UsbServerHost(UsbServerOptions options, TextWriter log, Func<UsbInventoryResult>? enumerate = null,
        Func<UsbPhysicalDevice, CancellationToken, Task<IUsbDeviceBackend>>? capture = null, bool shareUsb = true,
        Func<UsbPhysicalDevice, bool>? publish = null)
    {
        Options = options; Log = TextWriter.Synchronized(log); ShareUsb = shareUsb; nativeBackend = capture is null;
        Registry = new(options, publish);
        inventory = enumerate ?? (() => OperatingSystem.IsWindows() ? WindowsUsbInventory.Enumerate() : throw new PlatformNotSupportedException());
        Capture = capture ?? (async (device, token) => OperatingSystem.IsWindows()
            ? await WinUsbDeviceLease.CaptureAsync(device, token) : throw new PlatformNotSupportedException());
        Registry.Changed += entry =>
        {
            foreach (UsbServerConnection connection in connections.Values) { connection.DeviceChanged(entry); }
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var security = ServerTlsConfiguration.Load(Options);
        var listeners = new List<TcpListener>();
        var workers = new List<Task>();
        MdnsRegistration? mdns = null;
        try
        {
            await Task.Run(Refresh, stop.Token);
            void Listen(int port, bool tls)
            {
                var listener = new TcpListener(Options.BindAddress, port);
                listener.Start(); listeners.Add(listener);
                Event("Listening", new { Host = Options.BindAddress.ToString(), Port = port, Tls = tls, Devices = Registry.Devices.Count });
                workers.Add(AcceptAsync(listener, tls, security, stop.Token));
            }
            // SSLCert selects TLS mode, just as in the native server. No plaintext bypass for mTLS.
            if (security is null) { Listen(Options.Port, false); }
            else { Listen(Options.Number("SSLPort", 7574, 1, 65535), true); }
            if (Options.Enabled("UseAVAHI", true) && !IPAddress.IsLoopback(Options.BindAddress) && OperatingSystem.IsWindows())
            {
                mdns = new MdnsRegistration(Options, security is not null, Log);
                mdns.Register();
            }
            workers.Add(ScanAsync(stop.Token));
            workers.Add(ReverseAsync(security, stop.Token));
            Task ended = await Task.WhenAny(workers);
            await ended;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            foreach (TcpListener listener in listeners) { listener.Stop(); }
            if (OperatingSystem.IsWindows()) { mdns?.Dispose(); }
            Exception? shutdownFailure = null;
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { shutdownFailure = error; }
            // Even a failed scanner must wait for device I/O and driver restoration.
            // Otherwise RunAsync can finish while a capture/restore is still in flight.
            try { await Task.WhenAll(running.Values); }
            finally { SaveConfiguration(); }
            if (shutdownFailure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownFailure).Throw(); }
        }
    }
    private void Refresh()
    {
        if (nativeBackend && ShareUsb && OperatingSystem.IsWindows())
        {
            // A disconnected device can be restored on a later scan after replugging.
            // Its failure must not prevent unrelated devices from being served.
            WinUsbDeviceLease.RecoverAbandonedLeases((path, error) => Event("UsbRecoveryFailed", new { Path = path, Error = error.Message }));
        }
        UsbInventoryResult result = inventory();
        // Errors on an unrelated hub/port must not hide successfully enumerated devices.
        if (result.Errors.Count != 0) { Event("InventoryErrors", result.Errors); }
        Registry.Refresh(result.Devices, result.Errors);
    }
    private async Task ScanAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Options.ScanInterval));
        while (await timer.WaitForNextTickAsync(token)) { await Task.Run(Refresh, token); }
    }
    private async Task AcceptAsync(TcpListener listener, bool tls, ServerTlsConfiguration? security, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(token);
            StartConnection(client, tls, security, token);
        }
    }
    private void StartConnection(TcpClient client, bool tls, ServerTlsConfiguration? security, CancellationToken token)
    {
        long id = Interlocked.Increment(ref sequence);
        // Register the completion before starting work, so stop cannot miss a connecting session.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        running[id] = finished.Task;
        _ = Run();
        async Task Run()
        {
            try
            {
                using (client)
                {
                    if (running.Count > Options.Number("MaxClients", 16, 1, 1024)) { return; }
                    IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                    if (!Options.AllowsClient(address)) { Event("ClientDenied", new { Address = address.ToString() }); return; }
                    client.NoDelay = true;
                    using var close = token.Register(client.Dispose);
                    Stream stream = client.GetStream();
                    if (tls)
                    {
                        if (security is null) { throw new InvalidOperationException("TLS certificate is not configured."); }
                        var ssl = new SslStream(stream, false, security.ValidateClient);
                        stream = ssl;
                        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                        handshake.CancelAfter(TimeSpan.FromSeconds(15));
                        try { await ssl.AuthenticateAsServerAsync(security.AuthenticationOptions(), handshake.Token); }
                        catch { await ssl.DisposeAsync(); throw; }
                    }
                    await using (stream)
                    {
                        var connection = new UsbServerConnection(this, address);
                        connections[connection.Id] = connection;
                        try { await connection.RunAsync(stream, token); }
                        finally { connections.TryRemove(connection.Id, out _); }
                    }
                }
            }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            { Event("ConnectionEnded", new { Error = error.Message }); }
            catch (Exception error) { Event("ConnectionFailed", new { Error = error.ToString() }); }
            finally { finished.TrySetResult(); running.TryRemove(id, out _); }
        }
    }
    private async Task ReverseAsync(ServerTlsConfiguration? security, CancellationToken token)
    {
        var workers = new Dictionary<string, (CancellationTokenSource Stop, Task Work)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var requested = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (bool tls in security is null ? new[] { false } : new[] { true })
                {
                    foreach (string endpoint in Options.Configuration.GetString(tls ? "SSLReverseClients" : "ReverseClients", "")!
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    { requested[(tls ? "tls:" : "tcp:") + endpoint] = tls; }
                }
                foreach (string key in workers.Keys.Except(requested.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
                { workers[key].Stop.Cancel(); await workers[key].Work; workers[key].Stop.Dispose(); workers.Remove(key); }
                foreach (var (key, tls) in requested)
                {
                    if (workers.ContainsKey(key)) { continue; }
                    if (tls && security is null) { throw new InvalidDataException("SSLReverseClients requires SSLCert."); }
                    var localStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    workers[key] = (localStop, ConnectAsync(key[4..], tls, security, localStop.Token));
                }
                foreach (var worker in workers.Values) { if (worker.Work.IsFaulted) { await worker.Work; } }
                await Task.Delay(1000, token);
            }
        }
        finally
        {
            foreach (var worker in workers.Values) { worker.Stop.Cancel(); }
            try { await Task.WhenAll(workers.Values.Select(worker => worker.Work)); } catch (OperationCanceledException) { }
            foreach (var worker in workers.Values) { worker.Stop.Dispose(); }
        }
    }
    private async Task ConnectAsync(string endpoint, bool tls, ServerTlsConfiguration? security, CancellationToken token)
    {
        int defaultPort = Options.Number(tls ? "SSLReverseClientsPort" : "ReverseClientsPort", tls ? 7572 : 7573, 1, 65535);
        if (!Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out Uri? uri)) { throw new InvalidDataException("Invalid reverse endpoint: " + endpoint); }
        int port = uri.Port < 0 ? defaultPort : uri.Port;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(uri.DnsSafeHost, port, connect.Token);
                // Same connection engine and TLS server role, even though TCP is outgoing.
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                long id = Interlocked.Increment(ref sequence);
                running[id] = finished.Task;
                try
                {
                    using var close = token.Register(client.Dispose);
                    Stream stream = client.GetStream();
                    if (tls)
                    {
                        var ssl = new SslStream(stream, false, security!.ValidateClient);
                        stream = ssl;
                        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                        handshake.CancelAfter(TimeSpan.FromSeconds(15));
                        await ssl.AuthenticateAsServerAsync(security.AuthenticationOptions(), handshake.Token);
                    }
                    await using (stream)
                    {
                        IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                        if (!Options.AllowsClient(address)) { Event("ClientDenied", endpoint); return; }
                        var connection = new UsbServerConnection(this, address);
                        connections[connection.Id] = connection;
                        try { await connection.RunAsync(stream, token); }
                        finally { connections.TryRemove(connection.Id, out _); }
                    }
                }
                finally { finished.TrySetResult(); running.TryRemove(id, out _); }
            }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            { if (!token.IsCancellationRequested) { Event("ReverseRetry", new { Endpoint = endpoint, Error = error.Message }); } }
            if (!token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(Options.Number("ReverseClientsRetryPeriod", 15, 1, 3600)), token); }
        }
    }
    internal void SaveConfiguration()
    { lock (configSync) { if (Options.ConfigurationPath is { } path) { Options.Configuration.Save(path); } } }
    internal void Event(string name, object data) => Log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Event = name, Data = data }));
}
