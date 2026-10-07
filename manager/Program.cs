namespace FlyingThumbManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (UpdateService.TryRunUpdateHelper(args)) return;
        UpdateService.CleanupPreviousExecutable();
        Application.Run(new MainForm());
    }
}
