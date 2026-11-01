using System.Management;

namespace IslandBar;

/// <summary>Dizüstü ekranının parlaklığı (WMI). Harici monitörler bunu desteklemez.</summary>
internal static class ScreenBrightness
{
    private const string Scope = @"root\WMI";

    /// <summary>0-100; desteklenmiyorsa null.</summary>
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementBaseObject monitor in searcher.Get())
            {
                return Convert.ToInt32(monitor["CurrentBrightness"]);
            }
        }
        catch (ManagementException)
        {
            // Harici monitörde sınıf hiç bulunmaz.
        }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementBaseObject monitor in searcher.Get())
            {
                ((ManagementObject)monitor).InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(percent, 0, 100) });
            }
        }
        catch (ManagementException)
        {
            // Ekran o sırada kapanmış olabilir; kaydırıcı bir sonraki açılışta gerçeği gösterir.
        }
    }
}
