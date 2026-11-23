using Vhui.Core.Protocol;
using Vhui.Core.Windows;

namespace Vhui.Core.Configuration;

/// <summary>Desktop publication choices survive incomplete serial-number reads.</summary>
public sealed class UsbSharingSelection(IEnumerable<string>? saved = null)
{
    private readonly object sync = new();
    private readonly HashSet<string> shared = new(saved ?? [], StringComparer.OrdinalIgnoreCase);
    private static string Prefix(UsbPhysicalDevice device) => UsbDeviceRegistry.Key(device) + $"|{device.VendorId:X4}:{device.ProductId:X4}|";
    public bool Contains(UsbPhysicalDevice device)
    {
        lock (sync)
        {
            string prefix = Prefix(device);
            return shared.Contains(prefix + device.SerialNumber) || shared.Contains(prefix)
                || string.IsNullOrEmpty(device.SerialNumber) && shared.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
    public void Set(UsbPhysicalDevice device, bool enabled)
    {
        lock (sync)
        {
            string prefix = Prefix(device);
            // Disable must remove the original choice even if the serial is unreadable now.
            shared.RemoveWhere(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (enabled) { shared.Add(prefix + device.SerialNumber); }
        }
    }
    public string[] Snapshot() { lock (sync) { return shared.Order(StringComparer.OrdinalIgnoreCase).ToArray(); } }
}
