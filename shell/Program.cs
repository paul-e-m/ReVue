using Microsoft.AspNetCore.Builder;
using ReVueVRO.Hosting;
using ReVueVRO.Shell;
using System.Windows.Forms;

namespace ReVueVRO;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.TryBecomePrimaryInstance())
        {
            singleInstance.RequestActivatePrimaryInstance();
            return;
        }

        singleInstance.StartActivationListener();

        WebApplication app;

        try
        {
            app = AppServer.Build(args);
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
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
        }
        finally
        {
            try { app.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }
}
