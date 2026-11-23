using Vhui.Core.Configuration;

namespace Vhui.Server;

internal sealed class RenameDeviceForm : Form
{
    internal TextBox NameInput { get; } = new() { Dock = DockStyle.Top };
    internal string DeviceName => NameInput.Text.Trim();
    internal RenameDeviceForm(string original, string? alias)
    {
        Text = "Переименовать устройство"; Font = new Font("Segoe UI", 9F); ClientSize = new Size(475, 205);
        FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15) };
        var originalLabel = new Label { Dock = DockStyle.Top, Height = 30, Text = UiText.T("Исходное имя") + ": " + original, AutoEllipsis = true };
        var label = new Label { Dock = DockStyle.Top, Height = 24, Text = "Имя устройства" };
        NameInput.Text = alias ?? "";
        var hint = new Label { Dock = DockStyle.Top, Height = 50, Padding = new Padding(0, 9, 0, 0), ForeColor = SystemColors.GrayText, Text = "Пустое имя возвращает исходное название.\nИмя сохраняется и передаётся клиентам." };
        panel.Controls.Add(hint); panel.Controls.Add(NameInput); panel.Controls.Add(label); panel.Controls.Add(originalLabel);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 47, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var reset = new Button { Text = "Восстановить исходное", AutoSize = true };
        reset.Click += (_, _) => NameInput.Clear();
        save.Click += (_, _) =>
        {
            try { UsbDeviceNames.Validate(DeviceName); DialogResult = DialogResult.OK; }
            catch (ArgumentException) { MessageBox.Show(this, UiText.T("Имя не должно содержать управляющие символы и занимать больше 63 байт UTF-8."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        buttons.Controls.Add(cancel); buttons.Controls.Add(save); buttons.Controls.Add(reset);
        Controls.Add(panel); Controls.Add(buttons); AcceptButton = save; CancelButton = cancel;
        UiText.Apply(this); Shown += (_, _) => { NameInput.SelectAll(); NameInput.Focus(); };
    }
}
