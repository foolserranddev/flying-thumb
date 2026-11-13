using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FlyingThumbManager;

static class RecoveryManagerCopy
{
    public const string FileName = "FlyingThumbManager.exe";

    static async Task<string?> MatchVolumeAsync(string mac)
    {
        var serial = mac.Replace(":", "").ToUpperInvariant();
        if (!Regex.IsMatch(serial, @"\A[0-9A-F]{12}\z")) throw new ArgumentException("Invalid drive identity.");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; Get-Disk | Where-Object { $_.BusType -eq 'USB' -and ([string]$_.SerialNumber).Trim() -eq '" + serial + "' } | Get-Partition | ForEach-Object { if ($_.DriveLetter) { [string]$_.DriveLetter } }" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not query Windows USB volumes.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); return null; }
        if (process.ExitCode != 0) throw new IOException("Could not identify the recovered USB volume: " + (await error).Trim());
        var letters = (await output).Split('\n').Select(line => line.Trim()).Where(line => Regex.IsMatch(line, @"\A[A-Za-z]\z")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (letters.Length > 1) throw new IOException("More than one volume matched the recovered drive. No USB files were changed.");
        return letters.Length == 1 ? letters[0].ToUpperInvariant() + @":\" : null;
    }

    public static async Task<string?> FindVolumeAsync(string mac, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var root = await MatchVolumeAsync(mac);
            if (root is not null && Directory.Exists(root)) return root;
            await Task.Delay(500);
        } while (timer.Elapsed < timeout);
        return null;
    }

    public static async Task CopyAsync(string source, string root, string mac, bool replace)
    {
        async Task VerifyTarget()
        {
            if (!string.Equals(root, await MatchVolumeAsync(mac), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recovered drive disconnected or changed drive letters. No replacement was made.");
        }
        await VerifyTarget();
        var destination = Path.Combine(root, FileName);
        var token = Guid.NewGuid().ToString("N");
        var staging = destination + "." + token + ".tmp";
        var backup = destination + "." + token + ".backup";
        bool staged = false;
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                staged = true;
                await input.CopyToAsync(output);
                await output.FlushAsync();
                await Task.Run(() => output.Flush(flushToDisk: true));
            }
            await VerifyTarget();
            if (replace) File.Move(destination, backup);
            try { File.Move(staging, destination); }
            catch
            {
                if (replace)
                {
                    try { await VerifyTarget(); File.Move(backup, destination); }
                    catch (Exception ex) { throw new IOException($"The replacement could not finish. The previous copy was saved as {Path.GetFileName(backup)} on the recovered drive.", ex); }
                }
                throw;
            }
            if (replace)
                try { await VerifyTarget(); File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            if (staged && File.Exists(staging))
                try { await VerifyTarget(); File.Delete(staging); } catch { }
        }
    }
}
