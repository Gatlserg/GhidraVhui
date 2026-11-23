using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Vhui.Core.Windows;

/// <summary>Temporarily binds the physical USB devnode to Microsoft's WinUSB driver.</summary>
[SupportedOSPlatform("windows")]
public sealed class WinUsbDeviceLease : IUsbDeviceBackend
{
    private static readonly Guid InterfaceGuid = new("2ae6fa83-d310-471a-aee7-63f450dccb2b");
    private static readonly SemaphoreSlim DriverChanges = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UsbPhysicalDevice> SharedDevices = new(StringComparer.OrdinalIgnoreCase);
    private string? inventoryKey;
    private readonly string instanceId;
    private readonly string originalInf;
    private byte[] configurationDescriptor = [];
    private SafeFileHandle? file;
    private readonly Dictionary<byte, IntPtr> interfaces = [];
    private readonly Dictionary<byte, byte> alternateSettings = [];
    private bool changed;
    private bool disposed;
    private string? originalInterfaceGuid;
    private string? journal;
    public string InstanceId => instanceId;

    private WinUsbDeviceLease(string id, string inf) { instanceId = id; originalInf = inf; }

    internal static UsbInventoryResult ReadStableInventory(Func<UsbInventoryResult> read)
    {
        DriverChanges.Wait();
        try { return read(); }
        finally { DriverChanges.Release(); }
    }

    internal static UsbPhysicalDevice? CapturedDevice(string hub, int port)
        => SharedDevices.GetValueOrDefault(hub + "#" + port);

    public static void RecoverAbandonedLeases(Action<string, Exception>? onError = null)
    {
        DriverChanges.Wait();
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "usb-leases");
            UsbLeaseRecovery.Recover(directory, IsProcessAlive, RestoreJournalCore, onError);
        }
        finally { DriverChanges.Release(); }
    }
    private static bool IsProcessAlive(int pid)
    {
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    public static async Task<WinUsbDeviceLease> CaptureAsync(UsbPhysicalDevice device, CancellationToken token)
    {
        RequireAdministrator();
        await DriverChanges.WaitAsync(token);
        try { return await CaptureCoreAsync(device, token); }
        finally { DriverChanges.Release(); }
    }

    private static async Task<WinUsbDeviceLease> CaptureCoreAsync(UsbPhysicalDevice device, CancellationToken token)
    {
        RequireAdministrator();
        if (device.IsHub) { throw new NotSupportedException("WinUSB sharing supports USB functions, not USB hubs."); }
        string driverKey = WindowsUsbInventory.ReadDriverKey(device);
        string instanceId = FindDevice(driverKey);
        string directory = Path.Combine(AppContext.BaseDirectory, "usb-leases");
        string journalPath = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)))[..16] + ".json");
        if (File.Exists(journalPath))
        {
            using JsonDocument previous = JsonDocument.Parse(File.ReadAllText(journalPath));
            if (previous.RootElement.GetProperty("Phase").GetString() != "Restored")
            {
                if (!UsbLeaseRecovery.CanRecover(previous.RootElement, IsProcessAlive)) { throw new IOException($"An unfinished live USB lease exists. Recover it with restore-usb: {journalPath}"); }
                RestoreJournalCore(journalPath);
                driverKey = WindowsUsbInventory.ReadDriverKey(device);
            }
        }
        using RegistryKey driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey)
            ?? throw new InvalidDataException("Original driver registry key not found.");
        string inf = driver.GetValue("InfPath") as string ?? throw new InvalidDataException("Original driver INF not found.");
        var lease = new WinUsbDeviceLease(instanceId, inf) { configurationDescriptor = device.ConfigurationDescriptor };
        lease.inventoryKey = Vhui.Core.Protocol.UsbDeviceRegistry.Key(device);
        SharedDevices[lease.inventoryKey] = device;
        try
        {
            using (RegistryKey parameters = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters"))
            {
                object? previous = parameters.GetValue("DeviceInterfaceGUID");
                if (previous is not null and not string) { throw new IOException("Unexpected DeviceInterfaceGUID registry type."); }
                lease.originalInterfaceGuid = previous as string;
                Directory.CreateDirectory(directory);
                lease.journal = journalPath;
                lease.WriteJournal("Binding");
            }
            lease.changed = true; // Restore even if installation reports a partial failure.
            // Match the native Windows backend: first detach the current function
            // driver. This clears the USB Composite class restriction on selection.
            await Task.Run(() => DetachDriver(instanceId), token);
            using (RegistryKey parameters = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters"))
            {
                parameters.SetValue("DeviceInterfaceGUID", InterfaceGuid.ToString("B"), RegistryValueKind.String);
            }
            await Task.Run(() => InstallDriver(instanceId, "winusb.inf"), token);
            for (int attempt = 0; attempt < 60; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string? path = WindowsUsbInventory.GetInterfacePaths(InterfaceGuid).FirstOrDefault(candidate =>
                    candidate.Contains(instanceId.Replace('\\', '#'), StringComparison.OrdinalIgnoreCase));
                if (path is not null)
                {
                    lease.file = Api.CreateFileW(path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000080, IntPtr.Zero);
                    if (!lease.file.IsInvalid && Api.WinUsb_Initialize(lease.file, out IntPtr root))
                    {
                        lease.AddInterface(root);
                        for (byte index = 0; Api.WinUsb_GetAssociatedInterface(root, index, out IntPtr associated); index++)
                        {
                            lease.AddInterface(associated);
                            if (index == 254) { break; }
                        }
                        lease.WriteJournal("Captured");
                        return lease;
                    }
                    lease.file.Dispose(); lease.file = null;
                }
                await Task.Delay(100, token);
            }
            throw new IOException("WinUSB interface did not become available after driver binding.");
        }
        catch { await lease.DisposeCoreAsync(); throw; }
    }

    private void AddInterface(IntPtr handle)
    {
        byte[] descriptor = new byte[9];
        if (!Api.WinUsb_QueryInterfaceSettings(handle, 0, descriptor)) { Api.WinUsb_Free(handle); throw Error(); }
        interfaces.Add(descriptor[2], handle);
        alternateSettings.Add(descriptor[2], 0);
    }

    public async Task<int> ControlAsync(byte[] setup, byte[] buffer, CancellationToken token)
    {
        if (setup.Length != 8) { throw new ArgumentException("USB setup packet must contain 8 bytes."); }
        IntPtr handle = UsbControlRouting.SelectHandle(setup, interfaces, FindPipe);
        // WinUSB supports only the already selected first configuration.
        if (setup[0] == 0 && setup[1] == 9)
        {
            byte[] configuration = new byte[1];
            await ControlAsync([0x80, 8, 0, 0, 0, 0, 1, 0], configuration, token);
            if (configuration[0] != setup[2]) { throw new Win32Exception(50, "WinUSB cannot select another configuration."); }
            return 0;
        }
        if (setup[0] == 1 && setup[1] == 11)
        {
            SetAlternate(setup[4], setup[2]);
            return 0;
        }
        return await SubmitAsync(handle, buffer, (pointer, overlapped) =>
            Api.WinUsb_ControlTransfer(handle, BitConverter.ToUInt64(setup), pointer, (uint)buffer.Length, IntPtr.Zero, overlapped), token);
    }

    public Task<int> PipeAsync(byte endpoint, byte[] buffer, CancellationToken token)
    {
        IntPtr handle = FindPipe(endpoint);
        return SubmitAsync(handle, buffer, (pointer, overlapped) => (endpoint & 128) != 0
            ? Api.WinUsb_ReadPipe(handle, endpoint, pointer, (uint)buffer.Length, IntPtr.Zero, overlapped)
            : Api.WinUsb_WritePipe(handle, endpoint, pointer, (uint)buffer.Length, IntPtr.Zero, overlapped), token);
    }

    public void ResetPipe(byte endpoint) { if (!Api.WinUsb_ResetPipe(FindPipe(endpoint), endpoint)) { throw Error(); } }
    public void AbortPipe(byte endpoint) { if (!Api.WinUsb_AbortPipe(FindPipe(endpoint), endpoint)) { throw Error(); } }
    public ushort GetPortStatus()
    {
        uint length = 1;
        if (!Api.WinUsb_QueryDeviceInformation(interfaces.First().Value, 1, ref length, out byte speed)) { throw Error(); }
        // Native getPortStatusImpl (0x1407195d0), including its legacy speed mapping.
        return speed == 1 ? (ushort)0x303 : speed == 3 ? (ushort)0x503 : (ushort)0x103;
    }
    public bool IsIsochronous(byte endpoint)
    {
        // Native reset/abort checks all alternate descriptors, including currently inactive ISO endpoints.
        for (int offset = 0; offset + 2 <= configurationDescriptor.Length;)
        {
            int length = configurationDescriptor[offset];
            if (length < 2 || offset + length > configurationDescriptor.Length) { break; }
            if (configurationDescriptor[offset + 1] == 5 && length >= 7 && configurationDescriptor[offset + 2] == endpoint)
            { return (configurationDescriptor[offset + 3] & 3) == 1; }
            offset += length;
        }
        return false;
    }
    public void ResetInterfaces()
    {
        foreach (byte number in interfaces.Keys) { SetAlternate(number, 0); }
    }

    public async Task<(int Length, byte[] Packets)> IsochronousAsync(byte endpoint, byte[] buffer, byte[] requestedPackets, CancellationToken token)
    {
        IntPtr handle = FindPipe(endpoint);
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        IntPtr packets = Marshal.AllocHGlobal(requestedPackets.Length);
        IntPtr registration = IntPtr.Zero;
        try
        {
            byte[] nativePackets = new byte[requestedPackets.Length];
            int requestedOffset = 0;
            for (int index = 0; index < nativePackets.Length; index += 12)
            {
                int requested = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(requestedPackets.AsSpan(index));
                if (requested < 0 || requested > buffer.Length - requestedOffset) { throw new InvalidDataException("Invalid requested isochronous packet length."); }
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nativePackets.AsSpan(index), requestedOffset);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nativePackets.AsSpan(index + 4), requested);
                requestedOffset += requested;
            }
            Marshal.Copy(nativePackets, 0, packets, nativePackets.Length);
            if (!Api.WinUsb_RegisterIsochBuffer(handle, endpoint, pinned.AddrOfPinnedObject(), (uint)buffer.Length, out registration)) { throw Error(); }
            int transferred = await SubmitAsync(handle, buffer, (_, overlapped) => (endpoint & 128) != 0
                ? Api.WinUsb_ReadIsochPipeAsap(registration, 0, (uint)buffer.Length, false, (uint)(requestedPackets.Length / 12), packets, overlapped)
                : Api.WinUsb_WriteIsochPipeAsap(registration, 0, (uint)buffer.Length, false, overlapped), token);
            byte[] result = new byte[requestedPackets.Length];
            Marshal.Copy(packets, result, 0, result.Length);
            if ((endpoint & 128) != 0)
            {
                byte[] wireBuffer = new byte[buffer.Length];
                transferred = 0;
                requestedOffset = 0;
                for (int index = 0; index < result.Length; index += 12)
                {
                    int offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(index));
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(index + 4));
                    int requested = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(requestedPackets.AsSpan(index));
                    int status = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(index + 8));
                    if (offset < 0 || offset > buffer.Length || length < 0 || length > buffer.Length - offset || length > requested)
                    { throw new InvalidDataException("WinUSB returned invalid isochronous packet bounds."); }
                    buffer.AsSpan(offset, length).CopyTo(wireBuffer.AsSpan(requestedOffset));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(index), requested);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(index + 8), status == 0 ? 0 : -1);
                    transferred = checked(transferred + length);
                    requestedOffset += requested;
                }
                wireBuffer.CopyTo(buffer, 0);
            }
            else
            {
                transferred = requestedOffset;
                for (int index = 0; index < result.Length; index += 12)
                {
                    int requested = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(requestedPackets.AsSpan(index));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(index), requested);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(index + 4), requested);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(index + 8), 0);
                }
            }
            return (transferred, result);
        }
        finally
        {
            if (registration != IntPtr.Zero) { Api.WinUsb_UnregisterIsochBuffer(registration); }
            Marshal.FreeHGlobal(packets); pinned.Free();
        }
    }
    public void SetAlternate(byte number, byte alternate)
    {
        if (!interfaces.TryGetValue(number, out IntPtr handle)) { throw new Win32Exception(1167); }
        if (!Api.WinUsb_SetCurrentAlternateSetting(handle, alternate)) { throw Error(); }
        alternateSettings[number] = alternate;
    }

    private IntPtr FindPipe(byte endpoint) => FindPipeDetails(endpoint).Handle;
    private (IntPtr Handle, int Type) FindPipeDetails(byte endpoint)
    {
        foreach (var (number, handle) in interfaces)
        {
            for (byte index = 0; Api.WinUsb_QueryPipe(handle, alternateSettings[number], index, out PipeInformation pipe); index++)
            {
                if (pipe.PipeId == endpoint) { return (handle, pipe.Type); }
                if (index == 254) { break; }
            }
        }
        // Native interface resolver leaves the root handle selected when an endpoint is unknown.
        // Let WinUSB reject the pipe; reporting device-disconnected here changes native -12/-5 errors.
        byte numberFromDescriptor = 0;
        for (int offset = 0; offset + 2 <= configurationDescriptor.Length;)
        {
            int length = configurationDescriptor[offset];
            if (length < 2 || offset + length > configurationDescriptor.Length) { break; }
            if (configurationDescriptor[offset + 1] == 4 && length >= 9) { numberFromDescriptor = configurationDescriptor[offset + 2]; }
            if (configurationDescriptor[offset + 1] == 5 && length >= 7 && configurationDescriptor[offset + 2] == endpoint
                && interfaces.TryGetValue(numberFromDescriptor, out IntPtr candidate))
            { return (candidate, configurationDescriptor[offset + 3] & 3); }
            offset += length;
        }
        if (interfaces.Count != 0) { return (interfaces.First().Value, -1); }
        throw new Win32Exception(1167);
    }

    private async Task<int> SubmitAsync(IntPtr handle, byte[] buffer, Func<IntPtr, IntPtr, bool> submit, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var completion = new EventWaitHandle(false, EventResetMode.ManualReset);
        IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedData>());
        GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(new OverlappedData { Event = completion.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            token.ThrowIfCancellationRequested();
            bool immediate = submit(pin.AddrOfPinnedObject(), overlapped);
            int error = immediate ? 0 : Marshal.GetLastWin32Error();
            if (!immediate && error != 997) { throw new UsbBackendException(error, error == 6 ? -19 : -12); }
            using CancellationTokenRegistration cancellation = token.Register(() => Api.CancelIoEx(file!, overlapped));
            if (!immediate)
            {
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(completion, (_, _) => finished.TrySetResult(), null, -1, true);
                try { await finished.Task; }
                finally { wait.Unregister(null); }
            }
            if (!Api.WinUsb_GetOverlappedResult(interfaces.First().Value, overlapped, out uint transferred, false)) { throw Error(); }
            return checked((int)transferred);
        }
        finally { pin.Free(); Marshal.FreeHGlobal(overlapped); }
    }

    public async ValueTask DisposeAsync()
    {
        await DriverChanges.WaitAsync();
        try { await DisposeCoreAsync(); }
        finally { DriverChanges.Release(); }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (disposed) { return; }
        disposed = true;
        try
        {
        if (file is { IsInvalid: false }) { Api.CancelIoEx(file, IntPtr.Zero); }
        foreach (IntPtr handle in interfaces.Values.Reverse()) { Api.WinUsb_Free(handle); }
        interfaces.Clear(); file?.Dispose(); file = null;
        if (changed)
        {
            try
            {
                await Task.Run(() => InstallDriver(instanceId, originalInf));
                using RegistryKey parameters = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId + @"\Device Parameters");
                if (originalInterfaceGuid is null) { parameters.DeleteValue("DeviceInterfaceGUID", false); }
                else { parameters.SetValue("DeviceInterfaceGUID", originalInterfaceGuid, RegistryValueKind.String); }
                changed = false;
                WriteJournal("Restored");
            }
            catch (Exception error)
            {
                WriteJournal("RestoreFailed", error.ToString());
                throw;
            }
        }
        }
        finally { if (inventoryKey is not null) { SharedDevices.TryRemove(inventoryKey, out _); } }
    }

    private void WriteJournal(string phase, string? error = null)
    {
        if (journal is null) { return; }
        string temporary = journal + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { InstanceId = instanceId, OriginalInf = originalInf,
            OriginalInterfaceGuid = originalInterfaceGuid, Phase = phase, Error = error, UpdatedUtc = DateTimeOffset.UtcNow, OwnerProcessId = Environment.ProcessId }));
        File.Move(temporary, journal, true);
    }

    public static void RestoreFromJournal(string path)
    {
        DriverChanges.Wait();
        try { RestoreJournalCore(path); }
        finally { DriverChanges.Release(); }
    }

    private static void RestoreJournalCore(string path)
    {
        RequireAdministrator();
        LeaseJournal saved = JsonSerializer.Deserialize<LeaseJournal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid USB lease journal.");
        if (string.IsNullOrWhiteSpace(saved.InstanceId) || string.IsNullOrWhiteSpace(saved.OriginalInf)
            || !saved.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(saved.OriginalInf) != saved.OriginalInf || !saved.OriginalInf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
            || (saved.OriginalInterfaceGuid is not null && !Guid.TryParse(saved.OriginalInterfaceGuid, out _)))
        { throw new InvalidDataException("Invalid USB recovery instance or INF name."); }
        if (saved.Phase == "Restored") { return; }
        // A crash can leave Binding in the journal after Windows has already
        // restored the original driver. Do not start a redundant PnP transaction.
        if (!HasStartedOriginalDriver(saved.InstanceId, saved.OriginalInf)) { InstallDriver(saved.InstanceId, saved.OriginalInf); }
        using (RegistryKey parameters = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Enum\" + saved.InstanceId + @"\Device Parameters"))
        {
            if (saved.OriginalInterfaceGuid is null) { parameters.DeleteValue("DeviceInterfaceGUID", false); }
            else { parameters.SetValue("DeviceInterfaceGUID", saved.OriginalInterfaceGuid, RegistryValueKind.String); }
        }
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(saved with { Phase = "Restored", Error = null, UpdatedUtc = DateTimeOffset.UtcNow }));
        File.Move(temporary, path, true);
    }

    private sealed record LeaseJournal(string InstanceId, string OriginalInf, string? OriginalInterfaceGuid, string Phase, string? Error, DateTimeOffset UpdatedUtc);
    private static bool HasStartedOriginalDriver(string id, string inf)
    {
        using RegistryKey? node = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id);
        if (node?.GetValue("Driver") is not string key) { return false; }
        using RegistryKey? driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + key);
        if (driver?.GetValue("InfPath") is not string current || !current.Equals(inf, StringComparison.OrdinalIgnoreCase)) { return false; }
        uint devnode = 0;
        return Api.CM_Locate_DevNodeW(ref devnode, id, 0) == 0
            && Api.CM_Get_DevNode_Status(out uint status, out uint problem, devnode, 0) == 0
            && problem == 0 && (status & 8) != 0;
    }
    private static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { throw new UnauthorizedAccessException("USB driver binding requires an elevated Windows process."); }
    }

    private static void DetachDriver(string id)
    {
        IntPtr set = Api.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) { throw Error(); }
        try
        {
            var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!Api.SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref device)) { throw Error(); }
            if (!Api.DiInstallBestDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 4, out bool reboot)) { throw Error(); }
            if (reboot) { throw new IOException("Detaching the USB driver requires a Windows restart."); }
        }
        finally { Api.SetupDiDestroyDeviceInfoList(set); }
    }

    private static void InstallDriver(string id, string inf)
    {
        IntPtr set = Api.SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) { throw Error(); }
        try
        {
            var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!Api.SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, 0, ref device)) { throw Error(); }
            var parameters = new InstallParameters { Size = (uint)Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            if (!Api.SetupDiGetDeviceInstallParamsW(set, ref device, ref parameters)) { throw Error(); }
            parameters.Flags |= 0x10000; // DI_ENUMSINGLEINF
            parameters.FlagsEx |= 0x800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            parameters.DriverPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
            if (!Api.SetupDiSetDeviceInstallParamsW(set, ref device, ref parameters)
                || !Api.SetupDiBuildDriverInfoList(set, ref device, 1)) { throw Error(); }
            var info = new DriverInfo { Size = (uint)Marshal.SizeOf<DriverInfo>(), Description = "", Manufacturer = "", Provider = "" };
            // Binding WinUSB deliberately selects its first class driver. Restoring the
            // original INF asks Windows for its matching driver to avoid another model.
            if (inf.Equals("winusb.inf", StringComparison.OrdinalIgnoreCase))
            {
                if (!Api.SetupDiEnumDriverInfoW(set, ref device, 1, 0, ref info)) { throw Error(); }
                if (!Api.DiInstallDevice(IntPtr.Zero, set, ref device, ref info, 0, out bool reboot)) { throw Error(); }
                if (reboot) { throw new IOException("Driver installation requires a Windows restart."); }
            }
            else
            {
                Api.SetupDiDestroyDriverInfoList(set, ref device, 1);
                parameters.Flags &= ~0x10000u;
                parameters.DriverPath = "";
                if (!Api.SetupDiSetDeviceInstallParamsW(set, ref device, ref parameters)) { throw Error(); }
                if (!Api.DiInstallBestDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 0, out bool reboot)) { throw Error(); }
                if (reboot) { throw new IOException("Restoring the USB driver requires a Windows restart."); }
            }
        }
        finally { Api.SetupDiDestroyDeviceInfoList(set); }
    }

    private static string FindDevice(string key)
    {
        IntPtr set = Api.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 6);
        if (set == new IntPtr(-1)) { throw Error(); }
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!Api.SetupDiEnumDeviceInfo(set, index, ref device)) { break; }
                byte[] data = new byte[1024];
                if (!Api.SetupDiGetDeviceRegistryPropertyW(set, ref device, 9, out _, data, data.Length, out _)) { continue; }
                if (!Encoding.Unicode.GetString(data).TrimEnd('\0').Equals(key, StringComparison.OrdinalIgnoreCase)) { continue; }
                var id = new StringBuilder(512);
                if (!Api.SetupDiGetDeviceInstanceIdW(set, ref device, id, id.Capacity, out _)) { throw Error(); }
                return id.ToString();
            }
        }
        finally { Api.SetupDiDestroyDeviceInfoList(set); }
        throw new IOException("Cannot resolve the physical USB devnode from its hub driver key.");
    }

    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct OverlappedData { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PipeInformation { public int Type; public byte PipeId; public ushort MaximumPacketSize; public byte Interval; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct InstallParameters
    {
        public uint Size, Flags, FlagsEx; public IntPtr Parent, Handler, Context, FileQueue; public UIntPtr ClassReserved; public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DriverInfo
    {
        public uint Size, Type; public UIntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
        public long Date; public ulong Version;
    }
    private static class Api
    {
        [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] internal static extern uint CM_Locate_DevNodeW(ref uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr parent);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo device);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref DeviceInfo device);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo device, uint property, out uint type, byte[] data, int size, out uint needed);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo device, StringBuilder id, int size, out int needed);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref DeviceInfo device, ref InstallParameters parameters);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref DeviceInfo device, ref InstallParameters parameters);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref DeviceInfo device, uint type);
        [DllImport("setupapi.dll", SetLastError=true)] internal static extern bool SetupDiDestroyDriverInfoList(IntPtr set, ref DeviceInfo device, uint type);
        [DllImport("setupapi.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref DeviceInfo device, uint type, uint index, ref DriverInfo driver);
        [DllImport("newdev.dll", SetLastError=true)] internal static extern bool DiInstallDevice(IntPtr parent, IntPtr set, ref DeviceInfo device, ref DriverInfo driver, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
        [DllImport("newdev.dll", EntryPoint="DiInstallDevice", SetLastError=true)] internal static extern bool DiInstallBestDevice(IntPtr parent, IntPtr set, ref DeviceInfo device, IntPtr driver, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
        [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_QueryDeviceInformation(IntPtr handle, uint type, ref uint length, out byte value);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_Free(IntPtr handle);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_GetAssociatedInterface(IntPtr handle, byte index, out IntPtr associated);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, [Out] byte[] descriptor);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte index, out PipeInformation information);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_ControlTransfer(IntPtr handle, ulong setup, IntPtr buffer, uint length, IntPtr transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, IntPtr buffer, uint length, IntPtr transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, IntPtr buffer, uint length, IntPtr transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_ResetPipe(IntPtr handle, byte pipe);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipe);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_SetCurrentAlternateSetting(IntPtr handle, byte alternate);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_RegisterIsochBuffer(IntPtr handle, byte pipe, IntPtr buffer, uint length, out IntPtr registration);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_UnregisterIsochBuffer(IntPtr registration);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_ReadIsochPipeAsap(IntPtr registration, uint offset, uint length, [MarshalAs(UnmanagedType.Bool)] bool continuation, uint count, IntPtr packets, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError=true)] internal static extern bool WinUsb_WriteIsochPipeAsap(IntPtr registration, uint offset, uint length, [MarshalAs(UnmanagedType.Bool)] bool continuation, IntPtr overlapped);
    }
}
