using System.IO;
using ManagedShell.Common.Logging;
using Microsoft.Win32;

namespace IslandBar;

/// <summary>
/// Uygulamanın eski adı GrupBar'dı. Ayarlar, gruplar ve kısayol kopyaları yeni klasöre bir kez kopyalanır;
/// eski klasör silinmez. Ayarların içindeki kısayol yolları da eski klasörü gösterdiği için yeni yere çevrilir.
/// </summary>
internal static class LegacyData
{
    private const string OldName = "GrupBar";
    private const string NewName = "IslandBar";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Tek kopya kilidi alındıktan sonra, görev çubuğu kaydı okunmadan önce çağrılmalı.</summary>
    public static void Migrate()
    {
        try
        {
            CopyAppData();
            MoveStartupEntry();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShellLogger.Error($"LegacyData: Could not migrate: {e.Message}");
        }
    }

    private static void CopyAppData()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string oldFolder = Path.Combine(roaming, OldName);
        string newFolder = Path.Combine(roaming, NewName);
        if (!Directory.Exists(oldFolder) || Directory.Exists(newFolder))
        {
            return;
        }

        // Önce geçici yere: yarıda kalan kopya bir sonraki açılışta "taşınmış" sayılmasın.
        string temporary = newFolder + ".tmp";
        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, true);
        }
        CopyFolder(oldFolder, temporary);

        string settings = Path.Combine(temporary, "settings.json");
        if (File.Exists(settings))
        {
            File.WriteAllText(settings, File.ReadAllText(settings).Replace(Escaped(oldFolder), Escaped(newFolder), StringComparison.OrdinalIgnoreCase));
        }

        Directory.Move(temporary, newFolder);
    }

    private static string Escaped(string path) => path.Replace(@"\", @"\\");

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
        foreach (string folder in Directory.GetDirectories(from))
        {
            CopyFolder(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    /// <summary>"Windows'la başlat" kaydı silinen exe'yi göstermesin; Görev Yöneticisi'ndeki kapalı durumu da korunur.</summary>
    private static void MoveStartupEntry()
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (run?.GetValue(OldName) == null)
        {
            return;
        }

        run.SetValue(NewName, $"\"{Environment.ProcessPath}\"");
        run.DeleteValue(OldName, false);

        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
        if (approved?.GetValue(OldName) is byte[] state)
        {
            approved.SetValue(NewName, state, RegistryValueKind.Binary);
            approved.DeleteValue(OldName, false);
        }
    }
}
