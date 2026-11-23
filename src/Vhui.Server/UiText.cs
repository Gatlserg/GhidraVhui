using System.Globalization;

namespace Vhui.Server;

internal static class UiText
{
    internal static string Language { get; set; } = "ru";
    private static readonly Dictionary<string, string> English = new()
    {
        ["Запустить"] = "Start", ["Остановить"] = "Stop", ["Предоставить доступ"] = "Share device", ["Отключить доступ"] = "Unshare device",
        ["Настройки"] = "Settings", ["Сервер"] = "Server", ["Устройства"] = "Devices", ["Вид"] = "View", ["Справка"] = "Help",
        ["Запустить сервер"] = "Start server", ["Остановить сервер"] = "Stop server", ["Настройки…"] = "Settings…",
        ["Открыть файл настроек"] = "Open configuration file", ["Выход"] = "Exit", ["Обновить список"] = "Refresh devices",
        ["Журнал сообщений"] = "System messages", ["Свернуть в трей"] = "Minimize to tray", ["Руководство"] = "User guide",
        ["О программе"] = "About", ["Обновить"] = "Refresh", ["Сведения об устройстве"] = "Device details", ["Очистить"] = "Clear",
        ["Сохранить…"] = "Save…", ["Открыть"] = "Open", ["Сервер остановлен"] = "Server stopped", ["Сервер запущен"] = "Server running",
        ["Запуск сервера…"] = "Starting server…", ["Подождите…"] = "Please wait…", ["работает"] = "running", ["остановлен"] = "stopped",
        ["Доступ отключён"] = "Not shared", ["Исключено настройками"] = "Blocked by configuration", ["Используется: "] = "In use by: ",
        ["Доступно"] = "Available", ["Подготовка…"] = "Preparing…", ["Устройств: {0}   ·   Доступ разрешён: {1}"] = "Devices: {0}   ·   Shared: {1}",
        ["USB-устройство"] = "USB device", ["Производитель"] = "Manufacturer", ["Серийный номер"] = "Serial number", ["Порт USB"] = "USB port",
        ["Адрес сервера"] = "Server address", ["Клиент"] = "Client", ["Исходное имя"] = "Original name",
        ["Доступ включён для этого USB-порта."] = "Sharing is enabled for this USB port.", ["Устройство доступно только локально."] = "The device is only available locally.",
        ["Выберите USB-устройство и нажмите\r\n«Предоставить доступ»."] = "Select a USB device and click\r\nShare device.",
        ["Подключение выполняется из штатного\r\nклиента VirtualHere."] = "Connect using the original\r\nVirtualHere client.",
        ["Права администратора: есть"] = "Administrator privileges: enabled", ["Для передачи USB потребуются\r\nправа администратора."] = "USB sharing requires\r\nadministrator privileges.",
        ["  ·  Только этот компьютер"] = "  ·  This computer only", ["  ·  Просмотр без администратора"] = "  ·  Non-administrator preview",
        ["Не удалось опросить USB-порты: {0}. Остальные устройства показаны."] = "Could not query {0} USB ports. Other devices are shown.",
        ["Ошибка обновления устройств: "] = "Device refresh failed: ", ["Доступ отключён: "] = "Device unshared: ", ["Доступ включён: "] = "Device shared: ",
        ["Устройство сейчас используется клиентом. Отключить доступ и освободить устройство?"] = "A client is using this device. Unshare and release it?",
        ["Отключение устройства"] = "Unshare device", ["Сервер остановлен с ошибкой: "] = "Server stopped with an error: ", ["Ошибка остановки: "] = "Failed to stop: ",
        ["Текстовый журнал|*.log"] = "Text log|*.log", ["Настройки сервера"] = "Server settings", ["Основные"] = "General",
        ["Имя сервера"] = "Server name", ["Адрес прослушивания"] = "Listen address", ["TCP-порт"] = "TCP port",
        ["Обнаружение в сети"] = "Network discovery", ["Разрешённые IP / CIDR"] = "Allowed IPs / CIDR",
        ["127.0.0.1 — только этот компьютер.\n0.0.0.0 — все сетевые интерфейсы.\nПустой список IP разрешает любые адреса."] = "127.0.0.1 — this computer only.\n0.0.0.0 — all network interfaces.\nA blank IP list allows all addresses.",
        ["Сертификат сервера"] = "Server certificate", ["Отдельный ключ"] = "Separate private key", ["TLS-порт"] = "TLS port",
        ["Сертификаты клиентов"] = "Require client certificates", ["Центр сертификации"] = "Certificate authority",
        ["Сертификаты|*.pem;*.crt;*.cer;*.pfx;*.p12;*.key|Все файлы|*.*"] = "Certificates|*.pem;*.crt;*.cer;*.pfx;*.p12;*.key|All files|*.*",
        ["Пустой сертификат отключает TLS. С сертификатом\nсервер принимает только защищённые подключения.\nПароль ключа можно задать в INI: SSLCertPassword."] = "Leave the certificate blank to disable TLS. A configured\ncertificate enables secure connections only.\nSet SSLCertPassword in the INI for encrypted keys.",
        ["Обратное подключение"] = "Reverse connections", ["TCP-клиенты"] = "TCP clients", ["TLS-клиенты"] = "TLS clients",
        ["Адреса через запятую: host:port.\nTCP использует порт 7573, TLS — 7572, если порт опущен.\nTLS-клиенты требуют сертификат сервера.\n\nИзменения применяются с перезапуском сервера."] = "Comma-separated addresses: host:port.\nDefault ports: TCP 7573, TLS 7572.\nTLS clients require a server certificate.\n\nSaving settings restarts the server.",
        ["Отмена"] = "Cancel", ["Сохранить"] = "Save", ["Укажите имя, корректный IP-адрес и порты от 1 до 65535."] = "Enter a name, a valid IP address and ports from 1 to 65535.",
        ["Для этого режима нужен сертификат сервера."] = "This mode requires a server certificate.", ["Выберите центр сертификации клиентов."] = "Select a client certificate authority.",
        ["Vhui USB Server\nСервер USB по сети на C# / .NET\n\nСовместимость проверена с клиентом VirtualHere 6.0.3.\nEasyFind и лицензирование в эту версию не входят."] = "Vhui USB Server\nUSB over network server for Windows / .NET\n\nTested with the VirtualHere 6.0.3 client.\nEasyFind and licensing are not included.",
        ["Переименовать…"] = "Rename…", ["Переименовать устройство"] = "Rename device", ["Имя устройства"] = "Device name",
        ["Восстановить исходное"] = "Reset to original", ["Пустое имя возвращает исходное название.\nИмя сохраняется и передаётся клиентам."] = "Leave blank to restore the original name.\nThe name is saved and sent to clients.",
        ["Имя не должно содержать управляющие символы и занимать больше 63 байт UTF-8."] = "Use no control characters and at most 63 UTF-8 bytes.",
        ["Устройство переименовано: "] = "Device renamed: ", ["Сервер принимает подключения"] = "Server is listening", ["Клиент отключён"] = "Client disconnected",
        ["Ошибка подключения"] = "Connection error", ["Клиенту отказано в доступе"] = "Client access denied", ["Не удалось опросить некоторые USB-порты"] = "Could not query some USB ports",
        ["Не удалось восстановить драйвер"] = "Driver restoration failed", ["Повтор обратного подключения"] = "Retrying reverse connection",
        ["порт {0}, код {1}: {2}"] = "port {0}, code {1}: {2}", ["{0}. Проверьте «Настройки → Основные → Разрешённые IP / CIDR»."] = "{0}. Check Settings → General → Allowed IPs / CIDR.",
        ["{0} {1}:{2}, устройств: {3}"] = "{0} {1}:{2}, devices: {3}", ["Неизвестный параметр: "] = "Unknown argument: "
    };
    internal static string T(string text) => Language == "en" ? English.GetValueOrDefault(text, text) : text;
    internal static string Format(string text, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(text), args);
    private static string StaticText(string text)
    {
        string russian = English.ContainsKey(text) ? text : English.FirstOrDefault(pair => pair.Value == text).Key ?? text;
        return T(russian);
    }
    internal static void Apply(Control root)
    {
        if (root is not TextBoxBase && root is not TreeView) { root.Text = StaticText(root.Text); }
        if (root is ToolStrip strip) { foreach (ToolStripItem item in strip.Items) { ApplyItem(item); } }
        if (root.ContextMenuStrip is { } context) { foreach (ToolStripItem item in context.Items) { ApplyItem(item); } }
        foreach (Control child in root.Controls) { Apply(child); }
    }
    private static void ApplyItem(ToolStripItem item)
    {
        item.Text = StaticText(item.Text ?? "");
        if (item is ToolStripDropDownItem menu) { foreach (ToolStripItem child in menu.DropDownItems) { ApplyItem(child); } }
    }
}
