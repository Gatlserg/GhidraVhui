using System.Text;
using System.Text.Json;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;

namespace Vhui.Core.Configuration;

public sealed class UsbDeviceNames(ServerConfiguration configuration)
{
    private readonly object sync = new();
    private sealed record Alias(ushort Vendor, ushort Product, string? Serial, string Location, string Name);
    private List<Alias> Read() => JsonSerializer.Deserialize<List<Alias>>(configuration.GetString("DeviceAliases", "[]", expandEnvironment: false)!) ?? [];
    public static void Validate(string name)
    {
        if (name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name) > 63)
        { throw new ArgumentException("Device name must contain no control characters and fit in 63 UTF-8 bytes."); }
    }
    public string? AliasFor(UsbPhysicalDevice device)
    {
        lock (sync)
        {
            var aliases = Read().Where(value => value.Vendor == device.VendorId && value.Product == device.ProductId).ToArray();
            if (!string.IsNullOrEmpty(device.SerialNumber))
            {
                Alias? exact = aliases.FirstOrDefault(value => value.Serial == device.SerialNumber);
                if (exact is not null) { return exact.Name; }
            }
            return aliases.LastOrDefault(value => value.Location.Equals(UsbDeviceRegistry.Key(device), StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(device.SerialNumber) || string.IsNullOrEmpty(value.Serial)))?.Name;
        }
    }
    public string DisplayName(UsbPhysicalDevice device) => AliasFor(device)
        ?? (string.IsNullOrWhiteSpace(device.Product) ? $"USB {device.VendorId:X4}:{device.ProductId:X4}" : device.Product);
    public void Set(UsbPhysicalDevice device, string? name)
    {
        name = name?.Trim() ?? ""; Validate(name);
        lock (sync)
        {
            var aliases = Read(); string location = UsbDeviceRegistry.Key(device);
            bool Matches(Alias value) => value.Vendor == device.VendorId && value.Product == device.ProductId
                && ((!string.IsNullOrEmpty(device.SerialNumber) && value.Serial == device.SerialNumber)
                    || value.Location.Equals(location, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(value.Serial) || string.IsNullOrEmpty(device.SerialNumber)));
            string? serial = string.IsNullOrEmpty(device.SerialNumber) ? aliases.LastOrDefault(Matches)?.Serial : device.SerialNumber;
            aliases.RemoveAll(value => Matches(value));
            if (name.Length > 0) { aliases.Add(new(device.VendorId, device.ProductId, serial, location, name)); }
            configuration.Set("DeviceAliases", JsonSerializer.Serialize(aliases));
        }
    }
}
