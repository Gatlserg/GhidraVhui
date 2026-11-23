namespace Vhui.Core.Windows;

/// <summary>Device operations separated from framing so native wire behavior can be tested without rebinding hardware.</summary>
public interface IUsbDeviceBackend : IAsyncDisposable
{
    Task<int> ControlAsync(byte[] setup, byte[] buffer, CancellationToken token);
    Task<int> PipeAsync(byte endpoint, byte[] buffer, CancellationToken token);
    Task<(int Length, byte[] Packets)> IsochronousAsync(byte endpoint, byte[] buffer, byte[] packets, CancellationToken token);
    ushort GetPortStatus();
    void ResetPipe(byte endpoint);
    void AbortPipe(byte endpoint);
    bool IsIsochronous(byte endpoint);
    void SetAlternate(byte number, byte alternate);
}
