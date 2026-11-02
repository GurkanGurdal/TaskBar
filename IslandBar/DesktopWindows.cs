using System.Drawing;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>Bir uygulama penceresi ekranda gerçekten o bölgeyi kaplıyor mu?</summary>
internal static class DesktopWindows
{
    private const int DwmExtendedFrameBounds = 9;
    private const int DwmCloaked = 14;

    /// <summary>
    /// Küçültülmüş ve cloak'lu pencereler (başka sanal masaüstü, askıya alınmış Store uygulaması) sayılmaz.
    /// Dış gölge pencere dikdörtgeninin içinde: görünen çerçeveye bakılır, yoksa yan yana duran pencere üstünde sanılır.
    /// </summary>
    public static bool Covers(IntPtr window, Rectangle area)
    {
        if (!IsShown(window))
        {
            return false;
        }

        if (DwmGetWindowAttribute(window, DwmExtendedFrameBounds, out Bounds bounds, Marshal.SizeOf<Bounds>()) != 0 && !GetWindowRect(window, out bounds))
        {
            return false;
        }

        return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom).IntersectsWith(area);
    }

    /// <summary>Ekranda görünüyor mu: küçültülmüş ve cloak'lu pencereler görünmez.</summary>
    public static bool IsShown(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window))
        {
            return false;
        }

        return DwmGetWindowAttribute(window, DwmCloaked, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    /// <summary>Üst seviye pencerelerin sırası, en üstteki önce.</summary>
    public static List<IntPtr> ZOrder()
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);

    [DllImport("dwmapi")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("dwmapi")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Bounds value, int size);
}
