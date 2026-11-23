using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vhui.Core.Protocol;

namespace Vhui.Core.Windows;

/// <summary>Native Windows DNS-SD API, same service types and serial TXT key as the original.</summary>
[SupportedOSPlatform("windows")]
public sealed class MdnsRegistration(UsbServerOptions options, bool tls, TextWriter log) : IDisposable
{
    private readonly List<Registration> registrations = [];
    public void Register()
    {
        string service = options.Configuration.GetString("MDNSServiceName", "_vhusb._tcp")!;
        string name = options.Configuration.GetString("DNSFriendlyName", "VirtualHere USB Sharing")!;
        if (tls) { registrations.Add(new(name + " (SSL)._ssl" + service + ".local", Environment.MachineName + ".local", options.Number("SSLPort", 7574, 1, 65535), options.ServerId.ToString("D"), log)); }
        else { registrations.Add(new(name + "." + service + ".local", Environment.MachineName + ".local", options.Port, options.ServerId.ToString("D"), log)); }
    }
    public void Dispose() { foreach (Registration registration in registrations) { registration.Dispose(); } registrations.Clear(); }

    private sealed class Registration : IDisposable
    {
        private static readonly Completion Callback = Completed;
        private readonly object sync = new();
        private readonly TextWriter log;
        private IntPtr instance, request, cancel;
        private GCHandle root;
        private int phase; // 0=registering, 1=registered, 2=deregistering, 3=freed
        private bool stopping;
        public Registration(string name, string hostname, int port, string serial, TextWriter log)
        {
            this.log = log;
            IntPtr key = Marshal.StringToHGlobalUni(serial), value = Marshal.StringToHGlobalUni("");
            try { instance = DnsServiceConstructInstance(name, hostname, IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0, 1, [key], [value]); }
            finally { Marshal.FreeHGlobal(key); Marshal.FreeHGlobal(value); }
            if (instance == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            root = GCHandle.Alloc(this);
            request = Marshal.AllocHGlobal(Marshal.SizeOf<Request>());
            cancel = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(cancel, IntPtr.Zero);
            Marshal.StructureToPtr(new Request { Version = 1, Instance = instance, Callback = Marshal.GetFunctionPointerForDelegate(Callback), Context = GCHandle.ToIntPtr(root) }, request, false);
            uint result = DnsServiceRegister(request, cancel);
            if (result != 0x2522) { log.WriteLine($"mDNS registration failed: {result}"); Free(); }
        }
        private static void Completed(uint status, IntPtr context, IntPtr copy)
        {
            var registration = (Registration)GCHandle.FromIntPtr(context).Target!;
            lock (registration.sync)
            {
                if (copy != IntPtr.Zero && copy != registration.instance) { DnsServiceFreeInstance(copy); }
                try { registration.log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Event = "MdnsCompleted", Status = status, Stopping = registration.stopping })); }
                catch (IOException) { } catch (ObjectDisposedException) { }
                if (registration.phase == 2 || status != 0) { registration.Free(); return; }
                registration.phase = 1;
                if (registration.stopping) { registration.Deregister(); }
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                stopping = true;
                if (phase == 1) { Deregister(); }
                else if (phase == 0) { DnsServiceRegisterCancel(cancel); }
            }
        }
        private void Deregister()
        {
            phase = 2;
            Marshal.WriteIntPtr(cancel, IntPtr.Zero);
            uint result = DnsServiceDeRegister(request, cancel);
            if (result != 0x2522) { Free(); }
        }
        private void Free()
        {
            if (phase == 3) { return; } phase = 3;
            if (instance != IntPtr.Zero) { DnsServiceFreeInstance(instance); instance = IntPtr.Zero; }
            if (request != IntPtr.Zero) { Marshal.FreeHGlobal(request); request = IntPtr.Zero; }
            if (cancel != IntPtr.Zero) { Marshal.FreeHGlobal(cancel); cancel = IntPtr.Zero; }
            if (root.IsAllocated) { root.Free(); }
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Completion(uint status, IntPtr context, IntPtr instance);
    [StructLayout(LayoutKind.Sequential)] private struct Request
    { public uint Version, InterfaceIndex; public IntPtr Instance, Callback, Context, Credentials; public int Unicast; }
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DnsServiceConstructInstance(string name, string host, IntPtr ipv4, IntPtr ipv6, ushort port, ushort priority, ushort weight, uint count, IntPtr[] keys, IntPtr[] values);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegisterCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
}
