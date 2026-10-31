using System.Runtime.InteropServices;

namespace GrupBar;

/// <summary>Win+P'nin yaptığı iş: ekranların yalnızca bilgisayar / yinele / genişlet / yalnızca ikinci modu.</summary>
internal static class DisplayTopology
{
    public const uint Internal = 0x1;
    public const uint Clone = 0x2;
    public const uint Extend = 0x4;
    public const uint External = 0x8;

    private const uint QueryDatabaseCurrent = 0x4;
    private const uint Validate = 0x40;
    private const uint Apply = 0x80;

    // DISPLAYCONFIG_PATH_INFO ve DISPLAYCONFIG_MODE_INFO boyutları; içerikleri kullanılmıyor, Windows yer istiyor.
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    /// <summary>Şu anki mod; okunamazsa 0.</summary>
    public static uint Current()
    {
        if (GetDisplayConfigBufferSizes(QueryDatabaseCurrent, out uint paths, out uint modes) != 0)
        {
            return 0;
        }

        var pathBuffer = new byte[paths * PathInfoSize];
        var modeBuffer = new byte[modes * ModeInfoSize];
        return QueryDisplayConfig(QueryDatabaseCurrent, ref paths, pathBuffer, ref modes, modeBuffer, out uint topology) == 0 ? topology : 0;
    }

    /// <summary>Bu mod bu bilgisayarda kurulabilir mi? Ekranı değiştirmeden sorar.</summary>
    public static bool CanApply(uint topology)
    {
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Validate | topology) == 0;
    }

    /// <summary>Modu uygular; başarıda 0, değilse Windows hata kodu. Ekranlar yeniden kurulduğu için sürebilir.</summary>
    public static int Set(uint topology)
    {
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Apply | topology);
    }

    [DllImport("user32")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32")]
    private static extern int QueryDisplayConfig(uint flags, ref uint paths, byte[] pathInfo, ref uint modes, byte[] modeInfo, out uint topology);

    [DllImport("user32")]
    private static extern int SetDisplayConfig(uint paths, IntPtr pathInfo, uint modes, IntPtr modeInfo, uint flags);
}
