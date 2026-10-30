using Microsoft.Win32;

namespace GrupBar;

/// <summary>
/// "Windows'la başlat": HKCU Run anahtarı. Durumun kaynağı kayıt defteri, settings.json değil.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "GrupBar";

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
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
        approved?.DeleteValue(ValueName, false);
    }
}
