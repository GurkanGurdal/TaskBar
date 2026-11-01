using System.Windows.Media;

namespace IslandBar;

/// <summary>Bulanık görüntünün üstündeki ton: koyuda hafif aydınlık, parlakta yazı okunacak kadar koyu.</summary>
internal static class GlassTint
{
    private const double DarkLuminance = 0.2;
    private const double BrightLuminance = 0.6;

    private static readonly Color OnDark = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Color OnBright = Color.FromArgb(0x88, 0x10, 0x11, 0x14);

    /// <summary>Görüntü alınmadan önceki ton; camsız temada yüzeyin kendisi.</summary>
    public static Color Rest => ThemeManager.SurfaceColor;

    public static Color For(double luminance)
    {
        double t = Math.Clamp((luminance - DarkLuminance) / (BrightLuminance - DarkLuminance), 0, 1);
        return Color.FromArgb(
            (byte)(OnDark.A + (OnBright.A - OnDark.A) * t),
            (byte)(OnDark.R + (OnBright.R - OnDark.R) * t),
            (byte)(OnDark.G + (OnBright.G - OnDark.G) * t),
            (byte)(OnDark.B + (OnBright.B - OnDark.B) * t));
    }

    /// <summary>Fark gözle görülmeyecek kadar küçük mü; küçükse ton değiştirmeye değmez.</summary>
    public static bool IsNear(Color a, Color b)
    {
        return Math.Abs(a.A - b.A) + Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 6;
    }
}
