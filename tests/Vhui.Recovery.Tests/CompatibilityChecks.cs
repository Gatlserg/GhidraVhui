using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vhui.Core.Configuration;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;

internal static class CompatibilityChecks
{
    public static IEnumerable<(string Name, Func<Task> Test)> Cases()
    {
        yield return ("USB status, no-op reset, clear-halt, ISO exceptions and compound reset", UsbCommands);
        yield return ("Opaque Reserved and native cancel ACK do not invent cancellation", Cancellation);
        yield return ("INI parsing, persistence, policy selection and native auth exit codes", Configuration);
        yield return ("Two clients, global ownership, busy, hotplug and recapture over TCP", MultiClient);
        yield return ("TLS and reverse sessions use the same framed protocol", TlsAndReverse);
        yield return ("Composite endpoint control requests use their associated interface", CompositeRouting);
        yield return ("Partial hub failures preserve only uncertain device locations", PartialInventory);
        yield return ("TLS client certificates reject missing credentials and accept the configured CA", ClientCertificates);
        yield return ("Missing keepalive releases a captured device", KeepAliveExpiry);
        yield return ("Plain reverse connection interoperates without TLS mode", PlainReverse);
        yield return ("Capture does not block keepalive processing", SlowCapture);
        yield return ("Release drains pending pipe I/O before disposing the backend", ReleasePending);
        yield return ("Failed driver restoration retries after replug without blocking other devices", LeaseRecovery);
        yield return ("Blank GUI allowlist permits connections without relaxing explicit or admin policies", BlankClientPolicy);
        yield return ("Partial descriptor failures preserve device identity without masking replacements", DescriptorFailures);
        yield return ("Sharing choices survive missing serials and can be fully disabled", SharingIdentity);
        yield return ("Repeated GUI unshare and republish cancels I/O and permits recapture despite port errors", RepublishAfterError);
        yield return ("A failed scanner waits for backend restoration before the host exits", ScannerFailureCleanup);
        yield return ("Device aliases persist, follow serial numbers and reset without affecting other devices", DeviceAliases);
        yield return ("Renaming broadcasts to both clients and preserves the captured USB backend", AliasBroadcast);
    }
    private static Task DeviceAliases()
    {
        string path = Path.Combine(Path.GetTempPath(), "vhui-alias-" + Guid.NewGuid() + ".ini");
        try
        {
            UsbPhysicalDevice device = Device(); var config = new ServerConfiguration(); var names = new UsbDeviceNames(config);
            const string name = "Камера — Studio ${KEEP}";
            names.Set(device, name); config.Save(path);
            var loaded = new UsbDeviceNames(ServerConfiguration.Load(path));
            Require(loaded.DisplayName(device) == name && loaded.DisplayName(device with { Port = 7 }) == name, "Serial alias did not persist or follow its device.");
            Require(loaded.DisplayName(device with { SerialNumber = "other" }) == device.Product, "Another known device inherited the alias.");
            Require(loaded.DisplayName(device with { SerialNumber = null }) == name, "Temporary serial loss discarded the alias.");
            loaded.Set(device with { SerialNumber = null }, "Updated");
            Require(loaded.DisplayName(device with { Port = 9 }) == "Updated", "Renaming during serial loss lost the known serial.");
            loaded.Set(device with { SerialNumber = "" }, "Empty serial");
            Require(loaded.DisplayName(device with { Port = 9 }) == "Empty serial", "An empty serial lost the stored device identity.");
            loaded.Set(device with { Port = 9 }, "");
            Require(loaded.DisplayName(device) == device.Product, "Reset left an alias behind at the old port.");
            loaded.Set(device with { SerialNumber = null }, "No serial");
            Require(loaded.DisplayName(device with { Port = 8, SerialNumber = null }) == device.Product, "A port-scoped name leaked to another port.");
            foreach (string invalid in new[] { new string('a', 64), new string('я', 32), "line\nfeed" })
            {
                try { loaded.Set(device, invalid); throw new InvalidOperationException("Invalid alias was accepted."); }
                catch (ArgumentException) { }
            }
            Require(device.Product == "Camera", "The physical USB name was mutated.");
        }
        finally { if (File.Exists(path)) { File.Delete(path); } }
        return Task.CompletedTask;
    }
    private static async Task AliasBroadcast()
    {
        var options = Options(Port()); var backend = new Backend(); UsbPhysicalDevice device = Device();
        var names = new UsbDeviceNames(options.Configuration); names.Set(device, "Studio camera");
        var host = new UsbServerHost(options, TextWriter.Null, () => new([device], []), (physical, _) =>
        { Require(physical.Product == device.Product, "The alias replaced the USB backend's hardware identity."); return Task.FromResult<IUsbDeviceBackend>(backend); });
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        string TextAt(byte[] bytes, int offset, int capacity) => System.Text.Encoding.UTF8.GetString(bytes, offset, capacity).TrimEnd('\0');
        try
        {
            using TcpClient owner = await Connect(options.Port); using TcpClient observer = await Connect(options.Port);
            await VirtualHereFrameCodec.WriteAsync(owner.GetStream(), Request(9));
            Require(TextAt((await Receive(owner.GetStream(), 16)).Payload, 68, 64) == "Studio camera", "Enumeration omitted the alias.");
            await VirtualHereFrameCodec.WriteAsync(owner.GetStream(), Request(0x19));
            Require(TextAt((await Receive(owner.GetStream(), 0x27)).Payload, 0x10016, 128) == "Studio camera", "Capture omitted the alias.");
            names.Set(device, "Камера 2"); host.NotifyDeviceNameChanged(device);
            foreach (Stream peer in new[] { owner.GetStream(), observer.GetStream() })
            {
                await Receive(peer, 16, message => TextAt(message.Payload, 68, 64) == "Камера 2");
            }
            Require(!backend.Disposed && host.Registry.Find(1)?.Owner is not null, "Renaming disconnected the current owner.");
        }
        finally { stop.Cancel(); await server; }
    }
    private static async Task ScannerFailureCleanup()
    {
        var options = Options(Port()); int fail = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend { DisposeDelay = release.Task };
        var host = new UsbServerHost(options, TextWriter.Null,
            () => Volatile.Read(ref fail) == 0 ? new UsbInventoryResult([Device()], []) : throw new IOException("scan failed"),
            (_, _) => Task.FromResult<IUsbDeviceBackend>(backend));
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient client = await Connect(options.Port);
            await VirtualHereFrameCodec.WriteAsync(client.GetStream(), Request(0x19)); await Receive(client.GetStream(), 0x27);
            Volatile.Write(ref fail, 1);
            await backend.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Require(!server.IsCompleted, "The host exited while the driver was still being restored.");
            release.TrySetResult();
            try { await server; throw new InvalidOperationException("Scanner failure was swallowed."); }
            catch (IOException error) when (error.Message == "scan failed") { }
            Require(backend.Disposed, "Scanner failure abandoned the owned backend.");
        }
        finally { release.TrySetResult(); stop.Cancel(); try { await server; } catch (IOException) { } }
    }
    private static Task DescriptorFailures()
    {
        var registry = new UsbDeviceRegistry(new()); UsbPhysicalDevice device = Device();
        registry.Refresh([device]); UsbDeviceEntry entry = registry.Devices.Single();
        var owner = new UsbOwner(Guid.NewGuid(), IPAddress.Loopback, "fixture", "fixture");
        Require(entry.TryOwn(owner), "Initial ownership failed.");
        var partial = device with { Manufacturer = null, Product = null, SerialNumber = null, ConfigurationDescriptor = [] };
        registry.Refresh([partial], [new(device.HubPath, device.Port, 31, "descriptor read failed")]);
        Require(entry.Present && entry.Owner == owner && entry.Device.SerialNumber == device.SerialNumber
            && entry.Device.Product == device.Product && entry.Device.ConfigurationDescriptor.Length == 9, "A descriptor read failure disconnected or renamed the owned device.");
        var cache = new UsbInventoryCache(); cache.Merge(new([device], [])); cache.Merge(new([], []));
        Require(cache.Merge(new([partial], [new(device.HubPath, device.Port, 31, "partial")])).Devices.Single().SerialNumber is null, "Confirmed unplug did not clear the previous identity.");
        registry.Refresh([device with { SerialNumber = "different-device" }]);
        Require(entry.Device.SerialNumber == "different-device", "A real replacement was hidden by descriptor caching.");
        var recovering = new UsbDeviceRegistry(new()); recovering.Refresh([partial]);
        UsbDeviceEntry initiallyUnknown = recovering.Devices.Single(); initiallyUnknown.TryOwn(owner);
        bool removed = false; initiallyUnknown.Changed += () => removed |= !initiallyUnknown.Present;
        recovering.Refresh([device]);
        Require(!removed && initiallyUnknown.Owner == owner, "Discovering a previously unreadable serial was mistaken for physical replacement.");
        return Task.CompletedTask;
    }
    private static Task SharingIdentity()
    {
        UsbPhysicalDevice device = Device(); UsbPhysicalDevice partial = device with { SerialNumber = null };
        var sharing = new UsbSharingSelection(); sharing.Set(device, true);
        Require(sharing.Contains(partial), "Unreadable serial removed the publication choice.");
        sharing.Set(partial, false);
        Require(!sharing.Contains(device) && sharing.Snapshot().Length == 0, "Disabling with a missing serial retained a hidden sharing choice.");
        sharing.Set(partial, true);
        Require(sharing.Contains(device), "A recovered serial invalidated the original unknown-serial choice.");
        sharing.Set(device, true);
        Require(!sharing.Contains(device with { SerialNumber = "different" }), "An explicitly different known device inherited the choice.");
        Require(new UsbSharingSelection(sharing.Snapshot()).Contains(device), "The sharing choice did not round-trip.");
        return Task.CompletedTask;
    }
    private static async Task RepublishAfterError()
    {
        var options = Options(Port()); var selection = new UsbSharingSelection(); UsbPhysicalDevice original = Device();
        selection.Set(original, true);
        UsbInventoryResult observed = new([original], []);
        var created = new List<Backend>();
        var host = new UsbServerHost(options, TextWriter.Null, () => Volatile.Read(ref observed), (_, _) =>
        {
            var backend = new Backend { Wait = created.Count == 0 }; created.Add(backend);
            return Task.FromResult<IUsbDeviceBackend>(backend);
        }, publish: selection.Contains);
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient peer = await Connect(options.Port); Stream stream = peer.GetStream();
            await VirtualHereFrameCodec.WriteAsync(stream, Request(0x19));
            Require(BinaryPrimitives.ReadInt32LittleEndian((await Receive(stream, 0x27)).Payload) == 1, "Initial capture failed.");
            var partial = original with { Manufacturer = null, Product = null, SerialNumber = null, ConfigurationDescriptor = [] };
            Volatile.Write(ref observed, new([partial], [new(original.HubPath, original.Port, 31, "metadata failed")]));
            byte[] pipe = new byte[13]; pipe[0] = 0x81; BinaryPrimitives.WriteInt32LittleEndian(pipe.AsSpan(9), 8);
            await VirtualHereFrameCodec.WriteAsync(stream, Request(0x12, payload: pipe));
            await created[0].Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Refresh immediately as well as through the host timer, reproducing an errored port.
            host.Registry.Refresh(observed.Devices, observed.Errors);
            Require(!created[0].Disposed && host.Registry.Find(1)!.Owner is not null, "The transient read error interrupted capture.");
            for (int cycle = 0; cycle < 3; cycle++)
            {
                selection.Set(partial, false); host.Registry.Refresh(observed.Devices, observed.Errors);
                await Receive(stream, 16, message => message.Payload[136] == 0);
                for (int index = 0; index < 100 && !created[cycle].Disposed; index++) { await Task.Delay(10); }
                Require(created[cycle].Disposed, "Unshare did not cancel and drain the owned backend.");
                selection.Set(partial, true); host.Registry.Refresh(observed.Devices, observed.Errors);
                await Receive(stream, 16, message => message.Payload[136] == 1);
                await VirtualHereFrameCodec.WriteAsync(stream, Request(0x19));
                Require(BinaryPrimitives.ReadInt32LittleEndian((await Receive(stream, 0x27)).Payload) == 1, "Republished device could not be captured.");
            }
        }
        finally { stop.Cancel(); await server; }
    }
    private static async Task BlankClientPolicy()
    {
        var options = Options(Port());
        IPAddress client = IPAddress.Parse("192.168.0.190");
        foreach (string value in new[] { "", "   ", "*", "192.168.0.190", "192.168.0.0/24" })
        {
            options.Configuration.Set("AllowedClients", value);
            Require(options.AllowsClient(client), "The GUI client was rejected for allowlist: " + value);
        }
        options.Configuration.Set("AllowedClients", "192.168.1.0/24");
        Require(!options.AllowsClient(client), "An explicit subnet restriction was bypassed.");
        Require(!UsbServerOptions.MatchesAddress("", client), "An empty administrative restriction became permissive.");
        options.Configuration.Set("AllowedClients", "");
        using var stop = new CancellationTokenSource();
        var host = new UsbServerHost(options, TextWriter.Null, () => new UsbInventoryResult([], []), (_, _) => Task.FromResult<IUsbDeviceBackend>(new Backend()));
        Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient connection = await Connect(options.Port);
            await VirtualHereFrameCodec.WriteAsync(connection.GetStream(), Request(0));
            Require((await Receive(connection.GetStream(), 1)).Payload[0] == 8, "An empty allowlist still blocked the network handshake.");
        }
        finally { stop.Cancel(); await server; }
    }
    private static Task LeaseRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vhui-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            void Journal(string name, string phase, int? pid) => File.WriteAllText(Path.Combine(directory, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new { Phase = phase, OwnerProcessId = pid }));
            Journal("live", "Captured", 1); Journal("dead", "Captured", 2);
            Journal("unplugged", "RestoreFailed", 1); Journal("legacy", "Binding", null); Journal("done", "Restored", 2);
            var restored = new List<string>(); var errors = new List<string>(); bool replugged = false;
            void Restore(string path)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name == "unplugged" && !replugged) { throw new IOException("Device is disconnected."); }
                restored.Add(name); Journal(name, "Restored", null);
            }
            UsbLeaseRecovery.Recover(directory, pid => pid == 1, Restore, (path, _) => errors.Add(Path.GetFileNameWithoutExtension(path)));
            Require(restored.SequenceEqual(new[] { "dead" }) && errors.SequenceEqual(new[] { "unplugged" }), "One absent device blocked recovery or a live capture was touched.");
            replugged = true;
            UsbLeaseRecovery.Recover(directory, pid => pid == 1, Restore, (path, _) => throw new Exception(path));
            Require(restored.SequenceEqual(new[] { "dead", "unplugged" }), "A live daemon prevented recovery of its disposed backend.");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    private static UsbPhysicalDevice Device(string hub = "test-hub", int port = 1) => new(hub, port, 0x145f, 0x02e7, 2, false,
        "Fixture", "Camera", "fixture-serial", new byte[18], new byte[9]);
    private static VirtualHereMessage Request(byte type, uint address = 1, byte[]? payload = null)
        => VirtualHereMessage.Create(type, payload) with { DeviceAddress = address, TokenA = (ulong)Interlocked.Increment(ref next) };
    private static long next;
    private static Task CompositeRouting()
    {
        var handles = new Dictionary<byte, IntPtr> { [0] = new(10), [2] = new(20) };
        IntPtr Endpoint(byte endpoint) => endpoint == 0x83 ? handles[2] : throw new InvalidOperationException();
        Require(UsbControlRouting.SelectHandle([0x82, 0, 0, 0, 0x83, 0, 2, 0], handles, Endpoint) == handles[2], "Endpoint recipient was routed to the root interface.");
        Require(UsbControlRouting.SelectHandle([0x81, 0, 0, 0, 2, 0, 2, 0], handles, Endpoint) == handles[2], "Interface recipient selected the wrong handle.");
        Require(UsbControlRouting.SelectHandle([0x80, 0, 0, 0, 0, 0, 2, 0], handles, Endpoint) == handles[0], "Device recipient did not select the root handle.");
        return Task.CompletedTask;
    }
    private static Task PartialInventory()
    {
        var registry = new UsbDeviceRegistry(new()); registry.Refresh([Device("A"), Device("B")]);
        registry.Refresh([Device("C")], [new("A", 1, 31, "transient")]);
        Require(registry.Devices.Count == 2 && registry.Devices.Any(value => value.Device.HubPath == "A")
            && registry.Devices.Any(value => value.Device.HubPath == "C"), "An unrelated port failure hid valid devices or kept a confirmed removal.");
        return Task.CompletedTask;
    }
    private static void Require(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static async Task UsbCommands()
    {
        var options = new UsbServerOptions();
        var entry = new UsbDeviceEntry(Device(), 1);
        var backend = new Backend();
        await using var session = new WindowsUsbProtocolSession(entry, new(Guid.NewGuid(), IPAddress.Loopback, "fixture", "fixture"),
            "serial", options, (_, _) => Task.FromResult<IUsbDeviceBackend>(backend), TextWriter.Null);
        await session.HandleAsync(Request(0x19), default);
        VirtualHereMessage status = (await session.HandleAsync(Request(0x14), default))[0];
        Require(status.Type == 0x15 && status.Payload.Length == 6 && BinaryPrimitives.ReadInt32LittleEndian(status.Payload) == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(status.Payload.AsSpan(4)) == 0x503, "Port status differs from native response layout.");
        await session.HandleAsync(Request(0x16), default);
        Require(backend.Actions.SequenceEqual(["status"]), "Default reset must not change alternate settings.");
        byte[] endpoint = [0x81, 0, 0, 0];
        await session.HandleAsync(Request(0x1a, payload: endpoint), default);
        await session.HandleAsync(Request(0x32, payload: endpoint), default);
        Require(backend.Actions.SequenceEqual(["status", "reset:81", "abort:81", "reset:81", "reset:81"]), "Native clear-halt/compound sequence changed.");
        backend.Iso = true;
        int before = backend.Actions.Count;
        await session.HandleAsync(Request(0x1c, payload: endpoint), default);
        await session.HandleAsync(Request(0x1e, payload: endpoint), default);
        Require(backend.Actions.Count == before, "Native reset/abort must skip ISO pipes.");
        byte[] extended = new byte[18]; extended[0] = 1; extended[1] = 3; extended[3] = 2; extended[4] = 4;
        await session.HandleAsync(Request(0x33, payload: extended), default);
        Require(backend.Actions.TakeLast(3).SequenceEqual(["alternate:0:0", "alternate:1:2", "alternate:2:4"]), "Extended reset treated the packed alternate bytes as UInt32 values.");
        backend.ThrowStatus = true;
        status = (await session.HandleAsync(Request(0x14), default))[0];
        Require(BinaryPrimitives.ReadInt32LittleEndian(status.Payload) == -19 && BinaryPrimitives.ReadUInt16LittleEndian(status.Payload.AsSpan(4)) == 0x103,
            "Native port-status failure contract changed.");
    }
    private static async Task Cancellation()
    {
        var backend = new Backend { Wait = true };
        await using var session = new WindowsUsbProtocolSession(new(Device(), 1), new(Guid.NewGuid(), IPAddress.Loopback, "", ""), "serial", new(),
            (_, _) => Task.FromResult<IUsbDeviceBackend>(backend), TextWriter.Null);
        await session.HandleAsync(Request(0x19), default);
        byte[] pipe = new byte[13]; pipe[0] = 0x81; BinaryPrimitives.WriteInt32LittleEndian(pipe.AsSpan(9), 8);
        using var cancel = new CancellationTokenSource();
        VirtualHereMessage request = Request(0x4a, payload: pipe) with { Reserved = 1 };
        Task<IReadOnlyList<VirtualHereMessage>> transfer = session.HandleAsync(request, cancel.Token);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(40);
        byte[] cookie = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(cookie, request.TokenA);
        VirtualHereMessage ack = (await session.HandleAsync(Request(0x21, payload: cookie), default))[0];
        Require(!transfer.IsCompleted && ack.Type == 0x22 && ack.Payload.SequenceEqual(new byte[12]), "Reserved or ACK incorrectly canceled USB I/O.");
        cancel.Cancel();
        Require(BinaryPrimitives.ReadInt32LittleEndian((await transfer)[0].Payload) == -104, "Actual cancellation did not complete the request.");
    }
    private static async Task Configuration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vhui-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "config.ini");
            await File.WriteAllTextAsync(path, "; comment\n[server]\nServerName=First\nservername=Second\nAllowedDevices=145f/*\nIgnoredDevices=145f/0001\n");
            ServerConfiguration configuration = ServerConfiguration.Load(path);
            var options = new UsbServerOptions { Configuration = configuration };
            Require(options.ServerName == "Second" && options.Publishes(Device()) && !options.Publishes(Device() with { ProductId = 1 }), "Configuration policy not applied.");
            Require(UsbServerOptions.MatchesAddress("10.4.0.0/16", IPAddress.Parse("10.4.9.3"))
                && !UsbServerOptions.MatchesAddress("10.4.0.0/16", IPAddress.Parse("10.5.9.3"))
                && UsbServerOptions.MatchesAddress("fd00::/8", IPAddress.Parse("fd12::4")), "CIDR client policy failed.");
            configuration.Save(path); Require(ServerConfiguration.Load(path).GetString("SERVERNAME") == "Second", "INI save lost settings.");
            if (OperatingSystem.IsWindows())
            {
                foreach (var (exit, expected) in new[] { (0, -4), (1, 1), (2, -7), (3, -8) })
                {
                    configuration.Set("ClientAuthorization", "exit /b " + exit);
                    await using var session = new WindowsUsbProtocolSession(new(Device(), 1), new(Guid.NewGuid(), IPAddress.Loopback, "", ""), "serial", options,
                        (_, _) => Task.FromResult<IUsbDeviceBackend>(new Backend()), TextWriter.Null);
                    Require(BinaryPrimitives.ReadInt32LittleEndian((await session.HandleAsync(Request(0x19), default))[0].Payload) == expected,
                        "External authorization exit code differs from native 0x1409689e0 table.");
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static int Port()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static UsbServerOptions Options(int port)
    {
        var configuration = new ServerConfiguration(); configuration.Set("TCPPort", port.ToString()); configuration.Set("UseAVAHI", "0");
        configuration.Set("ScanInterval", "1"); configuration.Set("PingTimeout", "30"); return new() { Configuration = configuration };
    }
    private static async Task<TcpClient> Connect(int port)
    {
        for (int attempt = 0; ; attempt++)
        {
            var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, port); return client; }
            catch (SocketException) when (attempt < 50) { client.Dispose(); await Task.Delay(20); }
        }
    }
    private static async Task<VirtualHereMessage> Receive(Stream stream, byte type, Func<VirtualHereMessage, bool>? predicate = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (await VirtualHereFrameCodec.ReadAsync(stream, timeout.Token) is { } message)
        {
            if (message.Type == 12) { await VirtualHereFrameCodec.WriteAsync(stream, message with { Type = 15 }, timeout.Token); }
            if (message.Type == type && (predicate is null || predicate(message))) { return message; }
        }
        throw new IOException("Unexpected EOF.");
    }
    private static async Task MultiClient()
    {
        int port = Port(); var options = Options(port); UsbPhysicalDevice[] snapshot = [Device("hub-A"), Device("hub-B")];
        var backends = new List<Backend>();
        var host = new UsbServerHost(options, TextWriter.Null, () => new(Volatile.Read(ref snapshot), []), (_, _) =>
        { var backend = new Backend(); lock (backends) { backends.Add(backend); } return Task.FromResult<IUsbDeviceBackend>(backend); });
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient first = await Connect(port); using TcpClient second = await Connect(port);
            Stream one = first.GetStream(), two = second.GetStream();
            await VirtualHereFrameCodec.WriteAsync(two, Request(9));
            var a = await Receive(two, 16); var b = await Receive(two, 16);
            uint address = BinaryPrimitives.ReadUInt32LittleEndian(a.Payload.AsSpan(132));
            Require(address != BinaryPrimitives.ReadUInt32LittleEndian(b.Payload.AsSpan(132)), "Ports on different hubs collided.");
            await VirtualHereFrameCodec.WriteAsync(one, Request(0x19, address)); await Receive(one, 0x27);
            var owned = await Receive(two, 16, message => message.Payload[136] == 3);
            Require(new Guid(owned.Payload.AsSpan(137, 16)) != Guid.Empty, "Owner wasn't broadcast to another client.");
            await VirtualHereFrameCodec.WriteAsync(two, Request(0x19, address));
            Require(BinaryPrimitives.ReadInt32LittleEndian((await Receive(two, 0x27)).Payload) == -2, "Second client captured an owned device.");
            Volatile.Write(ref snapshot, []);
            await Receive(two, 16, message => message.Payload[136] == 0);
            for (int attempt = 0; attempt < 100 && !backends[0].Disposed; attempt++) { await Task.Delay(10); }
            Require(backends[0].Disposed, "Hotplug did not dispose the active lease.");
            Volatile.Write(ref snapshot, [Device("hub-A"), Device("hub-B")]);
            await Receive(two, 16, message => message.Payload[136] == 1);
            await VirtualHereFrameCodec.WriteAsync(two, Request(0x19, address));
            Require(BinaryPrimitives.ReadInt32LittleEndian((await Receive(two, 0x27)).Payload) == 1, "Recapture after hotplug failed.");
            second.Dispose();
            for (int attempt = 0; attempt < 100 && host.Registry.Find(address)!.Owner is not null; attempt++) { await Task.Delay(10); }
            Require(host.Registry.Find(address)!.Owner is null, "Peer EOF did not release ownership.");
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
    private static async Task TlsAndReverse()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vhui-tls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        int tcp = Port(), tls = Port(), reverse = Port(); var options = Options(tcp);
        string file = Path.Combine(directory, "server.pfx"); await File.WriteAllBytesAsync(file, cert.Export(X509ContentType.Pkcs12));
        options.Configuration.Set("SSLCert", file); options.Configuration.Set("SSLPort", tls.ToString());
        options.Configuration.Set("SSLReverseClients", "127.0.0.1:" + reverse);
        var listener = new TcpListener(IPAddress.Loopback, reverse); listener.Start();
        var host = new UsbServerHost(options, TextWriter.Null, () => new([], []));
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient client = await Connect(tls);
            using var ssl = new SslStream(client.GetStream(), false, (_, candidate, _, _) => candidate is not null && candidate.GetCertHashString() == cert.GetCertHashString());
            await ssl.AuthenticateAsClientAsync("localhost");
            await VirtualHereFrameCodec.WriteAsync(ssl, Request(0)); Require((await Receive(ssl, 1)).Payload[0] == 8, "TLS frame protocol failed.");
            using TcpClient reverseClient = await listener.AcceptTcpClientAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using var reverseSsl = new SslStream(reverseClient.GetStream(), false, (_, candidate, _, _) => candidate?.GetCertHashString() == cert.GetCertHashString());
            await reverseSsl.AuthenticateAsClientAsync("localhost");
            await VirtualHereFrameCodec.WriteAsync(reverseSsl, Request(0));
            Require((await Receive(reverseSsl, 1)).Payload[0] == 8, "TLS reverse session frame protocol failed.");
        }
        finally
        { stop.Cancel(); listener.Stop(); try { await server; } catch (OperationCanceledException) { } Directory.Delete(directory, true); }
    }
    private static async Task ClientCertificates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vhui-mtls-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fixture CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using RSA clientKey = RSA.Create(2048);
        var clientRequest = new CertificateRequest("CN=Fixture Client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        clientRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        clientRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using X509Certificate2 issued = clientRequest.Create(certificate, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 withKey = issued.CopyWithPrivateKey(clientKey);
        using X509Certificate2 clientCertificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        var options = Options(Port()); int tls = Port();
        string pfx = Path.Combine(directory, "server.pfx"), ca = Path.Combine(directory, "ca.pem");
        await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pkcs12)); await File.WriteAllTextAsync(ca, certificate.ExportCertificatePem());
        options.Configuration.Set("SSLCert", pfx); options.Configuration.Set("SSLCAFile", ca); options.Configuration.Set("SSLUseClientCerts", "1"); options.Configuration.Set("SSLPort", tls.ToString());
        var log = new StringWriter();
        using var stop = new CancellationTokenSource(); Task server = new UsbServerHost(options, log, () => new([], [])).RunAsync(stop.Token);
        try
        {
            using (var plain = new TcpClient())
            {
                bool connected = false;
                try { await plain.ConnectAsync(IPAddress.Loopback, options.Port); connected = true; } catch (SocketException) { }
                Require(!connected, "TLS mode exposed an unauthenticated plaintext listener.");
            }
            foreach (bool credentials in new[] { false, true })
            {
                bool accepted = false;
                string failure = "";
                try
                {
                    using TcpClient client = await Connect(tls);
                    using var ssl = new SslStream(client.GetStream(), false, (_, candidate, _, _) => candidate?.GetCertHashString() == certificate.GetCertHashString());
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
                        ClientCertificates = credentials ? new X509CertificateCollection { clientCertificate } : [] });
                    await VirtualHereFrameCodec.WriteAsync(ssl, Request(0)); await Receive(ssl, 1); accepted = true;
                }
                catch (Exception error) when (error is IOException or System.Security.Authentication.AuthenticationException) { failure = error.ToString(); }
                Require(accepted == credentials, $"Mutual TLS client certificate policy failed: supplied={credentials}, accepted={accepted}. {failure} {log}");
            }
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } Directory.Delete(directory, true); }
    }
    private static async Task KeepAliveExpiry()
    {
        var options = Options(Port()); options.Configuration.Set("PingInterval", "1"); options.Configuration.Set("PingTimeout", "1");
        var backend = new Backend();
        var host = new UsbServerHost(options, TextWriter.Null, () => new([Device()], []), (_, _) => Task.FromResult<IUsbDeviceBackend>(backend));
        using var stop = new CancellationTokenSource(); Task server = host.RunAsync(stop.Token);
        try
        {
            using TcpClient client = await Connect(options.Port);
            await VirtualHereFrameCodec.WriteAsync(client.GetStream(), Request(0x19)); await Receive(client.GetStream(), 0x27);
            for (int attempt = 0; attempt < 100 && !backend.Disposed; attempt++) { await Task.Delay(30); }
            Require(backend.Disposed && host.Registry.Find(1)!.Owner is null, "An unresponsive client retained USB ownership.");
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }
    private static async Task PlainReverse()
    {
        int port = Port(); var options = Options(Port());
        options.Configuration.Set("ReverseClients", "127.0.0.1:" + port);
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        using var stop = new CancellationTokenSource(); Task server = new UsbServerHost(options, TextWriter.Null, () => new([], [])).RunAsync(stop.Token);
        try
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(stop.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await VirtualHereFrameCodec.WriteAsync(peer.GetStream(), Request(0)); await Receive(peer.GetStream(), 1);
        }
        finally { stop.Cancel(); listener.Stop(); await server; }
    }
    private static async Task SlowCapture()
    {
        var options = Options(Port()); options.Configuration.Set("PingInterval", "1"); options.Configuration.Set("PingTimeout", "2");
        using var stop = new CancellationTokenSource();
        Task server = new UsbServerHost(options, TextWriter.Null, () => new([Device()], []), async (_, token) =>
        { await Task.Delay(2800, token); return new Backend(); }).RunAsync(stop.Token);
        try
        {
            using TcpClient client = await Connect(options.Port);
            await VirtualHereFrameCodec.WriteAsync(client.GetStream(), Request(0x19));
            Require(BinaryPrimitives.ReadInt32LittleEndian((await Receive(client.GetStream(), 0x27)).Payload) == 1, "Capture starved the heartbeat reader.");
        }
        finally { stop.Cancel(); await server; }
    }
    private static async Task ReleasePending()
    {
        var options = Options(Port()); var backend = new Backend { Wait = true };
        using var stop = new CancellationTokenSource();
        Task server = new UsbServerHost(options, TextWriter.Null, () => new([Device()], []), (_, _) => Task.FromResult<IUsbDeviceBackend>(backend)).RunAsync(stop.Token);
        try
        {
            using TcpClient client = await Connect(options.Port); Stream stream = client.GetStream();
            await VirtualHereFrameCodec.WriteAsync(stream, Request(0x19)); await Receive(stream, 0x27);
            byte[] payload = new byte[13]; payload[0] = 0x81; BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(9), 8);
            await VirtualHereFrameCodec.WriteAsync(stream, Request(0x12, payload: payload));
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await VirtualHereFrameCodec.WriteAsync(stream, Request(0x23));
            VirtualHereMessage release = await Receive(stream, 7);
            Require(backend.Disposed && BinaryPrimitives.ReadInt32LittleEndian(release.Payload) == 1, "Release did not drain pending I/O.");
        }
        finally { stop.Cancel(); await server; }
    }
    private sealed class Backend : IUsbDeviceBackend
    {
        public List<string> Actions { get; } = [];
        public bool Iso, ThrowStatus, Wait, Disposed;
        public Task? DisposeDelay;
        public TaskCompletionSource DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ushort GetPortStatus() { Actions.Add("status"); if (ThrowStatus) { throw new System.ComponentModel.Win32Exception(31); } return 0x503; }
        public void ResetPipe(byte endpoint) => Actions.Add($"reset:{endpoint:X2}");
        public void AbortPipe(byte endpoint) => Actions.Add($"abort:{endpoint:X2}");
        public bool IsIsochronous(byte endpoint) => Iso;
        public void SetAlternate(byte number, byte alternate) => Actions.Add($"alternate:{number}:{alternate}");
        public Task<int> ControlAsync(byte[] setup, byte[] bytes, CancellationToken token) => Task.FromResult(0);
        public async Task<int> PipeAsync(byte endpoint, byte[] bytes, CancellationToken token)
        { Started.TrySetResult(); if (Wait) { await Task.Delay(Timeout.Infinite, token); } return 0; }
        public Task<(int Length, byte[] Packets)> IsochronousAsync(byte endpoint, byte[] bytes, byte[] packets, CancellationToken token) => Task.FromResult((0, packets));
        public async ValueTask DisposeAsync() { DisposeStarted.TrySetResult(); if (DisposeDelay is not null) { await DisposeDelay; } Disposed = true; }
    }
}
