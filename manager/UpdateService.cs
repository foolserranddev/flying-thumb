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
        var script = Path.Combine(Path.GetDirectoryName(downloadedExe)!, "install-manager-update.ps1");
        File.WriteAllText(script, "param([int]$ProcessId,[string]$Source,[string]$Destination)\n$ErrorActionPreference='Stop'\n$process=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue\nif($process){$process.WaitForExit()}\nCopy-Item -LiteralPath $Source -Destination $Destination -Force\nStart-Process -FilePath $Destination\nRemove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue\nRemove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\n");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-ProcessId", Environment.ProcessId.ToString(), "-Source", downloadedExe, "-Destination", currentExe }) start.ArgumentList.Add(argument);
        Process.Start(start);
    }
}