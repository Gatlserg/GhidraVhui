using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vhui.Core.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsServerService
{
    public const string Name = "VhuiRecovery";
    public static int Run(Func<CancellationToken, Task> work, TextWriter log)
    {
        using var stop = new CancellationTokenSource();
        IntPtr handle = IntPtr.Zero;
        var sync = new object();
        int result = 0;
        uint checkpoint = 0;
        void Report(uint state, uint error = 0)
        {
            lock (sync)
            {
                var status = new Status { Type = 0x10, State = state, Controls = state == 4 ? 5u : 0u,
                    Win32Exit = error == 0 ? 0u : 1066u, ServiceExit = error,
                    Checkpoint = state is 2 or 3 ? ++checkpoint : 0, WaitHint = state is 2 or 3 ? 30000u : 0u };
                if (handle != IntPtr.Zero && !SetServiceStatus(handle, ref status)) { log.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()).Message); }
                log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Event = "ServiceStatus", State = state, AcceptedControls = status.Controls, Error = error }));
            }
        }
        Control control = command =>
        {
            log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Event = "ServiceControl", Command = command }));
            if (command is 1 or 5) { Report(3); _ = stop.CancelAsync(); }
        };
        Main main = (_, _) =>
        {
            try
            {
                handle = RegisterServiceCtrlHandlerW(Name, control);
                if (handle == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                Report(2);
                Task running = work(stop.Token);
                if (!stop.IsCancellationRequested) { Report(4); }
                using var progress = new Timer(_ => { if (stop.IsCancellationRequested) { Report(3); } }, null, 5000, 5000);
                running.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { result = 1; log.WriteLine(error); }
            finally { Report(1, (uint)result); }
        };
        IntPtr name = Marshal.StringToHGlobalUni(Name);
        try
        {
            Entry[] table = [new() { Name = name, Main = Marshal.GetFunctionPointerForDelegate(main) }, new()];
            if (!StartServiceCtrlDispatcherW(table)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            GC.KeepAlive(main); GC.KeepAlive(control);
            return result;
        }
        finally { Marshal.FreeHGlobal(name); }
    }
    public static int Manage(string command, string? configuration = null)
    {
        var start = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true };
        if (command == "install")
        {
            if (configuration is null || !File.Exists(configuration)) { throw new FileNotFoundException("Service configuration not found.", configuration); }
            string executable = Environment.ProcessPath ?? throw new IOException("Executable path unavailable.");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Install the published Vhui.Recovery.exe, not dotnet run."); }
            string binary = "\"" + executable + "\" service-run \"" + Path.GetFullPath(configuration) + "\"";
            foreach (string argument in new[] { "create", Name, "binPath=", binary, "start=", "auto", "DisplayName=", "Vhui USB Server (.NET)" }) { start.ArgumentList.Add(argument); }
        }
        else
        {
            string operation = command switch { "uninstall" => "delete", "start" => "start", "stop" => "stop", "status" => "query", _ => throw new ArgumentException("Unknown service operation.") };
            start.ArgumentList.Add(operation); start.ArgumentList.Add(Name);
        }
        using Process process = Process.Start(start) ?? throw new IOException("sc.exe did not start.");
        process.WaitForExit(); return process.ExitCode;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct Entry { public IntPtr Name, Main; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Main(uint count, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Control(uint command);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcherW([In] Entry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerW(string name, Control control);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
}
