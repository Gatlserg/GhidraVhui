using System.Diagnostics;
using System.Net;
using Vhui.Core.Configuration;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

public sealed class UsbServerOptions
{
    public ServerConfiguration Configuration { get; init; } = new();
    public string? ConfigurationPath { get; init; }
    public string? FilePath(string key, string? fallback = null)
    {
        string? value = Configuration.GetString(key, fallback);
        if (string.IsNullOrEmpty(value)) { return value; }
        string directory = ConfigurationPath is null ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(ConfigurationPath))!;
        return Path.GetFullPath(value, directory);
    }
    public IPAddress BindAddress => IPAddress.Parse(Configuration.GetString("NetworkInterface", "127.0.0.1")!);
    public int Port => Number("TCPPort", 7575, 1, 65535);
    public int PingInterval => Number("PingInterval", 4, 1, 3600);
    public int PingTimeout => Number("PingTimeout", 9, 1, 86400);
    public int ScanInterval => Number("ScanInterval", 2, 1, 60);
    public string ServerName => Configuration.GetString("ServerName", "Vhui C# USB server")!
        .Replace("$HOSTNAME$", Environment.MachineName).Replace("$SERIAL$", ServerId.ToString("D"));
    public Guid ServerId => Guid.TryParse(Configuration.GetString("ServerId"), out Guid id) ? id
        : new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Vhui:" + Environment.MachineName)).AsSpan(0, 16));
    public bool Enabled(string key, bool fallback = false) => Configuration.GetString(key, fallback ? "1" : "0") switch
    { "1" => true, "0" => false, _ => throw new InvalidDataException($"{key} must be 0 or 1.") };
    public int Number(string key, int fallback, int min, int max)
    {
        string text = Configuration.GetString(key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture))!;
        if (!int.TryParse(text, out int value) || value < min || value > max) { throw new InvalidDataException($"Invalid {key}: {text}."); }
        return value;
    }
    public bool AllowsClient(IPAddress peer)
    {
        string allowed = Configuration.GetString("AllowedClients", "*")!;
        // An unset or blank client allowlist means there is no IP restriction.
        // Keep MatchesAddress strict: an empty AdminAllowedClients must still deny.
        return string.IsNullOrWhiteSpace(allowed) || MatchesAddress(allowed, peer);
    }
    public static bool MatchesAddress(string allowed, IPAddress peer)
    {
        if (peer.IsIPv4MappedToIPv6) { peer = peer.MapToIPv4(); }
        foreach (string value in allowed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (value == "*") { return true; }
            string[] parts = value.Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out IPAddress? network)) { throw new InvalidDataException("Invalid client network: " + value); }
            if (network.IsIPv4MappedToIPv6) { network = network.MapToIPv4(); }
            byte[] address = peer.GetAddressBytes(), subnet = network.GetAddressBytes();
            int bits = subnet.Length * 8;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > subnet.Length * 8)) { throw new InvalidDataException("Invalid client prefix: " + value); }
            if (address.Length != subnet.Length) { continue; }
            bool matches = true;
            for (int index = 0; index < address.Length; index++)
            {
                int remaining = Math.Clamp(bits - index * 8, 0, 8), mask = (255 << (8 - remaining)) & 255;
                if ((address[index] & mask) != (subnet[index] & mask)) { matches = false; break; }
            }
            if (matches) { return true; }
        }
        return false;
    }
    public bool Publishes(UsbPhysicalDevice device)
    {
        if (device.IsHub) { return false; }
        string path = device.HubPath + "." + device.Port;
        if (Configuration.GetString("IgnoredBuses", "")!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(item => path.Contains(item, StringComparison.OrdinalIgnoreCase))) { return false; }
        string allowed = Configuration.GetString("AllowedDevices", "")!;
        return !Matches(Configuration.GetString("IgnoredDevices", "")!, device)
            && (allowed.Length == 0 || Matches(allowed, device));
    }
    private static bool Matches(string list, UsbPhysicalDevice device)
        => list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(value =>
        {
            string[] ids = value.Replace(':', '/').Split('/');
            return ids.Length is >= 1 and <= 3 && (ids[0] == "*" || ushort.TryParse(ids[0], System.Globalization.NumberStyles.HexNumber, null, out ushort vid) && vid == device.VendorId)
                && (ids.Length == 1 || ids[1] == "*" || ushort.TryParse(ids[1], System.Globalization.NumberStyles.HexNumber, null, out ushort pid) && pid == device.ProductId)
                && (ids.Length < 3 || ids[2] == "*" || ids[2].Equals(device.SerialNumber, StringComparison.OrdinalIgnoreCase));
        });

    public string? DeviceHook(string name, UsbPhysicalDevice device)
    {
        string? specific = Configuration.GetString($"{name}.{device.VendorId:x4}.{device.ProductId:x4}", expandEnvironment: false);
        if (specific is not null) { return specific; }
        string product = device.Product ?? "";
        if (name.Equals("onReset", StringComparison.OrdinalIgnoreCase)
            && ((device.VendorId == 0x46d && (product.Contains("wheel", StringComparison.OrdinalIgnoreCase) || product.Contains("driving", StringComparison.OrdinalIgnoreCase)))
                || device.VendorId == 0x44f && product.Contains("wheel", StringComparison.OrdinalIgnoreCase)
                || device.VendorId == 0x9886 && device.ProductId == 1)) { return ""; }
        return Configuration.GetString($"{name}.{device.VendorId:x4}", expandEnvironment: false)
            ?? Configuration.GetString(name, expandEnvironment: false);
    }

    public async Task<int?> HookAsync(string? command, IReadOnlyDictionary<string, string> context, CancellationToken token)
    {
        if (command is null) { return null; }
        if (command.Length == 0) { return 0; }
        if (command.Equals("power_cycle_port", StringComparison.OrdinalIgnoreCase))
        { throw new UsbBackendException(50, -95); }
        foreach (var (name, value) in context)
        {
            string placeholder = "$" + name + "$";
            if (!command.Contains(placeholder, StringComparison.Ordinal)) { continue; }
            // Do not interpolate shell metacharacters supplied by a remote client.
            // Such data remains available to an executable via the VH_* environment.
            if (value.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '\r', '\n']) >= 0)
            { throw new InvalidDataException($"Unsafe shell substitution {name}; use the VH_{name} environment in your hook program instead."); }
            command = command.Replace(placeholder, value, StringComparison.Ordinal);
        }
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/D"); start.ArgumentList.Add("/S"); start.ArgumentList.Add("/C"); start.ArgumentList.Add(command);
        foreach (var (name, value) in context) { start.Environment["VH_" + name] = value; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(Number("HookTimeout", 30, 1, 3600)));
        using Process process = Process.Start(start) ?? throw new IOException("Hook process could not start.");
        try { await process.WaitForExitAsync(deadline.Token); return process.ExitCode; }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
    }
}
