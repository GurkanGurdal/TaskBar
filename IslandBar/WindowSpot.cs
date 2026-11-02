using System.Runtime.InteropServices;
using ManagedShell.WindowsTasks;

namespace IslandBar;

/// <summary>
/// Bir pencerenin ekrandaki yeri ve boyu, ekran pikseliyle (süreç PerMonitorV2 olduğu için fiziksel). Grubun
/// çalışma düzeninde öğe başına bir tane tutulur, "Hepsini aç" pencereyi buraya koyar.
/// </summary>
public sealed class WindowSpot
{
    private const int ShowRestore = 9;
    private const int ShowMaximize = 3;
    private const uint NoZOrder = 0x4;
    private const uint NoActivate = 0x10;

    public int Left { get; set; }

    public int Top { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>Küçültülmüş pencerenin görünen bir yeri yok; o öğe düzene girmez.</summary>
    public static WindowSpot? Capture(ApplicationWindow window)
    {
        if (IsIconic(window.Handle) || !GetWindowRect(window.Handle, out Rect rect))
        {
            return null;
        }

        return new WindowSpot
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
            Maximized = IsZoomed(window.Handle)
        };
    }

    /// <summary>
    /// Yerine koyar. Ekran artık yoksa en yakın ekrana sığdırır. İki kez taşınır: başka DPI'lı ekrana geçen pencere
    /// ilk taşımada kendi boyunu yeniden hesaplıyor.
    /// </summary>
    public void Apply(ApplicationWindow window)
    {
        IntPtr handle = window.Handle;
        var bounds = new System.Drawing.Rectangle(Left, Top, Width, Height);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;

        if (IsIconic(handle) || IsZoomed(handle))
        {
            ShowWindow(handle, ShowRestore);
        }

        if (Maximized)
        {
            // Büyütülen pencere bulunduğu ekranda büyür; önce o ekrana taşınır.
            bounds = new System.Drawing.Rectangle(area.X, area.Y, Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        }
        else if (!System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
        {
            bounds.Width = Math.Min(bounds.Width, area.Width);
            bounds.Height = Math.Min(bounds.Height, area.Height);
            bounds.Location = new System.Drawing.Point(area.X + (area.Width - bounds.Width) / 2, area.Y + (area.Height - bounds.Height) / 2);
        }

        for (int i = 0; i < 2; i++)
        {
            SetWindowPos(handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoZOrder | NoActivate);
        }

        if (Maximized)
        {
            ShowWindow(handle, ShowMaximize);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32")]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
