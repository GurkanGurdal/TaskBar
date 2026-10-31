using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace GrupBar;

/// <summary>
/// Sabitlenen ya da gruba eklenen kısayolun kopyası uygulamanın klasöründe tutulur:
/// kullanıcı masaüstündeki kısayolu silince öğe bozulmasın.
/// </summary>
internal static class ShortcutStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrupBar", "Shortcuts");

    public static bool IsKept(string path)
    {
        return string.Equals(Path.GetDirectoryName(path), Folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Kısayolsa kopyasının yolunu döner, değilse yolun kendisini. Aynı kısayol hep aynı kopyaya gider.</summary>
    public static string Keep(string path)
    {
        if (!IsShortcut(path) || IsKept(path) || !File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            string source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..8];
            string copy = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(path)}-{source}{Path.GetExtension(path)}");
            File.Copy(path, copy, true);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// Dosyası kaybolmuş uygulama öğelerini yüklü uygulamalar arasında adıyla arar.
    /// Belge gibi öğelere dokunmaz: aynı adlı bir uygulamayla karışabilir.
    /// </summary>
    /// <returns>Bir öğe değiştiyse true.</returns>
    public static bool Repair(IEnumerable<GroupItem> items)
    {
        bool changed = false;
        Dictionary<string, string>? apps = null;

        foreach (GroupItem item in items)
        {
            if (!IsLostApp(item))
            {
                continue;
            }

            apps ??= InstalledApps();
            if (apps.TryGetValue(item.Name, out string? id))
            {
                item.Path = GroupItem.StoreAppPrefix + id;
                changed = true;
            }
        }
        return changed;
    }

    private static bool IsLostApp(GroupItem item)
    {
        return item.HasFileLocation
            && (IsShortcut(item.Path) || Path.GetExtension(item.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            && !File.Exists(item.Path);
    }

    private static bool IsShortcut(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Başlat menüsündeki tüm uygulamalar: ad ve kimlik. Store dışındakiler de burada.</summary>
    private static Dictionary<string, string> InstalledApps()
    {
        var apps = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic list = shell.NameSpace("shell:AppsFolder").Items();
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic app = list.Item(i);
                apps.TryAdd((string)app.Name, (string)app.Path);
            }
        }
        catch (Exception e) when (e is COMException or RuntimeBinderException or InvalidCastException)
        {
            // Liste alınamazsa öğe olduğu gibi kalır.
        }
        return apps;
    }
}
