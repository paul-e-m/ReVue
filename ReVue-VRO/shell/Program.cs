using Microsoft.AspNetCore.Builder;
using ReVueVRO.Hosting;
using ReVueVRO.Shell;
using System.Diagnostics;
using System.Windows.Forms;
using ReVue.Diagnostics;
using System.Reflection;

namespace ReVueVRO;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var singleInstance = new SingleInstanceCoordinator();
        var restartPending = false;

        try
        {
            if (!singleInstance.TryBecomePrimaryInstance())
            {
                singleInstance.RequestActivatePrimaryInstance();
                return;
            }

            singleInstance.StartActivationListener();
            using var runtimeLog = RuntimeLog.Start("ReVue-VRO", typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                RuntimeLog.Write("Critical", "Application", "Unhandled exception; process terminating=" + e.IsTerminating, e.ExceptionObject as Exception);
                runtimeLog?.Dispose();
            };

            WebApplication app;

            try
            {
                app = AppServer.Build(args);
                app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                RuntimeLog.Write("Error", "Application", "Local web server could not start.", ex);
                MessageBox.Show(
                    "ReVue VRO could not start the local web server.\r\n\r\n" + ex.Message,
                    "ReVue VRO",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            using var mainForm = new MainForm(app);
            singleInstance.SetActivationAction(mainForm.ActivateFromAnotherInstance);

            try
            {
                Application.Run(mainForm);
                restartPending = ShellCommands.RestartPending;
            }
            finally
            {
                try { app.StopAsync().GetAwaiter().GetResult(); } catch { }
                try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                RuntimeLog.Write("Information", "Application", "Stopped; restart requested=" + restartPending);
            }
        }
        finally
        {
            singleInstance.Dispose();
        }

        if (restartPending)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "ReVue VRO could not restart itself.\r\n\r\n" + ex.Message,
                    "ReVue VRO",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
