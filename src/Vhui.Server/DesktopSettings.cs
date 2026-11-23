using System.Text.Json;
using Vhui.Core.Configuration;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;

namespace Vhui.Server;

internal sealed class DesktopSettings
{
    private readonly object sync = new();
    private readonly UsbSharingSelection shared;
    internal string Path { get; }
    internal ServerConfiguration Configuration { get; }
    internal UsbServerOptions Options => new() { Configuration = Configuration, ConfigurationPath = Path };
    internal UsbDeviceNames Names => new(Configuration);

    internal DesktopSettings(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bool exists = File.Exists(path);
        if (exists) { Configuration = ServerConfiguration.Load(path); }
        else
        {
            Configuration = new();
            Configuration.Set("ServerName", Environment.MachineName + " — USB Server");
            Configuration.Set("NetworkInterface", "127.0.0.1");
            Configuration.Set("TCPPort", "7575");
            Configuration.Set("UseAVAHI", "1");
            Configuration.Set("RemoteAdmin", "0");
            Configuration.Set("TraceMsgs", "0");
        }
        string? saved = Configuration.GetString("DesktopSharedDevices", expandEnvironment: false);
        shared = new(string.IsNullOrEmpty(saved) ? [] : JsonSerializer.Deserialize<string[]>(saved));
        if (!exists) { Save(); }
    }
    internal bool IsShared(UsbPhysicalDevice device) => shared.Contains(device);
    internal void SetShared(UsbPhysicalDevice device, bool value)
    {
        lock (sync)
        {
            shared.Set(device, value);
            Configuration.Set("DesktopSharedDevices", JsonSerializer.Serialize(shared.Snapshot()));
            Save();
        }
    }
    internal void Save() => Configuration.Save(Path);
}
