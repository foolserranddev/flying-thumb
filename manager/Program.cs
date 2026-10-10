namespace FlyingThumbManager;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Length==2 && args[0]=="--transfer-preview") {
            using var preview=new MainForm(diagnosticWorker:true) { Opacity=0,ShowInTaskbar=false };
            preview.Shown+=async(_,_)=> { try { await preview.TestTransferQueue(Path.GetFullPath(args[1])); } catch(Exception ex) { File.WriteAllText(Path.GetFullPath(args[1])+".error",ex.ToString());Environment.ExitCode=1; } finally { preview.Close(); } };
            Application.Run(preview);return;
        }
        if(args.Length == 2 && args[0] == "--file-browser-preview") {
            using var preview = new MainForm(diagnosticWorker:true) { Size=new Size(1240,820),Opacity=0,ShowInTaskbar=false };
            preview.Shown += (_,_) => { preview.BeginInvoke(() => { try { preview.RenderBrowserPreview(Path.GetFullPath(args[1])); } catch(Exception ex) { File.WriteAllText(Path.GetFullPath(args[1])+".error",ex.ToString()); Environment.ExitCode=1; } finally { preview.Close(); } }); };
            Application.Run(preview); return;
        }
        if (UpdateService.TryRunUpdateHelper(args)) return;
        UpdateService.CleanupPreviousExecutable();
        if (args.Length == 4 && args[0] == "--usb-diagnostic-worker")
        {
            using var worker = new MainForm(diagnosticWorker: true) { Opacity = 0, ShowInTaskbar = false };
            worker.Shown += async (_, _) =>
            {
                try { await worker.RunUsbDiagnosticWorker(args[1], Path.GetFullPath(args[2]), args[3]); Environment.ExitCode = worker.AutomatedDiagnosticSucceeded ? 0 : 1; }
                catch (Exception ex) { File.WriteAllText(Path.GetFullPath(args[2]), ex.ToString()); Environment.ExitCode = 1; }
                finally { worker.Close(); }
            };
            Application.Run(worker);
            return;
        }
        Application.Run(new MainForm());
    }
}
