using System.ComponentModel;

namespace Vhui.Core.Windows;

public sealed class UsbBackendException(int windowsError, int wireStatus) : Win32Exception(windowsError)
{
    public int WireStatus { get; } = wireStatus;
}
