using System.Windows.Forms;
using ReVue.Diagnostics;
using System.Reflection;

namespace ReVueJudge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var runtimeLog = RuntimeLog.Start("ReVue-Judge", typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RuntimeLog.Write("Critical", "Application", "Unhandled exception; process terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
            runtimeLog?.Dispose();
        };
        try
        {
            Application.Run(new ReVueJudgeMainForm());
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("Error", "Application", "Judge application could not start.", ex);
            MessageBox.Show(
                "ReVue Judge could not start.\r\n\r\n" + ex.Message,
                "ReVue Judge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        RuntimeLog.Write("Information", "Application", "Stopped.");
    }
}
