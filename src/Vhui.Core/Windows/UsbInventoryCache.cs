using Vhui.Core.Protocol;

namespace Vhui.Core.Windows;

/// <summary>Keeps known descriptors when a hub confirms presence but cannot read metadata.</summary>
public sealed class UsbInventoryCache
{
    private readonly object sync = new();
    private readonly Dictionary<string, UsbPhysicalDevice> known = new(StringComparer.OrdinalIgnoreCase);

    public UsbInventoryResult Merge(UsbInventoryResult result)
    {
        lock (sync)
        {
            bool Uncertain(UsbPhysicalDevice device) => result.Errors.Any(error =>
                error.HubPath.Equals(device.HubPath, StringComparison.OrdinalIgnoreCase) && (error.Port == 0 || error.Port == device.Port));
            var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var devices = new List<UsbPhysicalDevice>();
            foreach (UsbPhysicalDevice value in result.Devices)
            {
                string key = UsbDeviceRegistry.Key(value); observed.Add(key);
                UsbPhysicalDevice device = value;
                if (known.TryGetValue(key, out UsbPhysicalDevice? previous) && Uncertain(value)
                    && previous.VendorId == value.VendorId && previous.ProductId == value.ProductId
                    && (string.IsNullOrEmpty(value.SerialNumber) || string.IsNullOrEmpty(previous.SerialNumber) || value.SerialNumber == previous.SerialNumber))
                {
                    device = value with
                    {
                        Manufacturer = string.IsNullOrEmpty(value.Manufacturer) ? previous.Manufacturer : value.Manufacturer,
                        Product = string.IsNullOrEmpty(value.Product) ? previous.Product : value.Product,
                        SerialNumber = string.IsNullOrEmpty(value.SerialNumber) ? previous.SerialNumber : value.SerialNumber,
                        ConfigurationDescriptor = value.ConfigurationDescriptor.Length >= 9 ? value.ConfigurationDescriptor : previous.ConfigurationDescriptor
                    };
                }
                known[key] = device; devices.Add(device);
            }
            foreach (string key in known.Keys.ToArray())
            {
                if (observed.Contains(key)) { continue; }
                if (Uncertain(known[key])) { devices.Add(known[key]); }
                else { known.Remove(key); }
            }
            return new(devices, result.Errors);
        }
    }
}
