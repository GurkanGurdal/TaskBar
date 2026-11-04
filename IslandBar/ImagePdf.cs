using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace IslandBar;

/// <summary>
/// Resimlerden PDF, kurulu bir şey gerektirmeden: her resim JPEG olarak olduğu gibi gömülür (DCTDecode), her biri bir
/// sayfa. Sayfa resmin kendi ölçüsünde (DPI'ından; bilinmiyorsa 96), uzun kenarı A4'ün uzun kenarını aşarsa o boya
/// indirilir; çözünürlük değişmez, yalnızca sayfanın görünen boyu.
/// </summary>
internal static class ImagePdf
{
    private const double PointsPerInch = 72;
    private const double DefaultDpi = 96;
    private const double MaxSide = 842;

    /// <param name="pages">Her sayfanın tek kareli JPEG kodlayıcısı; sırayla, gerektikçe oluşturulur.</param>
    /// <param name="count">Sayfa sayısı (ilerleme için).</param>
    public static void Write(string path, IEnumerable<JpegBitmapEncoder> pages, IProgress<double> progress, int count)
    {
        using var file = new FileStream(path, FileMode.CreateNew);
        try
        {
            WritePdf(file, pages, progress, count);
        }
        catch
        {
            file.Dispose();
            File.Delete(path);
            throw;
        }
    }

    private static void WritePdf(Stream file, IEnumerable<JpegBitmapEncoder> pages, IProgress<double> progress, int count)
    {
        var offsets = new List<long> { 0, 0, 0 };
        var kids = new List<int>();
        Ascii(file, "%PDF-1.4\n%âãÏÓ\n");

        int done = 0;
        foreach (JpegBitmapEncoder page in pages)
        {
            BitmapFrame frame = page.Frames[0];
            using var jpeg = new MemoryStream();
            page.Save(jpeg);

            (double width, double height) = PageSize(frame);
            int image = offsets.Count;
            offsets.Add(file.Position);
            Ascii(file, $"{image} 0 obj\n<< /Type /XObject /Subtype /Image /Width {frame.PixelWidth} /Height {frame.PixelHeight} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            jpeg.Position = 0;
            jpeg.CopyTo(file);
            Ascii(file, "\nendstream\nendobj\n");

            string drawing = $"q {Number(width)} 0 0 {Number(height)} 0 0 cm /Im0 Do Q";
            int content = offsets.Count;
            offsets.Add(file.Position);
            Ascii(file, $"{content} 0 obj\n<< /Length {drawing.Length} >>\nstream\n{drawing}\nendstream\nendobj\n");

            int pageObject = offsets.Count;
            offsets.Add(file.Position);
            Ascii(file, $"{pageObject} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(width)} {Number(height)}] " +
                $"/Resources << /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>\nendobj\n");
            kids.Add(pageObject);
            progress.Report(++done / (double)Math.Max(count, 1));
        }

        offsets[1] = file.Position;
        Ascii(file, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Ascii(file, $"2 0 obj\n<< /Type /Pages /Kids [{string.Join(' ', kids.Select(kid => $"{kid} 0 R"))}] /Count {kids.Count} >>\nendobj\n");

        long xref = file.Position;
        var table = new StringBuilder($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
        {
            table.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        table.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Ascii(file, table.ToString());
    }

    private static (double Width, double Height) PageSize(BitmapSource image)
    {
        double dpiX = image.DpiX is > 1 and < 10000 ? image.DpiX : DefaultDpi;
        double dpiY = image.DpiY is > 1 and < 10000 ? image.DpiY : DefaultDpi;
        double width = image.PixelWidth * PointsPerInch / dpiX;
        double height = image.PixelHeight * PointsPerInch / dpiY;
        double shrink = Math.Min(1, MaxSide / Math.Max(width, height));
        return (width * shrink, height * shrink);
    }

    private static string Number(double value)
    {
        return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>PDF'in yapı kısmı; ikinci satırdaki dört bayt ikili dosya işareti (Latin-1).</summary>
    private static void Ascii(Stream file, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        file.Write(bytes, 0, bytes.Length);
    }
}
