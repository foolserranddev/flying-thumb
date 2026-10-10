namespace FlyingThumbManager;

public sealed record BrowserEntry(string Path, bool IsFolder, int Depth)
{
    public string Name => Path.Split('/')[^1];
}

public static class FileBrowserTree
{
    public static IReadOnlyList<BrowserEntry> Build(IEnumerable<string> files, IEnumerable<string> directories, ISet<string> expanded)
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, bool folder)
        {
            path = FlyingThumbClient.NormalizeRemotePath(path.TrimEnd('/'));
            entries[path] = folder;
            var slash = path.LastIndexOf('/');
            while (slash >= 0) { path = path[..slash]; entries[path] = true; slash = path.LastIndexOf('/'); }
        }
        foreach (var file in files) Add(file, false);
        foreach (var folder in directories) Add(folder, true);
        var result = new List<BrowserEntry>();
        void Visit(string parent, int depth)
        {
            foreach (var entry in entries.Where(e => string.Equals(e.Key.Contains('/') ? e.Key[..e.Key.LastIndexOf('/')] : "", parent, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new(entry.Key, entry.Value, depth));
                if (entry.Value && expanded.Contains(entry.Key)) Visit(entry.Key, depth + 1);
            }
        }
        Visit("", 0);
        return result;
    }
}
