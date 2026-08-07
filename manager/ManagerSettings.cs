using System.Security.Cryptography;
using System.Text;

namespace FlyingThumbManager;

static class ManagerSettings
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FlyingThumbManager.ShopKey.v1");
    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyingThumbManager");
    static string KeyFile => Path.Combine(Folder, "shop-key.dat");
    static string WindowFile => Path.Combine(Folder, "window-size.txt");

    public static string LoadKey()
    {
        try
        {
            if (!File.Exists(KeyFile)) return "";
            var protectedBytes = Convert.FromBase64String(File.ReadAllText(KeyFile));
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Folder);
        if (string.IsNullOrEmpty(key))
        {
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
            return;
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllText(KeyFile, Convert.ToBase64String(protectedBytes));
    }

    public static (Size Size, bool Maximized)? LoadWindowSize()
    {
        try
        {
            if (!File.Exists(WindowFile)) return null;
            var parts = File.ReadAllText(WindowFile).Split(',');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height) || !bool.TryParse(parts[2], out var maximized)) return null;
            return (new Size(width, height), maximized);
        }
        catch { return null; }
    }

    public static void SaveWindowSize(Size size, bool maximized)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(WindowFile, $"{size.Width},{size.Height},{maximized}");
        }
        catch { }
    }}