namespace FlyingThumbManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (UpdateService.TryRunUpdateHelper(args)) return;
        Application.Run(new MainForm());
    }
}
