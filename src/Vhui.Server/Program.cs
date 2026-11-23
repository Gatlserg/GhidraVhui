using System.Security.Principal;

namespace Vhui.Server;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vhui", "Server", "server.ini");
        bool start = false;
        string? smokeOutput = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--config" && index + 1 < args.Length) { config = Path.GetFullPath(args[++index]); }
            else if (args[index] == "--start") { start = true; }
            else if (args[index] == "--smoke-output" && index + 1 < args.Length) { smokeOutput = Path.GetFullPath(args[++index]); }
            else { MessageBox.Show(UiText.T("Неизвестный параметр: ") + args[index], "Vhui USB Server", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        }
        try { Application.Run(new MainForm(config, start, smokeOutput)); }
        catch (Exception error) { MessageBox.Show(error.Message, "Vhui USB Server", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
    }

    internal static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
