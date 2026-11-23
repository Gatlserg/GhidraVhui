using System.Text.Json;

namespace Vhui.Core.Windows;

internal static class UsbLeaseRecovery
{
    internal static bool CanRecover(JsonElement record, Func<int, bool> isProcessAlive)
    {
        string? phase = record.GetProperty("Phase").GetString();
        if (phase == "Restored") { return false; }
        // All WinUSB handles are closed before RestoreFailed is written. The daemon
        // may remain alive after a cable removal without owning an active backend.
        if (phase == "RestoreFailed") { return true; }
        return record.TryGetProperty("OwnerProcessId", out JsonElement owner) && owner.ValueKind == JsonValueKind.Number && owner.TryGetInt32(out int pid) && !isProcessAlive(pid);
    }

    internal static void Recover(string directory, Func<int, bool> isProcessAlive, Action<string> restore, Action<string, Exception>? onError)
    {
        if (!Directory.Exists(directory)) { return; }
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using JsonDocument record = JsonDocument.Parse(File.ReadAllText(path));
                if (CanRecover(record.RootElement, isProcessAlive)) { restore(path); }
            }
            catch (Exception error) when (onError is not null) { onError(path, error); }
        }
    }
}
