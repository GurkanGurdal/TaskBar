using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>Adanın arkasından alınmış, cam için işlenmiş görüntü.</summary>
internal sealed record GlassFrame(byte[] Pixels, int Width, int Height, double Luminance);

/// <summary>
/// Adanın arkasındaki ekranı alıp cama dönüştürür. Windows'un hazır bulanıklığının miktarı sabit ve çok
/// güçlü; arka planı tamamen siliyor. Burada görüntüyü küçültüp doygunlaştırıyor, kenarlarda kırıyoruz.
/// Adada asıl bulanıklığı XAML'daki BlurEffect yapar, kartlarda <see cref="Blur"/>.
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
    private const int BlurPasses = 3;
    private const int SampleCellLength = 48;
    private const int MaxSampleCells = 16;

    /// <summary>Daha eski Windows'ta bu bayrak pencereyi siyah gösterir, ada kendini görüntüye karıştırır.</summary>
    public static bool CanExcludeWindow { get; } = Environment.OSVersion.Version.Build >= 19041;

    /// <summary>Kısa süreli pencereler (kapanan kartın kopyası) yeni kartın camına girmesin.</summary>
    private static readonly ConcurrentDictionary<IntPtr, byte> Transients = new();

    public static void AddTransient(IntPtr window) => Transients[window] = 0;

    public static void RemoveTransient(IntPtr window) => Transients.TryRemove(window, out _);

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

        IntPtr[] transients = CanExcludeWindow ? Transients.Keys.ToArray() : [];
        if (excludeWindow == IntPtr.Zero && transients.Length == 0)
        {
            return Process(area);
        }

        if (excludeWindow != IntPtr.Zero && (!CanExcludeWindow || !SetWindowDisplayAffinity(excludeWindow, WdaExcludeFromCapture)))
        {
            return null;
        }

        foreach (IntPtr transient in transients)
        {
            SetWindowDisplayAffinity(transient, WdaExcludeFromCapture);
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
            if (excludeWindow != IntPtr.Zero)
            {
                SetWindowDisplayAffinity(excludeWindow, WdaNone);
            }
            foreach (IntPtr transient in transients)
            {
                SetWindowDisplayAffinity(transient, WdaNone);
            }
        }
    }

    /// <summary>
    /// Kartların bulanıklığı: üç geçişli kutu bulanıklığı Gauss'a çok yakındır, küçültülmüş görüntüde birkaç piksel tutar.
    /// BlurEffect'in aksine görüntüsü alınırken yeniden hesaplanmaz (yazılımla Başlat kartında 90 ms sürüyordu).
    /// </summary>
    /// <param name="radius">Birim cinsinden, BlurEffect yarıçapıyla aynı anlamda.</param>
    public static GlassFrame Blur(GlassFrame frame, double radius, double dpiScale)
    {
        double sigma = radius / 2 * dpiScale / Scale;
        int half = (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2);
        if (half < 1)
        {
            return frame;
        }

        byte[] pixels = (byte[])frame.Pixels.Clone();
        byte[] buffer = new byte[pixels.Length];
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            BoxBlur(pixels, buffer, frame.Width, frame.Height, half, stepX: 4, stepY: frame.Width * 4);
            BoxBlur(buffer, pixels, frame.Height, frame.Width, half, stepX: frame.Width * 4, stepY: 4);
        }
        return frame with { Pixels = pixels };
    }

    /// <summary>Bir eksen boyunca hareketli toplam; kenarda son piksel tekrarlanır.</summary>
    private static void BoxBlur(byte[] source, byte[] target, int lineLength, int lines, int half, int stepX, int stepY)
    {
        int window = 2 * half + 1;
        for (int line = 0; line < lines; line++)
        {
            int start = line * stepY;
            for (int channel = 0; channel < 3; channel++)
            {
                int first = start + channel;
                int total = 0;
                for (int i = -half; i <= half; i++)
                {
                    total += source[first + Math.Clamp(i, 0, lineLength - 1) * stepX];
                }

                for (int i = 0; i < lineLength; i++)
                {
                    target[first + i * stepX] = (byte)(total / window);
                    total += source[first + Math.Min(i + half + 1, lineLength - 1) * stepX] - source[first + Math.Max(i - half, 0) * stepX];
                }
            }
        }
    }

    /// <summary>
    /// Şeritleri uzun kenarları boyunca hücrelere böler, her hücrenin ortalama rengini art arda verir (kırmızı, yeşil, mavi).
    /// Yeri değişen pencereyi de bir hücrede fark edecek kadar küçük, ekranı boşuna okumayacak kadar az.
    /// </summary>
    public static int[] SampleStrips(Rectangle[] strips)
    {
        var colors = new List<int>();
        foreach (Rectangle strip in strips)
        {
            using Bitmap bitmap = Grab(strip);
            int[] source = ReadPixels(bitmap);

            bool horizontal = strip.Width >= strip.Height;
            int length = horizontal ? strip.Width : strip.Height;
            int cells = Math.Clamp(length / SampleCellLength, 1, MaxSampleCells);
            var totals = new int[cells * 3];
            var counts = new int[cells];
            for (int y = 0; y < strip.Height; y++)
            {
                for (int x = 0; x < strip.Width; x++)
                {
                    int cell = (horizontal ? x : y) * cells / length;
                    int pixel = source[y * strip.Width + x];
                    totals[cell * 3] += (pixel >> 16) & 0xFF;
                    totals[cell * 3 + 1] += (pixel >> 8) & 0xFF;
                    totals[cell * 3 + 2] += pixel & 0xFF;
                    counts[cell]++;
                }
            }

            for (int i = 0; i < totals.Length; i++)
            {
                colors.Add(totals[i] / counts[i / 3]);
            }
        }
        return colors.ToArray();
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
