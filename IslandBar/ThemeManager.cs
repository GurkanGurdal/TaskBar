using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

public enum BarTheme
{
    Glass,
    White
}

/// <summary>
/// Paleti (ThemeGlass.xaml, ThemeWhite.xaml) uygulama kaynaklarına koyar. Pencereler renkleri StaticResource ile
/// bir kez okur: tema değişince çubuklar kapatılıp yeniden açılır (App.Settings_PropertyChanged).
/// </summary>
internal static class ThemeManager
{
    /// <summary>Camsız temada ekran hiç alınmaz: ne işlemci harcanır ne de çubuk ekran görüntülerinden hariç tutulur.</summary>
    public static bool UsesGlass { get; private set; } = true;

    public static void Apply(BarTheme theme)
    {
        UsesGlass = theme == BarTheme.Glass;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(Load(theme == BarTheme.White ? "ThemeWhite.xaml" : "ThemeGlass.xaml"));

        // Sıra önemli: buradaki stiller paleti uygulama kaynaklarından okur.
        dictionaries.Add(Load("Theme.xaml"));
    }

    public static Color SurfaceColor => (Color)Application.Current.Resources["SurfaceColor"];

    public static Color AccentColor => ((SolidColorBrush)Application.Current.Resources["AccentFill"]).Color;

    public static Color TextColor => ((SolidColorBrush)Application.Current.Resources["TextPrimary"]).Color;

    private static ResourceDictionary Load(string file)
    {
        return new ResourceDictionary { Source = new Uri($"pack://application:,,,/{file}") };
    }
}

/// <summary>
/// Windows tepsi ikonlarını ve bazı uygulama ikonlarını koyu çubuk için beyaz çizer; açık zeminde görünmezler.
/// Yalnızca açık temada, saydam zemin üstünde tamamen beyaz olanları koyu yazı rengine çevirir. Renkli ikona dokunmaz.
/// </summary>
public sealed class IconInkConverter : IValueConverter
{
    private const int MinVisibleAlpha = 32;
    private const int MaxSaturationSpread = 24;
    private const int MinBrightness = 190;
    private const double MinWhiteShare = 0.92;

    /// <summary>Beyaz bir uygulama ikonu (belge, kâğıt) yüzeyi doldurur; simge gibi ince olanlar çevrilir.</summary>
    private const double MaxCoverage = 0.6;

    private static readonly ConditionalWeakTable<ImageSource, ImageSource> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (ThemeManager.UsesGlass || value is not BitmapSource source)
        {
            return value;
        }

        return Cache.GetValue(source, Ink);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static ImageSource Ink(ImageSource image)
    {
        var source = (BitmapSource)image;
        if (source.PixelWidth == 0 || source.PixelHeight == 0)
        {
            return source;
        }

        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        int visible = 0, white = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < MinVisibleAlpha)
            {
                continue;
            }

            visible++;
            int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            if (max - min <= MaxSaturationSpread && max >= MinBrightness)
            {
                white++;
            }
        }

        double coverage = visible / (double)(pixels.Length / 4);
        if (visible == 0 || coverage > MaxCoverage || white < visible * MinWhiteShare)
        {
            return source;
        }

        Color ink = ThemeManager.TextColor;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = ink.B;
            pixels[i + 1] = ink.G;
            pixels[i + 2] = ink.R;
        }

        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
