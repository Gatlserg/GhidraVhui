using System.Net;
using Vhui.Core.Configuration;

namespace Vhui.Server;

internal sealed class SettingsForm : Form
{
    private readonly Dictionary<string, Control> fields = [];
    internal Dictionary<string, string> Changes { get; } = [];

    internal SettingsForm(ServerConfiguration configuration)
    {
        Text = "Настройки сервера"; Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(590, 465); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 7) };
        TableLayoutPanel Page(string name)
        {
            var page = new TabPage(name) { Padding = new Padding(15), UseVisualStyleBackColor = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(layout); tabs.TabPages.Add(page); return layout;
        }
        void Field(TableLayoutPanel page, string key, string label, string fallback = "", bool check = false, bool browse = false)
        {
            int row = page.RowCount++; page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 8, 9) }, 0, row);
            Control input = check ? new CheckBox { Checked = configuration.GetString(key, fallback) == "1", AutoSize = true, Margin = new Padding(0, 9, 0, 9) }
                : new TextBox { Text = configuration.GetString(key, fallback, expandEnvironment: false), Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
            fields.Add(key, input);
            if (browse)
            {
                var container = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
                container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                var button = new Button { Text = "…", Dock = DockStyle.Fill, Margin = new Padding(4, 4, 0, 4) };
                button.Click += (_, _) => { using var dialog = new OpenFileDialog { Filter = UiText.T("Сертификаты|*.pem;*.crt;*.cer;*.pfx;*.p12;*.key|Все файлы|*.*") }; if (dialog.ShowDialog(this) == DialogResult.OK) { input.Text = dialog.FileName; } };
                container.Controls.Add(input, 0, 0); container.Controls.Add(button, 1, 0); page.Controls.Add(container, 1, row);
            }
            else { page.Controls.Add(input, 1, row); }
        }
        var network = Page("Основные");
        Field(network, "ServerName", "Имя сервера", "Vhui USB Server");
        Field(network, "NetworkInterface", "Адрес прослушивания", "127.0.0.1");
        Field(network, "TCPPort", "TCP-порт", "7575");
        Field(network, "UseAVAHI", "Обнаружение в сети", "1", check: true);
        Field(network, "AllowedClients", "Разрешённые IP / CIDR");
        var hint = new Label { Text = "127.0.0.1 — только этот компьютер.\n0.0.0.0 — все сетевые интерфейсы.\nПустой список IP разрешает любые адреса.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 12, 0, 0) };
        network.Controls.Add(hint, 0, network.RowCount++); network.SetColumnSpan(hint, 2);
        var tls = Page("TLS");
        Field(tls, "SSLCert", "Сертификат сервера", browse: true);
        Field(tls, "SSLKeyFile", "Отдельный ключ", browse: true);
        Field(tls, "SSLPort", "TLS-порт", "7574");
        Field(tls, "SSLUseClientCerts", "Сертификаты клиентов", "0", check: true);
        Field(tls, "SSLCAFile", "Центр сертификации", browse: true);
        var tlsHint = new Label { Text = "Пустой сертификат отключает TLS. С сертификатом\nсервер принимает только защищённые подключения.\nПароль ключа можно задать в INI: SSLCertPassword.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 12, 0, 0) };
        tls.Controls.Add(tlsHint, 0, tls.RowCount++); tls.SetColumnSpan(tlsHint, 2);
        var reverse = Page("Обратное подключение");
        Field(reverse, "ReverseClients", "TCP-клиенты"); Field(reverse, "SSLReverseClients", "TLS-клиенты");
        var reverseHint = new Label { Text = "Адреса через запятую: host:port.\nTCP использует порт 7573, TLS — 7572, если порт опущен.\nTLS-клиенты требуют сертификат сервера.\n\nИзменения применяются с перезапуском сервера.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 12, 0, 0) };
        reverse.Controls.Add(reverseHint, 0, reverse.RowCount++); reverse.SetColumnSpan(reverseHint, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = SystemColors.Control };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 95 };
        var save = new Button { Text = "Сохранить", Width = 110 };
        save.Click += (_, _) =>
        {
            foreach (var (key, field) in fields) { Changes[key] = field is CheckBox box ? (box.Checked ? "1" : "0") : field.Text.Trim(); }
            if (Changes["AllowedClients"].Length == 0) { Changes["AllowedClients"] = "*"; }
            if (!IPAddress.TryParse(Changes["NetworkInterface"], out _) || !Port("TCPPort") || !Port("SSLPort") || string.IsNullOrWhiteSpace(Changes["ServerName"]))
            { MessageBox.Show(this, UiText.T("Укажите имя, корректный IP-адрес и порты от 1 до 65535."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if ((Changes["SSLUseClientCerts"] == "1" || Changes["SSLReverseClients"].Length > 0) && Changes["SSLCert"].Length == 0)
            { MessageBox.Show(this, UiText.T("Для этого режима нужен сертификат сервера."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (Changes["SSLUseClientCerts"] == "1" && Changes["SSLCAFile"].Length == 0)
            { MessageBox.Show(this, UiText.T("Выберите центр сертификации клиентов."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            DialogResult = DialogResult.OK;
        };
        bool Port(string key) => int.TryParse(Changes[key], out int value) && value is > 0 and <= 65535;
        buttons.Controls.Add(cancel); buttons.Controls.Add(save);
        Controls.Add(tabs); Controls.Add(buttons); AcceptButton = save; CancelButton = cancel;
        UiText.Apply(this);
    }
}
