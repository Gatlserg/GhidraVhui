using System.Text;
using System.Text.Json;
using System.Globalization;
using System.ComponentModel;
using System.Net;
using System.Security.Principal;
using Vhui.Core.Unpacking;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;
using Vhui.Core.Configuration;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args is ["audio-output-probe", var outputDevice])
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        Console.WriteLine(JsonSerializer.Serialize(AudioOutputProbe.Run(outputDevice), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args is ["usb-check", var checkHost, var checkPort, var checkDevice])
    {
        string[] ids = checkDevice.Split(':');
        if (ids.Length != 2) { throw new ArgumentException("Expected VID:PID."); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await UsbCompatibilityProbe.RunAsync(checkHost, int.Parse(checkPort), ushort.Parse(ids[0], NumberStyles.HexNumber), ushort.Parse(ids[1], NumberStyles.HexNumber), timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result.Captured && result.Released ? 0 : 1;
    }
    if (args.Length == 2 && args[0] is "serve" or "service-run")
    {
        string configurationPath = Path.GetFullPath(args[1]);
        var options = new UsbServerOptions { Configuration = ServerConfiguration.Load(configurationPath), ConfigurationPath = configurationPath };
        if (args[0] == "service-run")
        {
            if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
            string logPath = options.FilePath("LogFile", Path.Combine(Path.GetDirectoryName(configurationPath)!, "vhui-service.log"))!;
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            TextWriter synchronized = TextWriter.Synchronized(log);
            return WindowsServerService.Run(token => new UsbServerHost(options, synchronized).RunAsync(token), synchronized);
        }
        using var stop = new CancellationTokenSource();
        string? consoleLogPath = options.FilePath("LogFile");
        if (consoleLogPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(consoleLogPath)!); }
        using StreamWriter? consoleLog = consoleLogPath is null ? null : new StreamWriter(new FileStream(consoleLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        TextWriter serverLog = consoleLog is null ? Console.Error : TextWriter.Synchronized(consoleLog);
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        try { await new UsbServerHost(options, serverLog).RunAsync(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= handler; }
        return 0;
    }
    if (args is ["service-install", var serviceConfig])
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        _ = ServerConfiguration.Load(serviceConfig);
        return WindowsServerService.Manage("install", serviceConfig);
    }
    if (args is ["service-uninstall"] or ["service-start"] or ["service-stop"] or ["service-status"])
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        return WindowsServerService.Manage(args[0][8..]);
    }
    if (args is ["restore-usb", var leaseJournal])
    {
        if (!OperatingSystem.IsWindows()) { throw new NotSupportedException("USB driver recovery requires Windows."); }
        WinUsbDeviceLease.RestoreFromJournal(leaseJournal);
        Console.WriteLine("Штатный драйвер USB восстановлен.");
        return 0;
    }
    if (args is ["camera-probe", var cameraName])
    {
        if (!OperatingSystem.IsWindows()) { throw new NotSupportedException("Camera probe requires Windows."); }
        Console.WriteLine(JsonSerializer.Serialize(CameraFrameProbe.Read(cameraName), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length == 3 && args[0] is "camera-probe" or "audio-probe" or "camera-stream")
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        bool timed = args[0] == "camera-stream";
        Console.WriteLine(JsonSerializer.Serialize(CameraFrameProbe.Read(args[1], timed ? 36000 : int.Parse(args[2]), args[0] == "audio-probe", timed ? int.Parse(args[2]) : 0), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args is ["trace-proxy", var listenText, var serverHost, var serverPortText, var traceDirectory, var secondsText])
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(secondsText, CultureInfo.InvariantCulture)));
        VirtualHereTraceProxy.RunAsync(int.Parse(listenText, CultureInfo.InvariantCulture), serverHost,
            int.Parse(serverPortText, CultureInfo.InvariantCulture), traceDirectory, timeout.Token).GetAwaiter().GetResult();
        return 0;
    }
    if (args is ["usb-inspect", var usbId])
    {
        if (!OperatingSystem.IsWindows()) { throw new NotSupportedException("Чтение USB-хабов поддерживается в Windows."); }
        string[] parts = usbId.Split(':');
        if (parts.Length != 2 || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vid)
            || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pid))
        {
            throw new ArgumentException("Укажите USB VID:PID в HEX, например 145F:02E7.");
        }
        UsbInventoryResult inventory = WindowsUsbInventory.Enumerate(vid, pid);
        Console.WriteLine(JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true }));
        return inventory.Devices.Count > 0 && inventory.Errors.Count == 0 ? 0 : 1;
    }
    if (args is ["probe-server", var host, var portText])
    {
        if (!int.TryParse(portText, out int port)) { throw new ArgumentException("Порт должен быть числом."); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        ServerProbeResult result = VirtualHereServerProbe.ProbeAsync(host, port, timeout.Token).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args.Length is >= 3 and <= 5 && args[0] is "serve-discovery" or "serve-usb")
    {
        if (!OperatingSystem.IsWindows()) { throw new NotSupportedException("Чтение USB-хабов поддерживается в Windows."); }
        bool sharing = args[0] == "serve-usb";
        if (sharing && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        { throw new UnauthorizedAccessException("Для serve-usb запустите терминал от администратора (UAC)."); }
        string[] parts = args[1].Split(':');
        if (parts.Length != 2 || !ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort vid)
            || !ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort pid))
        {
            throw new ArgumentException("Укажите USB VID:PID в HEX, например 145F:02E7.");
        }
        if (!int.TryParse(args[2], out int port)) { throw new ArgumentException("Порт должен быть числом."); }
        int seconds = args.Length >= 4 && int.TryParse(args[3], out int duration) ? duration : (sharing ? 0 : 60);
        if (args.Length >= 4 && !int.TryParse(args[3], out _)) { throw new ArgumentException("Продолжительность должна быть числом секунд."); }
        if (seconds < (sharing ? 0 : 1) || seconds > 3600) { throw new ArgumentException("Продолжительность: 1..3600 секунд; для serve-usb 0 означает работу до Ctrl+C."); }
        IPAddress bindAddress = args.Length == 5 ? IPAddress.Parse(args[4]) : IPAddress.Loopback;
        UsbInventoryResult inventory = WindowsUsbInventory.Enumerate(vid, pid);
        if (inventory.Devices.Count != 1)
        {
            throw new InvalidDataException("Нужно ровно одно устройство с указанным VID:PID. Проверьте usb-inspect VID:PID.");
        }
        UsbPhysicalDevice device = inventory.Devices[0];
        if (inventory.Errors.Count != 0) { throw new InvalidDataException(JsonSerializer.Serialize(inventory.Errors)); }
        using var timeout = new CancellationTokenSource();
        if (seconds > 0) { timeout.CancelAfter(TimeSpan.FromSeconds(seconds)); }
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; timeout.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try { VirtualHereServer.RunAsync(device, port, Console.Error, timeout.Token, sharing, bindAddress).GetAwaiter().GetResult(); }
        finally { Console.CancelKeyPress -= cancelHandler; }
        return 0;
    }
    if (args is ["inspect", var inputPath])
    {
        PackedPayloadInfo info = VhusbdImageProfile.Inspect(File.ReadAllBytes(inputPath));
        Console.WriteLine("Проверен исходный vhusbdwinw64.exe (UPX / LZMA / Windows x64).");
        Console.WriteLine($"Поток: смещение в файле 0x{info.StreamFileOffset:X}, размер 0x{info.StreamLength:X}.");
        Console.WriteLine($"LZMA: lc={info.Properties.LiteralContextBits}, lp={info.Properties.LiteralPositionBits}, pb={info.Properties.PositionBits}.");
        Console.WriteLine($"Лимит из C: {info.DeclaredOutputLimit}; подтверждённый декодируемый префикс: {info.VerifiedOutputLength} байт.");
        return 0;
    }
    if (args is ["extract-payload", var sourcePath, var destinationPath])
    {
        byte[] executable = File.ReadAllBytes(sourcePath);
        LzmaDecodeResult result = VhusbdImageProfile.ExtractVerifiedPayload(executable);
        // Не перезаписываем исходники или уже сохранённые результаты.
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(result.Bytes);
        Console.WriteLine($"Сохранён проверенный префикс payload: {result.Bytes.Length} байт.");
        Console.WriteLine("Это данные для анализа, а не исполняемая программа и не реализация USB-сервера.");
        return 0;
    }
    Console.WriteLine("Инструмент для анализа фактического C-экспорта vhusbdwinw64.exe.");
    Console.WriteLine("  Vhui.Recovery inspect <packed.exe>");
    Console.WriteLine("  Vhui.Recovery extract-payload <packed.exe> <new-payload.bin>");
    Console.WriteLine("  Vhui.Recovery usb-inspect <VID:PID>");
    Console.WriteLine("  Vhui.Recovery probe-server <host> <port>");
    Console.WriteLine("  Vhui.Recovery usb-check <host> <port> <VID:PID> (captures/releases device for protocol checks)");
    Console.WriteLine("  Vhui.Recovery serve-discovery <VID:PID> <loopback-port> [seconds=60]");
    Console.WriteLine("  Vhui.Recovery serve-usb <VID:PID> <port> [seconds=0] [bind-IP=127.0.0.1] (Administrator)");
    Console.WriteLine("  Vhui.Recovery serve <config.ini>");
    Console.WriteLine("  Vhui.Recovery service-install <config.ini> / service-start / service-stop / service-status / service-uninstall");
    Console.WriteLine("  Vhui.Recovery restore-usb <usb-leases/journal.json> (Administrator)");
    Console.WriteLine("  Vhui.Recovery camera-probe <camera-name>");
    Console.WriteLine("  Vhui.Recovery camera-stream <camera-name> <seconds>");
    Console.WriteLine("  Vhui.Recovery audio-probe <name> <sample-count>");
    Console.WriteLine("  Vhui.Recovery audio-output-probe <name> (100 ms of silent PCM)");
    Console.WriteLine("  Vhui.Recovery trace-proxy <listen-port> <host> <server-port> <trace-dir> <seconds>");
    Console.WriteLine("Руководство: README.ru.md рядом с Vhui.Recovery.exe.");
    return args.Length == 0 ? 0 : 2;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
    or ArgumentException or BadImageFormatException or Win32Exception or NotSupportedException or OperationCanceledException or System.Runtime.InteropServices.COMException
    or JsonException or FormatException or OverflowException or System.Security.Cryptography.CryptographicException or System.Security.Authentication.AuthenticationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
