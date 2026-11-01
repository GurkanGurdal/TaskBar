using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using ManagedShell.Common.Logging;
using Microsoft.Win32;

namespace IslandBar;

/// <summary>
/// "Windows'la başlat": HKCU Run anahtarı. Durumun kaynağı kayıt defteri, settings.json değil.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "IslandBar";
    private const string StartMenuShortcutName = "IslandBar.lnk";

    /// <summary>Görev Yöneticisi'nin "etkin" kaydı.</summary>
    private static readonly byte[] EnabledState = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            // Başka bir kopyayı gösteren kayıt bu kopya için açık sayılmaz.
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!string.Equals(run?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Görev Yöneticisi'nden kapatılınca Run değeri kalır, burada ilk bayt tek sayı olur.
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled)
            {
                run.SetValue(ValueName, Command);
            }
            else
            {
                run.DeleteValue(ValueName, false);
            }
        }

        // Kullanıcı burada açıkça seçti; Görev Yöneticisi'ndeki eski "devre dışı" kaydı kalmasın.
        using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        if (enabled)
        {
            approved.SetValue(ValueName, EnabledState, RegistryValueKind.Binary);
        }
        else
        {
            approved.DeleteValue(ValueName, false);
        }
    }

    /// <summary>
    /// Açık kayıtta onay kaydı yoksa ekler. Eski sürüm bunu siliyordu ve Run'da duran komut oturum açılışında
    /// çalıştırılmıyordu (Shell-Core 9707'de yok); Run'daki onay kaydı olmayan tek uygulama buydu.
    /// Görev Yöneticisi'nden kapatılmışsa kayıt vardır, dokunulmaz.
    /// </summary>
    public static void RepairApproval()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (!string.Equals(run?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (approved.GetValue(ValueName) == null)
            {
                approved.SetValue(ValueName, EnabledState, RegistryValueKind.Binary);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShellLogger.Error($"StartupRegistration: Could not repair approval: {e.Message}");
        }
    }

    /// <summary>Çubuk kapalıyken Windows aramasından ve Başlat'tan açılabilsin. Exe taşınmışsa kısayol yenilenir.</summary>
    public static void EnsureStartMenuShortcut()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuShortcutName);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(path);
            if (File.Exists(path) && string.Equals((string)shortcut.TargetPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            shortcut.Save();
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"StartupRegistration: Could not write {path}: {e.Message}");
        }
    }
}
