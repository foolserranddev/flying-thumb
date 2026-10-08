using System.Security.Cryptography;
using System.Text.Json;

namespace FlyingThumbManager;

public sealed class DiagnosticSession
{
    public string Mac { get; set; } = "";
    public string Phase { get; set; } = "Backed up";
    public Dictionary<string, string> Hashes { get; set; } = new();
    public static readonly string[] BackupNames = ["installed-app0.bin", "boot-selection.bin", "original-coredump.bin"];
    public static DiagnosticSession Create(string folder, string mac)
    {
        var session = new DiagnosticSession { Mac = mac };
        foreach (var name in BackupNames) session.Hashes[name] = Hash(Path.Combine(folder, name));
        session.Save(folder); return session;
    }
    public void Save(string folder)
    {
        var pending = Path.Combine(folder, "session.json.pending");
        File.WriteAllText(pending, JsonSerializer.Serialize(this));
        File.Move(pending, Path.Combine(folder, "session.json"), true);
    }
    public static DiagnosticSession Load(string file)
    {
        var session = JsonSerializer.Deserialize<DiagnosticSession>(File.ReadAllText(file)) ?? throw new InvalidOperationException("Empty diagnostic session.");
        var folder = Path.GetDirectoryName(file)!;
        foreach (var name in BackupNames)
            if (!session.Hashes.TryGetValue(name, out var expected) || Hash(Path.Combine(folder, name)) != expected)
                throw new InvalidOperationException("Diagnostic backup is missing or changed: " + name);
        return session;
    }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
