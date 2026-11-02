using System.IO;

namespace IslandBar;

/// <summary>
/// Otomatik gizleme tanı kaydı (%LocalAppData%\IslandBar\autohide.log): çubuğun kenarda açılmadığı anlar ve tam ekran
/// sayılan pencereler. Büyüyünce yarısı atılır.
/// </summary>
internal static class AutoHideLog
{
    private const long MaxBytes = 256 * 1024;

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandBar", "autohide.log");

    public static void Write(string message)
    {
        try
        {
            var info = new FileInfo(LogFile);
            if (info.Exists && info.Length > MaxBytes)
            {
                string[] lines = File.ReadAllLines(LogFile);
                File.WriteAllLines(LogFile, lines.Skip(lines.Length / 2));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tanı kaydı çubuğu bozmamalı.
        }
    }
}
