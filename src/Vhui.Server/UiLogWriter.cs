using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Vhui.Server;

internal sealed class UiLogWriter : TextWriter
{
    private readonly ConcurrentQueue<string> lines = new();
    internal event Action<string>? EventReceived;
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (value is null) { return; }
        string text = value;
        try
        {
            using JsonDocument json = JsonDocument.Parse(value);
            string name = json.RootElement.GetProperty("Event").GetString() ?? "";
            string details = json.RootElement.TryGetProperty("Data", out JsonElement data) ? data.ToString() : value;
            string title = name switch
            {
                "Listening" => "Сервер принимает подключения", "ConnectionEnded" => "Клиент отключён",
                "ConnectionFailed" => "Ошибка подключения", "ClientDenied" => "Клиенту отказано в доступе",
                "InventoryErrors" => "Не удалось опросить некоторые USB-порты",
                "DeviceRestoreFailed" or "UsbRecoveryFailed" => "Не удалось восстановить драйвер",
                "ReverseRetry" => "Повтор обратного подключения", "MdnsCompleted" => "Обнаружение в сети",
                _ => name
            };
            if (name == "Listening" && json.RootElement.TryGetProperty("Data", out JsonElement listener))
            {
                details = UiText.Format("{0} {1}:{2}, устройств: {3}", listener.GetProperty("Tls").GetBoolean() ? "TLS" : "TCP", listener.GetProperty("Host").GetString()!, listener.GetProperty("Port").GetInt32(), listener.GetProperty("Devices").GetInt32());
            }
            if (name == "InventoryErrors" && json.RootElement.TryGetProperty("Data", out JsonElement inventory) && inventory.ValueKind == JsonValueKind.Array)
            {
                string signature = inventory.GetRawText();
                if (signature == lastInventoryError) { return; }
                lastInventoryError = signature;
                details = string.Join("; ", inventory.EnumerateArray().Select(error =>
                    UiText.Format("порт {0}, код {1}: {2}", error.GetProperty("Port").GetInt32(), error.GetProperty("ErrorCode").GetInt32(), error.GetProperty("Message").GetString()!)));
            }
            if (name == "ClientDenied" && json.RootElement.TryGetProperty("Data", out JsonElement denied) && denied.ValueKind == JsonValueKind.Object && denied.TryGetProperty("Address", out JsonElement peer))
            {
                details = UiText.Format("{0}. Проверьте «Настройки → Основные → Разрешённые IP / CIDR».", peer.GetString()!);
            }
            text = UiText.T(title) + ": " + details;
            EventReceived?.Invoke(name);
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        lines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + text.Replace("\r", "").Replace("\n", " "));
        while (lines.Count > 1500) { lines.TryDequeue(out _); }
    }
    private string? lastInventoryError;
    internal string[] Drain()
    {
        var result = new List<string>();
        while (lines.TryDequeue(out string? line)) { result.Add(line); }
        return result.ToArray();
    }
}
