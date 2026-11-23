using System.ComponentModel;

namespace Vhui.Core.Windows;

public static class UsbControlRouting
{
    public static IntPtr SelectHandle(byte[] setup, IReadOnlyDictionary<byte, IntPtr> interfaces, Func<byte, IntPtr> findEndpoint)
    {
        if (setup.Length != 8 || interfaces.Count == 0) { throw new Win32Exception(1167); }
        return (setup[0] & 31) switch
        {
            1 => interfaces.TryGetValue(setup[4], out IntPtr target) ? target : throw new Win32Exception(1167),
            2 when (setup[4] & 15) != 0 => findEndpoint(setup[4]),
            _ => interfaces.First().Value
        };
    }
}
