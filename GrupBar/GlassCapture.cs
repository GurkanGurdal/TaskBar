using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GrupBar;

/// <summary>Adanın arkasından alınmış, cam için işlenmiş görüntü.</summary>
internal sealed record GlassFrame(byte[] Pixels, int Width, int Height, double Luminance);

/// <summary>
/// Adanın arkasındaki ekranı alıp cama dönüştürür. Windows'un hazır bulanıklığının miktarı sabit ve çok
/// güçlü; arka planı tamamen siliyor. Burada görüntüyü küçültüp doygunlaştırıyor, kenarlarda kırıyoruz.
/// Asıl bulanıklığı XAML'daki BlurEffect yapar, ayarlaması oradan.
/// </summary>
internal static class GlassCapture
{
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;

    /// <summary>Tam görüntünün kaçta biri işlenir; zaten bulanıklaşacak, ayrıntı gerekmiyor.</summary>
    private const int Scale = 3;
    private const double Saturation = 1.55;

    /// <summary>Kenar kırılmasının etkilediği şerit, küçültülmüş piksel olarak.</summary>
    private const int LensBand = 6;
    private const double LensStrength = 0.9;

    /// <summary>Daha eski Windows'ta bu bayrak pencereyi siyah gösterir, ada kendini görüntüye karıştırır.</summary>
    public static bool CanExcludeWindow { get; } = Environment.OSVersion.Version.Build >= 19041;

    /// <summary>
    /// Ekrandan görüntü alır. Pencere o sırada görünürse kendini görüntüye karıştırmaması için
    /// kısa süreliğine yakalamadan hariç tutulur; ekranda görünmeye devam eder.
    /// </summary>
    public static GlassFrame? Capture(Rectangle area, IntPtr excludeWindow)
    {
        if (area.Width < Scale || area.Height < Scale)
        {
            return null;
        }

        if (excludeWindow == IntPtr.Zero)
        {
            return Process(area);
        }

        if (!CanExcludeWindow || !SetWindowDisplayAffinity(excludeWindow, WdaExcludeFromCapture))
        {
            return null;
        }

        try
        {
            // Hariç tutma bir sonraki kompozisyonda geçerli olur.
            DwmFlush();
            DwmFlush();
            return Process(area);
        }
        finally
        {
            SetWindowDisplayAffinity(excludeWindow, WdaNone);
        }
    }

    /// <summary>Ekrandaki bir bölgenin ortalama parlaklığı, 0 ile 1 arasında.</summary>
    public static double AverageLuminance(Rectangle area)
    {
        using Bitmap bitmap = Grab(area);
        int[] source = ReadPixels(bitmap);

        double total = 0;
        for (int i = 0; i < source.Length; i += 3)
        {
            total += Luma(source[i]);
        }
        return total / Math.Max(1, (source.Length + 2) / 3) / 255;
    }

    private static GlassFrame Process(Rectangle area)
    {
        using Bitmap bitmap = Grab(area);
        int[] source = ReadPixels(bitmap);

        int width = area.Width / Scale;
        int height = area.Height / Scale;
        var small = new int[width * height];
        double lumaTotal = 0;

        // Kutu ortalaması ile küçült.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int sy = 0; sy < Scale; sy++)
                {
                    int row = (y * Scale + sy) * area.Width;
                    for (int sx = 0; sx < Scale; sx++)
                    {
                        int pixel = source[row + x * Scale + sx];
                        r += (pixel >> 16) & 0xFF;
                        g += (pixel >> 8) & 0xFF;
                        b += pixel & 0xFF;
                    }
                }

                int count = Scale * Scale;
                small[y * width + x] = (r / count << 16) | (g / count << 8) | (b / count);
                lumaTotal += Luma(small[y * width + x]);
            }
        }

        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sourceY = Refract(y, height);
            for (int x = 0; x < width; x++)
            {
                int pixel = small[sourceY * width + Refract(x, width)];

                // Renkler camdan geçerken canlanır.
                double luma = Luma(pixel);
                int at = (y * width + x) * 4;
                pixels[at] = Saturate(pixel & 0xFF, luma);
                pixels[at + 1] = Saturate((pixel >> 8) & 0xFF, luma);
                pixels[at + 2] = Saturate((pixel >> 16) & 0xFF, luma);
            }
        }

        return new GlassFrame(pixels, width, height, lumaTotal / (width * height) / 255);
    }

    /// <summary>Kenara yakın noktalar içeriden örneklenir; cam kenarındaki bükülme buradan gelir.</summary>
    private static int Refract(int position, int size)
    {
        int fromEdge = Math.Min(position, size - 1 - position);
        if (fromEdge >= LensBand)
        {
            return position;
        }

        double depth = LensBand - fromEdge;
        int shift = (int)(depth * depth / LensBand * LensStrength);
        return position < size / 2
            ? Math.Min(position + shift, size - 1)
            : Math.Max(position - shift, 0);
    }

    private static byte Saturate(int channel, double luma)
    {
        return (byte)Math.Clamp(luma + (channel - luma) * Saturation, 0, 255);
    }

    private static double Luma(int pixel)
    {
        return 0.2126 * ((pixel >> 16) & 0xFF) + 0.7152 * ((pixel >> 8) & 0xFF) + 0.0722 * (pixel & 0xFF);
    }

    private static Bitmap Grab(Rectangle area)
    {
        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        return bitmap;
    }

    private static int[] ReadPixels(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width, bitmap.Width);
            }
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [DllImport("user32", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("dwmapi")]
    private static extern int DwmFlush();
}
