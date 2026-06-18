using System.Windows.Forms;

namespace ReVueJudge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new ReVueJudgeMainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "ReVue Judge could not start.\r\n\r\n" + ex.Message,
                "ReVue Judge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
