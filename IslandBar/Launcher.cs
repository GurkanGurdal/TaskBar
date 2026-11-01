using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ManagedShell.Common.Logging;

namespace IslandBar;

internal static class Launcher
{
    public static bool Launch(GroupItem item)
    {
        var startInfo = new ProcessStartInfo(item.Path) { UseShellExecute = true };

        // Programlar yanlarındaki dosyaları IslandBar'ın klasöründe aramasın.
        if (File.Exists(item.Path))
        {
            startInfo.WorkingDirectory = Path.GetDirectoryName(item.Path) ?? "";
        }

        try
        {
            Process.Start(startInfo)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            ShellLogger.Error($"Launcher: Could not start {item.Path}: {e.Message}");
            return false;
        }
    }
}
