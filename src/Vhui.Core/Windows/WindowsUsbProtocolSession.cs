using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Net;
using Vhui.Core.Protocol;

namespace Vhui.Core.Windows;

public sealed class WindowsUsbProtocolSession(UsbDeviceEntry entry, UsbOwner owner, string serial,
    UsbServerOptions options, Func<UsbPhysicalDevice, CancellationToken, Task<IUsbDeviceBackend>> capture, TextWriter log) : IUsbProtocolSession
{
    private UsbPhysicalDevice device = entry.Device;
    private IUsbDeviceBackend? lease;
    private string password = "";
    private bool ownsDevice;
    private readonly ConcurrentDictionary<(ulong, ulong), CancellationTokenSource> transfers = [];
    public bool Captured => lease is not null;
    private static void Cancel(CancellationTokenSource cancellation) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    public void CancelPending() { foreach (CancellationTokenSource cancellation in transfers.Values) { Cancel(cancellation); } }

    public async Task<IReadOnlyList<VirtualHereMessage>> HandleAsync(VirtualHereMessage request, CancellationToken token)
    {
        if (request.DeviceAddress != entry.Address) { throw new InvalidDataException("Request does not address the exported physical device."); }
        if (request.Type == 0x42)
        {
            if (request.Payload.Length > 128) { throw new InvalidDataException("Device password exceeds native limit."); }
            password = System.Text.Encoding.UTF8.GetString(request.Payload).TrimEnd('\0');
            return [];
        }
        if (request.Type == 0x19)
        {
            int status = -2;
            if (lease is not null) { status = 1; }
            else if (!entry.Present) { status = -19; }
            else if (entry.TryOwn(owner))
            {
                device = entry.Device;
                ownsDevice = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    int? auth = await options.HookAsync(options.Configuration.GetString("ClientAuthorization", expandEnvironment: false), Context(), token);
                    // Original exit table at 0x1409689e0: 0=deny, 1=allow, 2/3=request credentials.
                    status = auth switch { null or 1 => 1, 0 => -4, 2 => -7, 3 => -8, _ => -1 };
                    if (status == 1)
                    {
                        await options.HookAsync(options.DeviceHook("onBind", device), Context(), token);
                        lease = await capture(device, token);
                    }
                    else { entry.Release(owner.ConnectionId); ownsDevice = false; }
                }
                catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    log.WriteLine($"USB capture failed: {exception}");
                    entry.Release(owner.ConnectionId); ownsDevice = false; status = -1;
                }
            }
            var replies = new List<VirtualHereMessage> { UsbWireFormat.CaptureReply(request, device with { Product = entry.DisplayName }, serial, status) };
            if (Captured) { replies.Add(DeviceInformation(request)); }
            return replies;
        }
        if (request.Type == 0x23)
        {
            await ReleaseAsync();
            return [UsbWireFormat.StatusReply(request, 7, 1), DeviceInformation(request)];
        }
        if (request.Type == 0x21)
        {
            // Native 0x140741710 only ACKs; it does not identify/cancel a pending URB.
            return [UsbWireFormat.StatusReply(request, 0x22, 0, 12)];
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Header bytes 1..4 are not a recovered deadline. WinUSB retains its native policies.
        var keyId = (request.TokenA, request.TokenC);
        if (!transfers.TryAdd(keyId, cancellation)) { throw new InvalidDataException("Duplicate outstanding USB request ID."); }
        try
        {
            if (request.Type == 13)
            {
                var (setup, buffer, input) = UsbWireFormat.ParseControl(request);
                int actual = 0, status = 0;
                try { actual = await RequireLease().ControlAsync(setup, buffer, cancellation.Token); }
                catch (Exception error) when (error is Win32Exception or OperationCanceledException) { status = Status(error); }
                return [UsbWireFormat.ControlReply(request, setup, buffer, input, status, actual)];
            }
            if (request.Type is 0x12 or 0x4a)
            {
                if (request.Payload.Length < 13) { throw new InvalidDataException("Pipe request is truncated."); }
                byte endpoint = request.Payload[0];
                int length = BinaryPrimitives.ReadInt32LittleEndian(request.Payload.AsSpan(9));
                if (length is < 0 or > 16 * 1024 * 1024) { throw new InvalidDataException("Invalid pipe transfer length."); }
                bool input = (endpoint & 128) != 0;
                if (request.Payload.Length != 13 + (input ? 0 : length)) { throw new InvalidDataException("Pipe payload length mismatch."); }
                byte[] buffer = new byte[length];
                if (!input) { request.Payload.AsSpan(13).CopyTo(buffer); }
                int actual = 0, status = 0;
                try { actual = await RequireLease().PipeAsync(endpoint, buffer, cancellation.Token); }
                catch (Exception error) when (error is Win32Exception or OperationCanceledException)
                { status = error is Win32Exception { NativeErrorCode: 87 } ? -12 : Status(error); }
                // Native pipe completion payload contains actual bytes, followed by data for IN.
                byte[] reply = new byte[4 + (input ? length : 0)];
                BinaryPrimitives.WriteInt32LittleEndian(reply, status < 0 ? status : actual);
                if (input) { buffer.CopyTo(reply, 4); }
                return [request with { Type = 0x13, Reserved = 0, Payload = reply }];
            }
            if (request.Type is 0x24 or 0x4d)
            {
                if (request.Payload.Length < 17) { throw new InvalidDataException("Isochronous request is truncated."); }
                byte endpoint = request.Payload[0];
                int length = BinaryPrimitives.ReadInt32LittleEndian(request.Payload.AsSpan(5));
                int count = BinaryPrimitives.ReadInt32LittleEndian(request.Payload.AsSpan(13));
                bool input = (endpoint & 128) != 0;
                if (length is < 1 or > 16 * 1024 * 1024 || count is < 1 or > 8192
                    || request.Payload.Length != 17 + count * 12 + (input ? 0 : length)) { throw new InvalidDataException("Invalid isochronous bounds."); }
                byte[] buffer = new byte[length];
                byte[] packets = request.Payload.AsSpan(17, count * 12).ToArray();
                if (!input) { request.Payload.AsSpan(17 + count * 12).CopyTo(buffer); }
                int actual = 0, status = 0;
                try { (actual, packets) = await RequireLease().IsochronousAsync(endpoint, buffer, packets, cancellation.Token); }
                catch (Exception error) when (error is Win32Exception or OperationCanceledException) { status = Status(error); }
                return [UsbWireFormat.IsochronousReply(request, buffer, packets, input, status, actual)];
            }
            byte responseType = request.Type switch { 0x14 => 0x15, 0x16 or 0x33 => 0x17, 0x1a => 0x1b, 0x1c => 0x1d, 0x1e => 0x1f, 0x32 => 0x20,
                0x46 => 0x47, 0x48 => 0x49, _ => throw new NotSupportedException($"USB opcode {request.Type:X2} is not implemented.") };
            int result = 0;
            ushort portStatus = 0x103;
            try
            {
                IUsbDeviceBackend active = RequireLease();
                if (request.Type == 0x14) { portStatus = active.GetPortStatus(); }
                else if (request.Type is 0x16 or 0x33)
                {
                    // Native Windows reset is the onReset hook, not SET_INTERFACE(0).
                    if (await RunHookAsync("onReset", cancellation.Token)) { result = 1; }
                    else if (request.Type == 0x33)
                    {
                        // Handler 0x140741e10 and worker assembly 0x140711861..18bb:
                        // configuration byte, interface count, sixteen alternate-setting BYTES.
                        if (request.Payload.Length != 18 || request.Payload[1] > 16) { throw new InvalidDataException("Invalid extended reset interface array."); }
                        byte configuration = request.Payload[0], count = request.Payload[1];
                        if (configuration > 1) { result = -5; }
                        else if (configuration == 1)
                        { for (byte index = 0; index < count; index++) { active.SetAlternate(index, request.Payload[2 + index]); } }
                    }
                }
                else if (request.Type is 0x46 or 0x48) { result = -5; } // Native worker case 6/7 returns 0xfffffffb on Windows.
                else
                {
                    if (request.Payload.Length < 4) { throw new InvalidDataException("Endpoint request is truncated."); }
                    byte endpoint = request.Payload[0];
                    if (request.Type == 0x1a)
                    { if (await RunHookAsync("onClearHalt", cancellation.Token)) { result = 1; } else { active.ResetPipe(endpoint); } }
                    else if (request.Type == 0x1c)
                    { if (await RunHookAsync("onResetEp", cancellation.Token)) { result = 1; } else if (!active.IsIsochronous(endpoint)) { active.ResetPipe(endpoint); } }
                    else if (request.Type == 0x1e)
                    { if (!active.IsIsochronous(endpoint)) { active.AbortPipe(endpoint); } }
                    else if (request.Type == 0x32)
                    {
                        // Native worker case 8 ignores individual errors and always completes with zero.
                        try { if (!active.IsIsochronous(endpoint)) { active.AbortPipe(endpoint); } } catch (Win32Exception) { }
                        try { if (!await RunHookAsync("onResetEp", cancellation.Token) && !active.IsIsochronous(endpoint)) { active.ResetPipe(endpoint); } } catch (Win32Exception) { }
                        try { if (!await RunHookAsync("onClearHalt", cancellation.Token)) { active.ResetPipe(endpoint); } } catch (Win32Exception) { }
                    }
                }
            }
            catch (Win32Exception error) { result = request.Type == 0x14 ? -19 : Status(error); }
            VirtualHereMessage response = UsbWireFormat.StatusReply(request, responseType, result, request.Type == 0x14 ? 6 : 4);
            if (request.Type == 0x14) { BinaryPrimitives.WriteUInt16LittleEndian(response.Payload.AsSpan(4), portStatus); }
            return [response];
        }
        finally { transfers.TryRemove(keyId, out _); }
    }

    public VirtualHereMessage DeviceInformation(VirtualHereMessage request)
    {
        return entry.Information(request, options.Enabled("HideClientInfo"));
    }
    private Dictionary<string, string> Context() => new()
    {
        ["CLIENT_ID"] = owner.ClientId, ["CLIENT_IP"] = owner.Address.ToString(), ["CLIENT_HOSTNAME"] = owner.Hostname,
        ["PASSWORD"] = password, ["VENDOR_ID"] = device.VendorId.ToString("x4"), ["PRODUCT_ID"] = device.ProductId.ToString("x4"),
        ["PRODUCT_SERIAL"] = device.SerialNumber ?? "", ["DEVPATH"] = UsbDeviceRegistry.Key(device),
        ["ADDRESS"] = entry.Address.ToString(), ["NUM_BINDINGS"] = "1", ["NICKNAME"] = entry.DisplayName
    };
    private async Task<bool> RunHookAsync(string name, CancellationToken token)
    {
        string? hook = options.DeviceHook(name, device);
        if (hook is null) { return false; }
        await options.HookAsync(hook, Context(), token); return true;
    }
    private IUsbDeviceBackend RequireLease() => entry.Present && lease is not null ? lease : throw new Win32Exception(1167);
    private static int Status(Exception error) => error switch
    {
        UsbBackendException backend => backend.WireStatus,
        OperationCanceledException => -104,
        Win32Exception win32 => win32.NativeErrorCode switch { 995 => -104, 121 or 1460 => -110, 1167 or 2 => -19, 31 => -32, _ => -5 },
        _ => -5
    };
    private async Task ReleaseAsync()
    {
        try
        {
            if (lease is not null)
            {
                try { await lease.DisposeAsync(); }
                finally { lease = null; }
                await options.HookAsync(options.DeviceHook("onUnbind", device), Context(), CancellationToken.None);
                await options.HookAsync(options.Configuration.GetString("ClientDeauthorization", expandEnvironment: false), Context(), CancellationToken.None);
            }
        }
        finally { if (ownsDevice) { ownsDevice = false; entry.Release(owner.ConnectionId); } }
    }
    public async ValueTask DisposeAsync() { CancelPending(); await ReleaseAsync(); }
    public static bool Supports(byte type) => type is 0x0d or 0x12 or 0x14 or 0x16 or 0x19 or 0x1a or 0x1c or 0x1e or 0x21 or 0x23 or 0x24 or 0x32 or 0x33 or 0x42 or 0x46 or 0x48 or 0x4a or 0x4d;
}
