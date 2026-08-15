using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FlyingThumbManager;

public sealed class UpdateManifest
{
    public int Schema { get; set; }
    public UpdateAsset Manager { get; set; } = new();
    public UpdateAsset Firmware { get; set; } = new();
    public UpdateAsset Recovery { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class UpdateAsset
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public static class UpdateService
{
    public const string ManifestUrl = "https://github.com/foolserranddev/flying-thumb/releases/latest/download/latest.json";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string CurrentManagerVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateManifest> GetLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl + "?check=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.UserAgent.ParseAdd("FlyingThumbManager/" + CurrentManagerVersion);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(input, JsonOptions) ?? throw new InvalidOperationException("The update information was empty.");
        if (manifest.Schema != 1 || string.IsNullOrWhiteSpace(manifest.Firmware.Version)) throw new InvalidOperationException("The update information was not recognized.");
        return manifest;
    }

    public static bool IsNewer(string available, string installed)
    {
        static Version Parse(string value)
        {
            var clean = (value ?? "").Split('-', 2)[0].Trim();
            return Version.TryParse(clean, out var version) ? version : new Version(0, 0, 0);
        }
        return Parse(available) > Parse(installed);
    }

    public static async Task<string> DownloadVerifiedAsync(UpdateAsset asset, string fileName)
    {
        if (string.IsNullOrWhiteSpace(asset.Url) || string.IsNullOrWhiteSpace(asset.Sha256)) throw new InvalidOperationException("The update download information is incomplete.");
        var folder = Path.Combine(Path.GetTempPath(), "FlyingThumb", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, fileName);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd("FlyingThumbManager/" + CurrentManagerVersion);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync())
        await using (var output = File.Create(destination)) await input.CopyToAsync(output);
        await using var verify = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify));
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The downloaded update did not pass its safety check.");
        return destination;
    }

    public static void LaunchSelfUpdate(string downloadedExe)
    {
        var currentExe = Application.ExecutablePath;
        var start = new ProcessStartInfo(downloadedExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--apply-manager-update", "--wait-pid", Environment.ProcessId.ToString(), "--destination", currentExe }) start.ArgumentList.Add(argument);
        Process.Start(start);
    }

    public static bool TryRunUpdateHelper(string[] args)
    {
        if (!args.Contains("--apply-manager-update", StringComparer.OrdinalIgnoreCase)) return false;

        try
        {
            var processId = ArgumentValue(args, "--wait-pid");
            var destination = ArgumentValue(args, "--destination");
            if (!int.TryParse(processId, out var oldProcessId) || string.IsNullOrWhiteSpace(destination))
                throw new InvalidOperationException("The update handoff information was incomplete.");

            try
            {
                using var oldProcess = Process.GetProcessById(oldProcessId);
                if (!oldProcess.WaitForExit(60_000))
                    throw new TimeoutException("The previous Manager did not close within one minute.");
            }
            catch (ArgumentException)
            {
                // The old Manager already exited before the updater started.
            }

            var source = Environment.ProcessPath ?? Application.ExecutablePath;
            Exception? lastCopyError = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Copy(source, destination, true);
                    lastCopyError = null;
                    break;
                }
                catch (IOException ex) { lastCopyError = ex; Thread.Sleep(250); }
                catch (UnauthorizedAccessException ex) { lastCopyError = ex; Thread.Sleep(250); }
            }
            if (lastCopyError is not null) throw new IOException("Windows would not replace the old Manager executable.", lastCopyError);

            Process.Start(new ProcessStartInfo(destination)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(destination) ?? Environment.CurrentDirectory
            });
        }
        catch (Exception ex)
        {
            var logFolder = Path.Combine(Path.GetTempPath(), "FlyingThumb");
            Directory.CreateDirectory(logFolder);
            File.WriteAllText(Path.Combine(logFolder, "manager-update-error.txt"), ex.ToString());
            MessageBox.Show("Flying Thumb Manager could not finish installing its update.\n\n" + ex.Message + "\n\nThe downloaded Manager can still be installed manually.", "Flying Thumb Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return true;
    }

    static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
