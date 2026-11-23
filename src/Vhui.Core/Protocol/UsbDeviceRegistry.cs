using System.Buffers.Binary;
using System.Net;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

public sealed record UsbOwner(Guid ConnectionId, IPAddress Address, string ClientId, string Hostname);

public sealed class UsbDeviceEntry(UsbPhysicalDevice device, uint address, Vhui.Core.Configuration.UsbDeviceNames? names = null)
{
    private readonly object sync = new();
    private UsbOwner? owner;
    public UsbPhysicalDevice Device { get; private set; } = device;
    public uint Address { get; } = address;
    public bool Present { get; private set; } = true;
    public DateTimeOffset? CapturedAt { get; private set; }
    public event Action? Changed;
    public string DisplayName => names?.DisplayName(Device) ?? Device.Product ?? $"USB {Device.VendorId:X4}:{Device.ProductId:X4}";
    public UsbOwner? Owner { get { lock (sync) { return owner; } } }
    public bool TryOwn(UsbOwner candidate)
    {
        lock (sync) { if (!Present || owner is not null) { return false; } owner = candidate; CapturedAt = DateTimeOffset.UtcNow; }
        Changed?.Invoke(); return true;
    }
    public void Release(Guid candidate)
    {
        lock (sync) { if (owner?.ConnectionId != candidate) { return; } owner = null; CapturedAt = null; }
        Changed?.Invoke();
    }
    internal void Update(UsbPhysicalDevice? value)
    {
        if (value is not null && Present && (value.VendorId != Device.VendorId || value.ProductId != Device.ProductId
            || !string.IsNullOrEmpty(value.SerialNumber) && !string.IsNullOrEmpty(Device.SerialNumber) && value.SerialNumber != Device.SerialNumber))
        { Update(null); }
        bool changed;
        lock (sync) { changed = Present != (value is not null); Present = value is not null; if (value is not null) { Device = value; } }
        if (changed) { Changed?.Invoke(); }
    }
    public VirtualHereMessage Information(VirtualHereMessage request, bool hideClients = false)
    {
        VirtualHereMessage response = VirtualHereServer.BuildDeviceInformation(request with { DeviceAddress = Address }, Device with { Product = DisplayName });
        BinaryPrimitives.WriteUInt32LittleEndian(response.Payload.AsSpan(132), Address);
        UsbOwner? current = Owner;
        response.Payload[136] = !Present ? (byte)0 : current is null ? (byte)1 : (byte)3;
        if (current is not null)
        {
            current.ConnectionId.TryWriteBytes(response.Payload.AsSpan(137, 16));
            if (!hideClients)
            {
                IPAddress ip = current.Address.IsIPv4MappedToIPv6 ? current.Address.MapToIPv4() : current.Address;
                ip.GetAddressBytes().CopyTo(response.Payload, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 153 : 290);
            }
        }
        return response;
    }
}

public sealed class UsbDeviceRegistry(UsbServerOptions options, Func<UsbPhysicalDevice, bool>? publish = null)
{
    private readonly object sync = new();
    private readonly Dictionary<string, UsbDeviceEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly UsbInventoryCache inventory = new();
    private readonly Vhui.Core.Configuration.UsbDeviceNames names = new(options.Configuration);
    public event Action<UsbDeviceEntry>? Changed;
    public static string Key(UsbPhysicalDevice value) => value.HubPath + "#" + value.Port;
    public IReadOnlyList<UsbDeviceEntry> Devices { get { lock (sync) { return entries.Values.Where(value => value.Present).OrderBy(value => value.Address).ToArray(); } } }
    public UsbDeviceEntry? Find(uint address) { lock (sync) { return entries.Values.FirstOrDefault(value => value.Address == address); } }
    public void Refresh(IEnumerable<UsbPhysicalDevice> devices, IReadOnlyList<UsbInventoryError>? errors = null)
    {
        bool Publishes(UsbPhysicalDevice device) => options.Publishes(device) && (publish?.Invoke(device) ?? true);
        UsbInventoryResult snapshot = inventory.Merge(new(devices.ToArray(), errors ?? []));
        var observed = snapshot.Devices.Where(Publishes).ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var changes = new List<(UsbDeviceEntry, UsbPhysicalDevice?)>();
        lock (sync)
        {
            foreach (var (key, entry) in entries)
            {
                // Keep only the uncertain locations, not the entire old snapshot.
                bool uncertain = errors?.Any(error => error.HubPath.Equals(entry.Device.HubPath, StringComparison.OrdinalIgnoreCase)
                    && (error.Port == 0 || error.Port == entry.Device.Port)) == true;
                // A valid partial record restores presence even if metadata IOCTLs failed.
                // An explicit unshare takes effect even when that port currently has errors.
                if (observed.TryGetValue(key, out UsbPhysicalDevice? value)) { changes.Add((entry, value)); }
                else if (!Publishes(entry.Device) || !uncertain) { changes.Add((entry, null)); }
            }
            foreach (var (key, value) in observed)
            {
                if (entries.ContainsKey(key)) { continue; }
                if (!addresses.TryGetValue(key, out uint address))
                {
                    string setting = "DeviceAddress." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
                    if (!uint.TryParse(options.Configuration.GetString(setting), out address) || address == 0 || addresses.Values.Contains(address))
                    {
                        address = (uint)Math.Max(value.Port, 1);
                        while (addresses.Values.Contains(address)) { address++; }
                        options.Configuration.Set(setting, address.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    addresses.Add(key, address);
                }
                var entry = new UsbDeviceEntry(value, address, names);
                entry.Changed += () => Changed?.Invoke(entry);
                entries.Add(key, entry); changes.Add((entry, value));
                Changed?.Invoke(entry);
            }
        }
        foreach (var (entry, value) in changes) { entry.Update(value); }
    }
}
