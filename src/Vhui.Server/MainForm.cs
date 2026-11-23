using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;
using static Vhui.Server.UiText;

namespace Vhui.Server;

internal sealed class MainForm : Form
{
    private readonly DesktopSettings settings;
    private readonly UiLogWriter log = new();
    private readonly TreeView devices = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = true, FullRowSelect = true, ItemHeight = 29, BorderStyle = BorderStyle.None };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ScrollBars = ScrollBars.Vertical };
    private readonly RichTextBox messages = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None, WordWrap = false, DetectUrls = false };
    private readonly ToolStripButton startButton = new("Запустить");
    private readonly ToolStripButton stopButton = new("Остановить");
    private readonly ToolStripButton shareButton = new("Предоставить доступ");
    private readonly ToolStripButton settingsButton = new("Настройки");
    private readonly ToolStripButton renameButton = new("Переименовать…");
    private readonly ToolStripMenuItem renameItem = new("Переименовать…");
    private readonly ToolStripMenuItem russian = new("Русский");
    private readonly ToolStripMenuItem english = new("English");
    private readonly ToolStripStatusLabel status = new("Сервер остановлен");
    private readonly ToolStripStatusLabel summary = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 15F, FontStyle.Regular), ForeColor = Color.FromArgb(33, 53, 75), Margin = new Padding(0, 0, 0, 6) };
    private readonly Label address = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly NotifyIcon tray = new() { Text = "Vhui USB Server", Icon = SystemIcons.Application, Visible = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 750 };
    private readonly SplitContainer content = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
    private readonly ToolStripMenuItem startItem = new("Запустить сервер");
    private readonly ToolStripMenuItem stopItem = new("Остановить сервер");
    private readonly ToolStripMenuItem configureItem = new("Настройки…");
    private readonly ToolStripMenuItem editConfigItem = new("Открыть файл настроек");
    private readonly ToolStripMenuItem shareItem = new("Предоставить доступ");
    private readonly ToolStripMenuItem trayStart = new("Запустить сервер");
    private readonly ToolStripMenuItem trayStop = new("Остановить сервер");
    private readonly ToolStripMenuItem journalItem = new("Журнал сообщений") { Checked = true, CheckOnClick = true };
    private UsbInventoryResult snapshot = new([], []);
    private readonly object inventorySync = new();
    private UsbServerHost? host;
    private CancellationTokenSource? serverStop;
    private Task? serverTask;
    private Task? serverObservation;
    private bool changing, refreshing, closing, allowClose;
    private bool listening;
    private readonly bool startOnOpen;
    private readonly string? smokeOutput;
    private DateTime nextRefresh;
    private string? inventoryError;

    internal MainForm(string configPath, bool startOnOpen, string? smokeOutput)
    {
        this.startOnOpen = startOnOpen; this.smokeOutput = smokeOutput;
        settings = new(configPath);
        UiText.Language = settings.Configuration.GetString("DesktopLanguage", "ru") == "en" ? "en" : "ru";
        Text = "Vhui USB Server " + Application.ProductVersion; Font = new Font("Segoe UI", 9F);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        tray.Icon = Icon;
        ClientSize = new Size(990, 670); MinimumSize = new Size(780, 530);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        UiText.Apply(this);
        russian.Checked = UiText.Language == "ru"; english.Checked = UiText.Language == "en";
        russian.Click += (_, _) => ChangeLanguage("ru"); english.Click += (_, _) => ChangeLanguage("en");
        renameButton.Click += (_, _) => RenameSelected(); renameItem.Click += (_, _) => RenameSelected();
        startButton.Click += async (_, _) => await StartServerAsync(); startItem.Click += async (_, _) => await StartServerAsync(); trayStart.Click += async (_, _) => await StartServerAsync();
        stopButton.Click += async (_, _) => await StopServerAsync(); stopItem.Click += async (_, _) => await StopServerAsync(); trayStop.Click += async (_, _) => await StopServerAsync();
        settingsButton.Click += async (_, _) => await ConfigureAsync(); configureItem.Click += async (_, _) => await ConfigureAsync();
        shareButton.Click += (_, _) => ToggleSharing(); shareItem.Click += (_, _) => ToggleSharing();
        editConfigItem.Click += (_, _) => Guard(() => Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { settings.Path } }));
        devices.AfterSelect += (_, _) => UpdateDetails();
        devices.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) { devices.SelectedNode = e.Node; } };
        log.EventReceived += name => { if (name == "Listening") { Post(() => { if (serverTask is { IsCompleted: false }) { listening = true; UpdateState(); } }); } };
        tray.DoubleClick += (_, _) => RestoreWindow();
        journalItem.CheckedChanged += (_, _) => content.Panel2Collapsed = !journalItem.Checked;
        timer.Tick += async (_, _) =>
        {
            FlushLog();
            if (serverTask is null && !refreshing && DateTime.UtcNow >= nextRefresh) { await RefreshDevicesAsync(); }
            else { UpdateTree(); }
        };
        Shown += async (_, _) =>
        {
            content.SplitterDistance = Math.Max(260, content.Height - 155);
            SmokePhase("Reading USB inventory");
            timer.Start(); await RefreshDevicesAsync();
            if (smokeOutput is not null) { await SmokeAsync(smokeOutput); }
            else if (startOnOpen) { await StartServerAsync(); }
        };
        FormClosing += OnClosing;
        UpdateState();
    }

    private void BuildLayout()
    {
        var menu = new MenuStrip();
        var serverMenu = new ToolStripMenuItem("Сервер");
        serverMenu.DropDownItems.AddRange([startItem, stopItem, new ToolStripSeparator(), configureItem, editConfigItem, new ToolStripSeparator(), new ToolStripMenuItem("Выход", null, (_, _) => Close())]);
        var deviceMenu = new ToolStripMenuItem("Устройства"); deviceMenu.DropDownItems.Add(shareItem);
        deviceMenu.DropDownItems.Add(renameItem);
        deviceMenu.DropDownItems.Add(new ToolStripMenuItem("Обновить список", null, async (_, _) => await RefreshDevicesAsync()));
        var view = new ToolStripMenuItem("Вид"); view.DropDownItems.Add(journalItem);
        view.DropDownItems.Add(new ToolStripMenuItem("Свернуть в трей", null, (_, _) => Hide()));
        var help = new ToolStripMenuItem("Справка");
        help.DropDownItems.Add(new ToolStripMenuItem("Руководство", null, (_, _) => Guard(() => { using var guide = new UserGuideForm(); guide.ShowDialog(this); })));
        help.DropDownItems.Add(new ToolStripMenuItem("О программе", null, (_, _) => MessageBox.Show(this, T("Vhui USB Server\nСервер USB по сети на C# / .NET\n\nСовместимость проверена с клиентом VirtualHere 6.0.3.\nEasyFind и лицензирование в эту версию не входят."), T("О программе"), MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.AddRange([serverMenu, deviceMenu, view, help]); MainMenuStrip = menu;
        var languageMenu = new ToolStripMenuItem("Language / Язык"); languageMenu.DropDownItems.AddRange([russian, english]); menu.Items.Add(languageMenu);
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 4, 8, 4) };
        startButton.Image = MakeIcon(Color.SeaGreen, "▶"); stopButton.Image = MakeIcon(Color.Firebrick, "■");
        shareButton.Image = MakeIcon(Color.SteelBlue, "+"); settingsButton.Image = MakeIcon(Color.DimGray, "•");
        var refresh = new ToolStripButton("Обновить") { Image = MakeIcon(Color.SteelBlue, "↻") };
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        tools.Items.AddRange([startButton, stopButton, new ToolStripSeparator(), shareButton, renameButton, refresh, new ToolStripSeparator(), settingsButton]);
        var heading = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, Height = 83, Padding = new Padding(18, 12, 10, 8), BackColor = Color.White, WrapContents = false };
        heading.Controls.Add(title); heading.Controls.Add(address);
        var images = new ImageList { ImageSize = new Size(20, 20), ColorDepth = ColorDepth.Depth32Bit };
        images.Images.Add("server", MakeIcon(Color.FromArgb(52, 99, 146), "S"));
        images.Images.Add("off", MakeIcon(Color.Gray, "U")); images.Images.Add("ready", MakeIcon(Color.SeaGreen, "U"));
        images.Images.Add("busy", MakeIcon(Color.DarkOrange, "U")); devices.ImageList = images;
        var context = new ContextMenuStrip();
        var shareContext = new ToolStripMenuItem("Предоставить доступ");
        shareContext.Click += (_, _) => ToggleSharing();
        context.Items.Add(shareContext);
        var renameContext = new ToolStripMenuItem("Переименовать…"); renameContext.Click += (_, _) => RenameSelected(); context.Items.Add(renameContext);
        context.Opening += (_, _) => { shareContext.Text = shareButton.Text; shareContext.Enabled = shareButton.Enabled; renameContext.Enabled = renameButton.Enabled; };
        devices.ContextMenuStrip = context;
        var columns = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 5 };
        columns.Panel1.Controls.Add(devices);
        var propertyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16) };
        var propertiesTitle = new Label { Text = "Сведения об устройстве", Dock = DockStyle.Top, Height = 34, Font = new Font(Font, FontStyle.Bold) };
        propertyPanel.Controls.Add(details); propertyPanel.Controls.Add(propertiesTitle); columns.Panel2.Controls.Add(propertyPanel);
        columns.SizeChanged += (_, _) => { if (columns.Width > 600) { columns.SplitterDistance = columns.Width * 58 / 100; } };
        content.Panel1.Controls.Add(columns);
        var logTitle = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = SystemColors.ControlLight };
        logTitle.Items.Add(new ToolStripLabel("Журнал сообщений"));
        var clear = new ToolStripButton("Очистить") { Alignment = ToolStripItemAlignment.Right }; clear.Click += (_, _) => messages.Clear();
        var save = new ToolStripButton("Сохранить…") { Alignment = ToolStripItemAlignment.Right };
        save.Click += (_, _) => Guard(() => { using var dialog = new SaveFileDialog { Filter = T("Текстовый журнал|*.log"), FileName = "vhui-server.log" }; if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllText(dialog.FileName, messages.Text, Encoding.UTF8); } });
        logTitle.Items.Add(clear); logTitle.Items.Add(save);
        content.Panel2.Controls.Add(messages); content.Panel2.Controls.Add(logTitle);
        var statusBar = new StatusStrip(); statusBar.Items.Add(status); statusBar.Items.Add(summary);
        Controls.Add(content); Controls.Add(heading); Controls.Add(tools); Controls.Add(menu); Controls.Add(statusBar);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(new ToolStripMenuItem("Открыть", null, (_, _) => RestoreWindow())); trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayStart); trayMenu.Items.Add(trayStop); trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Close())); tray.ContextMenuStrip = trayMenu;
    }

    private static Bitmap MakeIcon(Color color, string symbol)
    {
        var image = new Bitmap(20, 20);
        using Graphics graphics = Graphics.FromImage(image); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (symbol == "S")
        {
            using var pen = new Pen(color, 1.7F);
            graphics.DrawRectangle(pen, 2, 2, 16, 11); graphics.DrawLine(pen, 10, 13, 10, 17); graphics.DrawLine(pen, 5, 18, 15, 18);
            return image;
        }
        if (symbol == "U")
        {
            using var pen = new Pen(color, 2F);
            graphics.DrawLine(pen, 10, 17, 10, 3); graphics.DrawLine(pen, 6, 7, 10, 3); graphics.DrawLine(pen, 14, 7, 10, 3);
            graphics.DrawLine(pen, 10, 12, 5, 10); graphics.DrawLine(pen, 5, 10, 5, 7);
            graphics.DrawLine(pen, 10, 15, 16, 12); graphics.DrawLine(pen, 16, 12, 16, 8);
            using var usbBrush = new SolidBrush(color); graphics.FillEllipse(usbBrush, 7, 15, 6, 5); graphics.FillRectangle(usbBrush, 14, 6, 4, 4);
            return image;
        }
        using var brush = new SolidBrush(color); graphics.FillRoundedRectangle(brush, new Rectangle(1, 1, 18, 18), new Size(3, 3));
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        TextRenderer.DrawText(graphics, symbol, font, new Rectangle(0, 0, 20, 20), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return image;
    }

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) { return; }
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) { action(); } })); }
        catch (InvalidOperationException) { }
    }
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error)
    {
        log.WriteLine(error.Message); FlushLog();
        MessageBox.Show(this, error.Message, "Vhui USB Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ChangeLanguage(string language)
    {
        Guard(() =>
        {
            settings.Configuration.Set("DesktopLanguage", language); settings.Save(); UiText.Language = language;
            UiText.Apply(this); russian.Checked = language == "ru"; english.Checked = language == "en"; UpdateState();
        });
    }
    private void RenameSelected()
    {
        if (Selected is not { } device || changing || closing) { return; }
        using var dialog = new RenameDeviceForm(device.Product ?? $"USB {device.VendorId:X4}:{device.ProductId:X4}", settings.Names.AliasFor(device));
        if (dialog.ShowDialog(this) == DialogResult.OK) { Guard(() => SaveDeviceName(device, dialog.DeviceName)); }
    }
    private void SaveDeviceName(UsbPhysicalDevice device, string name)
    {
        settings.Names.Set(device, name); settings.Save(); host?.NotifyDeviceNameChanged(device); UpdateTree();
        log.WriteLine(T("Устройство переименовано: ") + settings.Names.DisplayName(device));
    }
    private UsbPhysicalDevice? Selected => devices.SelectedNode?.Tag as UsbPhysicalDevice;
    private UsbDeviceEntry? Entry(UsbPhysicalDevice device) => host?.Registry.Devices.FirstOrDefault(entry => UsbDeviceRegistry.Key(entry.Device).Equals(UsbDeviceRegistry.Key(device), StringComparison.OrdinalIgnoreCase));
    private string DeviceState(UsbPhysicalDevice device)
    {
        if (!settings.IsShared(device)) { return T("Доступ отключён"); }
        if (!settings.Options.Publishes(device)) { return T("Исключено настройками"); }
        UsbDeviceEntry? entry = Entry(device);
        if (entry?.Owner is { } owner) { return T("Используется: ") + owner.Address; }
        return T(listening && entry is not null ? "Доступно" : serverTask is not null ? "Подготовка…" : "Сервер остановлен");
    }
    private void UpdateTree()
    {
        string? selectedKey = Selected is { } selected ? UsbDeviceRegistry.Key(selected) : null;
        UsbPhysicalDevice[] observed; lock (inventorySync) { observed = snapshot.Devices.Where(device => !device.IsHub).ToArray(); }
        devices.BeginUpdate();
        try
        {
            TreeNode root;
            if (devices.Nodes.Count == 0) { root = devices.Nodes.Add("server", settings.Options.ServerName, "server", "server"); }
            else { root = devices.Nodes[0]; root.Text = settings.Options.ServerName; }
            var keys = observed.Select(UsbDeviceRegistry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (TreeNode old in root.Nodes.Cast<TreeNode>().ToArray()) { if (!keys.Contains(old.Name)) { old.Remove(); } }
            foreach (UsbPhysicalDevice device in observed.OrderBy(device => device.Product ?? "").ThenBy(device => device.Port))
            {
                string key = UsbDeviceRegistry.Key(device); TreeNode? node = root.Nodes[key];
                if (node is null) { node = new TreeNode { Name = key }; root.Nodes.Add(node); }
                node.Tag = device;
                node.Text = settings.Names.DisplayName(device) + " — " + DeviceState(device);
                string icon = Entry(device)?.Owner is not null ? "busy" : settings.IsShared(device) && listening && Entry(device) is not null ? "ready" : "off";
                node.ImageKey = icon; node.SelectedImageKey = icon;
                if (key == selectedKey) { devices.SelectedNode = node; }
            }
            root.Expand();
            devices.SelectedNode ??= root;
            summary.Text = UiText.Format("Устройств: {0}   ·   Доступ разрешён: {1}", observed.Length, observed.Count(settings.IsShared));
        }
        finally { devices.EndUpdate(); }
        UpdateDetails();
    }
    private void UpdateDetails()
    {
        UsbPhysicalDevice? selected = Selected;
        shareButton.Enabled = selected is not null && !changing && !closing;
        renameButton.Enabled = renameItem.Enabled = shareButton.Enabled;
        shareButton.Text = T(selected is not null && settings.IsShared(selected) ? "Отключить доступ" : "Предоставить доступ");
        shareItem.Enabled = shareButton.Enabled; shareItem.Text = shareButton.Text;
        if (selected is null)
        {
            details.Text = settings.Options.ServerName + "\r\n\r\n" + T(listening ? "Сервер запущен" : "Сервер остановлен") + "\r\n" + Endpoint() + "\r\n\r\n" + T("Выберите USB-устройство и нажмите\r\n«Предоставить доступ».") + "\r\n\r\n" + T("Подключение выполняется из штатного\r\nклиента VirtualHere.") + "\r\n\r\n" + T(Program.IsAdministrator() ? "Права администратора: есть" : "Для передачи USB потребуются\r\nправа администратора.");
            return;
        }
        UsbDeviceEntry? entry = Entry(selected);
        details.Text = $"{settings.Names.DisplayName(selected)}\r\n\r\n{DeviceState(selected)}\r\n\r\n{T("Исходное имя")}: {selected.Product ?? T("USB-устройство")}\r\n{T("Производитель")}: {selected.Manufacturer ?? "—"}\r\nVID:PID: {selected.VendorId:X4}:{selected.ProductId:X4}\r\n{T("Серийный номер")}: {selected.SerialNumber ?? "—"}\r\n{T("Порт USB")}: {selected.Port}\r\n{T("Адрес сервера")}: {entry?.Address.ToString() ?? "—"}\r\n{T("Клиент")}: {entry?.Owner?.Address.ToString() ?? "—"}\r\n\r\n{T(settings.IsShared(selected) ? "Доступ включён для этого USB-порта." : "Устройство доступно только локально.")}";
    }
    private string Endpoint()
    {
        bool tls = !string.IsNullOrEmpty(settings.Configuration.GetString("SSLCert"));
        string ip = settings.Options.BindAddress.ToString();
        return (tls ? "TLS" : "TCP") + " · " + (ip.Contains(':') ? "[" + ip + "]" : ip) + ":" + (tls ? settings.Options.Number("SSLPort", 7574, 1, 65535) : settings.Options.Port);
    }
    private void UpdateState()
    {
        bool running = serverTask is not null;
        startButton.Enabled = startItem.Enabled = trayStart.Enabled = !running && !changing && !closing;
        stopButton.Enabled = stopItem.Enabled = trayStop.Enabled = running && !changing && !closing;
        settingsButton.Enabled = configureItem.Enabled = !changing && !closing;
        editConfigItem.Enabled = !running && !changing && !closing;
        title.Text = settings.Options.ServerName;
        address.Text = Endpoint() + (settings.Options.BindAddress.Equals(System.Net.IPAddress.Loopback) ? T("  ·  Только этот компьютер") : "") + (Program.IsAdministrator() ? "" : T("  ·  Просмотр без администратора"));
        status.Text = T(closing || changing ? "Подождите…" : listening ? "Сервер запущен" : running ? "Запуск сервера…" : "Сервер остановлен");
        status.ForeColor = listening ? Color.DarkGreen : SystemColors.ControlText;
        tray.Text = "Vhui USB Server — " + T(listening ? "работает" : "остановлен");
        UpdateTree();
    }
    private UsbInventoryResult EnumerateDevices()
    {
        UsbInventoryResult result = ReadInventory();
        lock (inventorySync) { snapshot = result; }
        Post(UpdateTree);
        return result;
    }
    private UsbInventoryResult ReadInventory() => smokeOutput is null ? WindowsUsbInventory.Enumerate() : WindowsUsbInventory.Enumerate(0x145f, 0x02e7);
    private async Task RefreshDevicesAsync()
    {
        if (refreshing || closing) { return; }
        // The running host already scans. Do not race another enumeration against driver binding.
        if (serverTask is not null) { UpdateTree(); return; }
        refreshing = true;
        try
        {
            UsbInventoryResult result = await Task.Run(ReadInventory);
            lock (inventorySync) { snapshot = result; }
            string? error = result.Errors.Count == 0 ? null : UiText.Format("Не удалось опросить USB-порты: {0}. Остальные устройства показаны.", result.Errors.Count);
            if (error is not null && error != inventoryError) { log.WriteLine(error); }
            inventoryError = error;
            UpdateTree();
        }
        catch (Exception error) { log.WriteLine(T("Ошибка обновления устройств: ") + error.Message); }
        finally { refreshing = false; nextRefresh = DateTime.UtcNow.AddSeconds(3); }
    }
    private void ToggleSharing()
    {
        if (Selected is not { } device || changing || closing) { return; }
        bool shared = settings.IsShared(device);
        if (shared && Entry(device)?.Owner is not null && MessageBox.Show(this, T("Устройство сейчас используется клиентом. Отключить доступ и освободить устройство?"), T("Отключение устройства"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { return; }
        Guard(() => { settings.SetShared(device, !shared); log.WriteLine(T(shared ? "Доступ отключён: " : "Доступ включён: ") + settings.Names.DisplayName(device)); UpdateTree(); });
    }
    private async Task StartServerAsync()
    {
        if (serverTask is not null || changing || closing) { return; }
        if (smokeOutput is null && !Program.IsAdministrator())
        {
            Guard(() =>
            {
                settings.Save();
                var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                launch.ArgumentList.Add("--config"); launch.ArgumentList.Add(settings.Path); launch.ArgumentList.Add("--start");
                using Process? elevated = Process.Start(launch);
                if (elevated is not null) { allowClose = true; Close(); }
            });
            return;
        }
        try
        {
            settings.Save();
            serverStop = new CancellationTokenSource();
            host = new UsbServerHost(settings.Options, log, EnumerateDevices, shareUsb: smokeOutput is null, publish: settings.IsShared);
            host.Registry.Changed += _ => Post(UpdateTree);
            serverTask = Task.Run(() => host.RunAsync(serverStop.Token));
            UpdateState();
            serverObservation = ObserveServerAsync(serverTask);
        }
        catch (Exception error) { ShowError(error); }
        await Task.CompletedTask;
    }
    private async Task ObserveServerAsync(Task observed)
    {
        Exception? failure = null;
        try { await observed; }
        catch (OperationCanceledException) when (serverStop?.IsCancellationRequested == true) { }
        catch (Exception error) { failure = error; log.WriteLine(T("Сервер остановлен с ошибкой: ") + error.Message); }
        if (serverTask != observed) { return; }
        listening = false; serverTask = null; host = null;
        serverStop?.Dispose(); serverStop = null;
        if (!IsDisposed) { UpdateState(); FlushLog(); }
        if (failure is not null && !closing && !changing && smokeOutput is null) { ShowError(failure); }
    }
    private async Task StopServerAsync()
    {
        Task? stopping = serverObservation ?? serverTask;
        if (stopping is null) { return; }
        changing = true; UpdateState();
        try
        {
            if (serverStop is not null) { await serverStop.CancelAsync(); }
            await stopping;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.WriteLine(T("Ошибка остановки: ") + error.Message); }
        finally { changing = false; if (!IsDisposed) { UpdateState(); FlushLog(); } }
    }
    private async Task ConfigureAsync()
    {
        if (changing || closing) { return; }
        using var dialog = new SettingsForm(settings.Configuration);
        if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
        bool restart = serverTask is not null;
        if (restart) { await StopServerAsync(); }
        Guard(() => { foreach (var (key, value) in dialog.Changes) { settings.Configuration.Set(key, value); } settings.Save(); UpdateState(); });
        if (restart) { await StartServerAsync(); }
    }
    private void FlushLog()
    {
        string[] lines = log.Drain();
        if (lines.Length == 0) { return; }
        if (messages.TextLength > 180000) { messages.Select(0, messages.TextLength - 120000); messages.SelectedText = ""; }
        messages.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
        messages.SelectionStart = messages.TextLength; messages.ScrollToCaret();
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) { return; }
        e.Cancel = true;
        if (closing) { return; }
        closing = true; timer.Stop(); UpdateState();
        await StopServerAsync();
        allowClose = true; Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Visible = false; tray.Dispose(); devices.ImageList?.Dispose(); }
        base.Dispose(disposing);
    }
    private async Task SmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        bool started = false, probed = false, stopped = false, saved = false, shared = false, unpublished = false, republished = false, policySaved = false, renamed = false, languageSaved = false, guideLoaded = false;
        Exception? failure = null;
        try
        {
            SmokePhase("Selecting device");
            UsbPhysicalDevice? testDevice;
            lock (inventorySync) { testDevice = snapshot.Devices.FirstOrDefault(device => device.VendorId == 0x145f && device.ProductId == 0x02e7) ?? snapshot.Devices.FirstOrDefault(device => !device.IsHub); }
            if (testDevice is null) { throw new IOException("No USB device available for the read-only GUI test."); }
            devices.SelectedNode = devices.Nodes[0].Nodes[UsbDeviceRegistry.Key(testDevice)];
            string language = UiText.Language;
            ChangeLanguage(language == "en" ? "ru" : "en"); ChangeLanguage(language);
            languageSaved = new DesktopSettings(settings.Path).Configuration.GetString("DesktopLanguage") == language
                && startButton.Text == (language == "en" ? "Start" : "Запустить");
            using (var guide = new UserGuideForm())
            {
                guideLoaded = guide.GuideText.Length > 1000 && guide.GuideText.Contains("Vhui.Server.exe", StringComparison.Ordinal);
                guide.Shown += async (_, _) =>
                {
                    await Task.Delay(150);
                    using var image = new Bitmap(guide.Width, guide.Height);
                    guide.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "help.png")); guide.Close();
                };
                guide.ShowDialog(this);
            }
            const string initialAlias = "Studio Камера";
            using (var renameDialog = new RenameDeviceForm(testDevice.Product ?? "USB", null))
            {
                renameDialog.Shown += async (_, _) =>
                {
                    renameDialog.NameInput.Text = initialAlias; await Task.Delay(150);
                    using var image = new Bitmap(renameDialog.Width, renameDialog.Height);
                    renameDialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "rename.png"));
                    renameDialog.AcceptButton?.PerformClick();
                };
                if (renameDialog.ShowDialog(this) != DialogResult.OK) { throw new IOException("Rename dialog did not save."); }
                SaveDeviceName(testDevice, renameDialog.DeviceName);
            }
            if (!settings.IsShared(testDevice)) { ToggleSharing(); }
            saved = new DesktopSettings(settings.Path).IsShared(testDevice);
            settings.Configuration.Set("AllowedClients", "");
            SmokePhase("Starting host");
            await StartServerAsync();
            for (int index = 0; index < 80 && !listening && serverTask is not null; index++) { await Task.Delay(100); }
            started = listening;
            if (!started) { throw new IOException("GUI host did not listen."); }
            using var socket = new System.Net.Sockets.TcpClient(); await socket.ConnectAsync("127.0.0.1", settings.Options.Port);
            SmokePhase("Protocol handshake");
            await VirtualHereFrameCodec.WriteAsync(socket.GetStream(), VirtualHereMessage.Create(0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            VirtualHereMessage? response = await VirtualHereFrameCodec.ReadAsync(socket.GetStream(), timeout.Token);
            probed = response?.Type == 1;
            async Task<int> DeviceCount()
            {
                await VirtualHereFrameCodec.WriteAsync(socket.GetStream(), VirtualHereMessage.Create(9));
                await VirtualHereFrameCodec.WriteAsync(socket.GetStream(), VirtualHereMessage.Create(2));
                int count = 0;
                while (await VirtualHereFrameCodec.ReadAsync(socket.GetStream(), timeout.Token) is { } frame)
                {
                    if (frame.Type == 16) { count++; }
                    if (frame.Type == 17) { return count; }
                    if (frame.Type == 12) { await VirtualHereFrameCodec.WriteAsync(socket.GetStream(), VirtualHereMessage.Create(15)); }
                }
                throw new IOException("Unexpected end of GUI test connection.");
            }
            shared = await DeviceCount() == 1;
            const string updatedAlias = "Studio Камера 2";
            SaveDeviceName(testDevice, updatedAlias);
            while (await VirtualHereFrameCodec.ReadAsync(socket.GetStream(), timeout.Token) is { } update)
            {
                if (update.Type == 16 && Encoding.UTF8.GetString(update.Payload, 68, 64).TrimEnd('\0') == updatedAlias)
                { renamed = new DesktopSettings(settings.Path).Names.DisplayName(testDevice) == updatedAlias; break; }
            }
            await Task.Delay(500); UpdateTree(); FlushLog();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "main.png")); }
            using var settingsDialog = new SettingsForm(settings.Configuration);
            settingsDialog.Shown += async (_, _) =>
            {
                await Task.Delay(300);
                using var image = new Bitmap(settingsDialog.Width, settingsDialog.Height);
                settingsDialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(directory, "settings.png"));
                settingsDialog.AcceptButton?.PerformClick();
            };
            settingsDialog.ShowDialog(this);
            SmokePhase("Unsharing device");
            policySaved = settingsDialog.DialogResult == DialogResult.OK && settingsDialog.Changes.GetValueOrDefault("AllowedClients") == "*";
            devices.SelectedNode = devices.Nodes[0].Nodes[UsbDeviceRegistry.Key(testDevice)];
            ToggleSharing();
            for (int index = 0; index < 20 && !unpublished; index++) { unpublished = await DeviceCount() == 0; if (!unpublished) { await Task.Delay(150); } }
            saved = saved && !new DesktopSettings(settings.Path).IsShared(testDevice);
            ToggleSharing();
            for (int index = 0; index < 20 && !republished; index++) { republished = await DeviceCount() == 1; if (!republished) { await Task.Delay(150); } }
            SmokePhase("Stopping host");
            await StopServerAsync();
            for (int index = 0; index < 20 && serverTask is not null; index++) { await Task.Delay(50); }
            stopped = serverTask is null;
        }
        catch (Exception error) { failure = error; Environment.ExitCode = 1; }
        finally
        {
            bool passed = started && probed && stopped && saved && shared && unpublished && republished && policySaved && renamed && languageSaved && guideLoaded && failure is null;
            if (!passed) { Environment.ExitCode = 1; }
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { Passed = passed, Started = started, Probed = probed, Stopped = stopped, SettingsSaved = saved, BlankAllowlistSavedAsWildcard = policySaved, SharedDevicePublished = shared, UnsharedDeviceRemoved = unpublished, RepublishedDeviceRestored = republished, Language = UiText.Language, LanguageSaved = languageSaved, AliasSavedAndBroadcast = renamed, EmbeddedGuideLoaded = guideLoaded, Devices = devices.Nodes.Count == 0 ? 0 : devices.Nodes[0].Nodes.Count, Error = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
    private void SmokePhase(string phase)
    {
        if (smokeOutput is null) { return; }
        Directory.CreateDirectory(smokeOutput);
        File.WriteAllText(Path.Combine(smokeOutput, "phase.txt"), phase);
    }
}
