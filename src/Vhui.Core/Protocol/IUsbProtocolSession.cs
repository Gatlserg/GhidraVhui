namespace Vhui.Core.Protocol;

internal interface IUsbProtocolSession : IAsyncDisposable
{
    bool Captured { get; }
    VirtualHereMessage DeviceInformation(VirtualHereMessage request);
    void CancelPending();
    Task<IReadOnlyList<VirtualHereMessage>> HandleAsync(VirtualHereMessage request, CancellationToken token);
}
