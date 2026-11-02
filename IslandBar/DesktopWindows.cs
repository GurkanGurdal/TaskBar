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

    /// <summary>Sürecin açık bir menüsü (<c>#32768</c>, tepsi ikonlarının çoğunun sağ tık menüsü) var mı?</summary>
    public static bool HasMenu(uint process)
    {
        IntPtr menu = IntPtr.Zero;
        while ((menu = FindWindowEx(IntPtr.Zero, menu, MenuClass, null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(menu) && GetWindowThreadProcessId(menu, out uint owner) != 0 && owner == process)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Masaüstünün kendisi (Gezgin'in Progman / WorkerW pencereleri): öne gelse de açılır pencere sayılmaz.</summary>
    public static bool IsDesktop(IntPtr window)
    {
        if (window == GetShellWindow())
        {
            return true;
        }

        var name = new System.Text.StringBuilder(16);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "WorkerW" or "Progman";
    }

    /// <summary>Pencerenin şu an bulunduğu ekran. ManagedShell'in <c>HMonitor</c>'u yeni pencerede geç güncelleniyor.</summary>
    public static IntPtr MonitorOf(IntPtr window)
    {
        return MonitorFromWindow(window, MonitorDefaultToNearest);
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

    private const string MenuClass = "#32768";
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);

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
