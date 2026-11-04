using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

/// <summary>
/// Resimden Windows simgesi (.ico): WIC'in ICO kodlayıcısı yok, dosyayı biz yazarız. Windows'un kullandığı boylar
/// (16-256) her biri PNG olarak gömülür (Vista'dan beri her boyda geçerli); kare olmayan resim saydam kareye ortalanır.
/// </summary>
internal static class IconFile
{
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static void Write(string path, BitmapSource image)
    {
        List<byte[]> pictures = Sizes.Select(size => Png(image, size)).ToList();
        using var file = new FileStream(path, FileMode.CreateNew);
        try
        {
            using var writer = new BinaryWriter(file);
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                // 256 boy 0 yazılır.
                writer.Write((byte)(Sizes[i] % 256));
                writer.Write((byte)(Sizes[i] % 256));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(pictures[i].Length);
                writer.Write(offset);
                offset += pictures[i].Length;
            }
            foreach (byte[] picture in pictures)
            {
                writer.Write(picture);
            }
        }
        catch
        {
            file.Dispose();
            File.Delete(path);
            throw;
        }
    }

    private static byte[] Png(BitmapSource image, int size)
    {
        double scale = Math.Min((double)size / image.PixelWidth, (double)size / image.PixelHeight);
        double width = image.PixelWidth * scale;
        double height = image.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(image, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
