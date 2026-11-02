using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using ManagedShell.WindowsTasks;

namespace IslandBar;

/// <summary>
/// Pencerenin gösterdiği dosya ya da klasör: çalışma düzeni uygulamayı değil onu açsın diye (yoksa Fotoğraflar boş
/// açılıp son görüntüyü gösteriyordu). Windows'ta bunun genel bir yolu yok. Gezgin klasörünü kendisi söylüyor; dosyada
/// başlıktaki ad aranır: önce sürecin komut satırında (Fotoğraflar yazmıyor, Notepad'in pencere süreci kodlu yazıyor),
/// sonra Son Kullanılanlar'da (Gezgin'den açılanlar), en son kullanıcının klasörlerinde.
/// </summary>
internal static class WindowDocument
{
    private const int MaxRecent = 80;
    private const int SearchDepth = 4;
    private const int SearchBudgetMs = 1500;

    private const int MaxExtensionLength = 6;

    /// <summary>Uygulamaların başlıkta dosya adıyla kendi adını ayırdığı işaretler ("ad.png - Paint", "ad.txt, Not Defteri").</summary>
    private static readonly string[] TitleSeparators = [" - ", " – ", " — ", ", ", " ‹ ", " › ", " | "];

    private static readonly Dictionary<string, (DateTime Written, string? Target)> RecentTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Düzenleme kartındaki sayı için, diske bakmadan: başlıktaki dosya adı ya da Gezgin'in klasörü.</summary>
    public static string Key(ApplicationWindow window)
    {
        return FileNames(window.Title).FirstOrDefault() ?? (IsExplorer(window) ? window.Title ?? "" : "");
    }

    /// <summary>Pencerenin gösterdiği dosyanın ya da klasörün yolu; bulunamazsa null (o zaman uygulamanın kendisi kaydedilir).</summary>
    public static string? Find(ApplicationWindow window)
    {
        if (IsExplorer(window))
        {
            return ExplorerFolder(window.Handle);
        }

        List<string> names = FileNames(window.Title).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        return names.Select(name => FromCommandLine(window.ProcId, name) ?? FromRecent(name)).FirstOrDefault(path => path != null)
            ?? SearchUserFolders(names);
    }

    /// <summary>Düzen geri açılırken: bu pencere o dosyayı ya da klasörü mü gösteriyor?</summary>
    public static bool Shows(ApplicationWindow window, string path)
    {
        if (Directory.Exists(path))
        {
            return IsExplorer(window) && string.Equals(ExplorerFolder(window.Handle), path, StringComparison.OrdinalIgnoreCase);
        }

        return window.Title?.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) == true;
    }

    private static IEnumerable<string> FileNames(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            yield break;
        }

        foreach (string part in title.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // Değiştirilmiş belgenin başındaki işaret (*ad.txt, ●ad.txt).
            string name = part.Trim().TrimStart('*', '●', '•').Trim();
            string extension = Path.GetExtension(name);
            if (name.Length > extension.Length && extension.Length is > 1 and <= MaxExtensionLength + 1
                && extension.Skip(1).All(char.IsLetterOrDigit) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                yield return name;
            }
        }
    }

    private static bool IsExplorer(ApplicationWindow window)
    {
        return string.Equals(Path.GetFileName(window.WinFileName), "explorer.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gezgin penceresinin klasörü (sekmelerde ilki). Bu Bilgisayar gibi sanal klasörlerin yolu yok.</summary>
    private static string? ExplorerFolder(IntPtr handle)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic window in shell.Windows())
            {
                if ((long)window.HWND == handle.ToInt64())
                {
                    string path = window.Document.Folder.Self.Path;
                    return Directory.Exists(path) ? path : null;
                }
            }
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            // Pencere tam bu sırada kapanmış ya da Gezgin olmayan bir kabuk penceresi.
        }
        return null;
    }

    private static string? FromCommandLine(uint? processId, string name)
    {
        if (processId is not { } id || CommandLine(id) is not { } commandLine)
        {
            return null;
        }

        return Arguments(commandLine).Skip(1)
            .FirstOrDefault(argument => string.Equals(Path.GetFileName(argument), name, StringComparison.OrdinalIgnoreCase) && File.Exists(argument));
    }

    private static string? FromRecent(string name)
    {
        string recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        try
        {
            IEnumerable<FileInfo> links = new DirectoryInfo(recent).EnumerateFiles("*.lnk")
                .OrderByDescending(link => link.LastWriteTimeUtc)
                .Take(MaxRecent);
            foreach (FileInfo link in links)
            {
                // Son Kullanılanlar'daki kısayolun adı hedefin adıdır; ötekilerin hedefi okunmaz.
                if (!Path.GetFileNameWithoutExtension(link.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!RecentTargets.TryGetValue(link.FullName, out var cached) || cached.Written != link.LastWriteTimeUtc)
                {
                    cached = (link.LastWriteTimeUtc, LinkTarget(link.FullName));
                    RecentTargets[link.FullName] = cached;
                }
                if (cached.Target != null && File.Exists(cached.Target))
                {
                    return cached.Target;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>
    /// Son çare: kullanıcının klasörlerinde aynı adlı dosya, birden çoksa en son değişen. Süreyle sınırlı; süre her
    /// klasöre inerken sorulur, yoksa büyük bir klasör (kod deposu dolu Belgeler) sıradakilere hiç sıra bırakmıyordu.
    /// Belgeler bu yüzden en sonda.
    /// </summary>
    private static string? SearchUserFolders(List<string> names)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        ];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = SearchDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
        };
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        long until = Environment.TickCount64 + SearchBudgetMs;

        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var files = new FileSystemEnumerable<FileInfo>(root, (ref FileSystemEntry entry) => (FileInfo)entry.ToFileSystemInfo(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && wanted.Contains(entry.FileName.ToString()),
                ShouldRecursePredicate = (ref FileSystemEntry entry) => Environment.TickCount64 < until && !entry.FileName.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            };

            try
            {
                // Başlıktaki ilk ad öncelikli; aynı addan birden çoksa en son değişen.
                FileInfo? best = files.OrderBy(file => names.FindIndex(name => name.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                    .ThenByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (best != null)
                {
                    return best.FullName;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            if (Environment.TickCount64 >= until)
            {
                break;
            }
        }
        return null;
    }

    private static string? LinkTarget(string link)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string target = shell.CreateShortcut(link).TargetPath;
            return target.Length > 0 ? target : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Arguments(string commandLine)
    {
        IntPtr list = CommandLineToArgvW(commandLine, out int count);
        if (list == IntPtr.Zero)
        {
            yield break;
        }

        try
        {
            var arguments = new string[count];
            for (int i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size)) ?? "";
            }
            foreach (string argument in arguments)
            {
                yield return argument;
            }
        }
        finally
        {
            LocalFree(list);
        }
    }

    /// <summary>Aynı kullanıcının sürecinde okunur (Windows 8.1+); yönetici olarak çalışanınki okunamaz.</summary>
    private static string? CommandLine(uint processId)
    {
        const uint queryLimited = 0x1000;
        const int commandLineInformation = 60;

        IntPtr process = OpenProcess(queryLimited, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            NtQueryInformationProcess(process, commandLineInformation, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(process, commandLineInformation, buffer, length, out _) != 0)
                {
                    return null;
                }

                // UNICODE_STRING: uzunluk (bayt), kapasite, metnin adresi.
                int bytes = Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
