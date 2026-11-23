using System.Reflection;
using System.Text.RegularExpressions;

namespace Vhui.Server;

internal sealed class UserGuideForm : Form
{
    internal string GuideText { get; }
    internal UserGuideForm()
    {
        Text = UiText.T("Руководство"); Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(760, 640); MinimumSize = new Size(520, 380); StartPosition = FormStartPosition.CenterParent;
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Vhui.Help." + UiText.Language + ".md")
            ?? throw new InvalidDataException("Embedded user guide was not found.");
        using var reader = new StreamReader(stream);
        string markdown = reader.ReadToEnd();
        GuideText = Regex.Replace(markdown, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("**", "").Replace("`", "");
        var text = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, Font = Font };
        using var heading = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        foreach (string line in GuideText.Split('\n'))
        {
            bool isHeading = line.StartsWith('#');
            text.SelectionFont = isHeading ? heading : Font;
            text.AppendText((isHeading ? line.TrimStart('#', ' ') : line).TrimEnd('\r') + Environment.NewLine);
        }
        text.SelectionStart = 0; text.ScrollToCaret();
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18), BackColor = Color.White };
        panel.Controls.Add(text); Controls.Add(panel);
    }
}
