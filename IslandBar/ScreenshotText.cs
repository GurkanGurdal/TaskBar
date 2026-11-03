using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using ManagedShell.Common.Logging;

namespace IslandBar;

/// <summary>
/// Ekran görüntüsündeki yazı (kullanıcının isteği). Windows'un kendi metin tanıması, internetsiz; dil kullanıcının
/// Windows dillerinden seçilir (Türkçe kurulu, ölçüldü). Kurulu dil yoksa ya da yazı bulunamazsa boş döner.
/// </summary>
internal static class ScreenshotText
{
    public static async Task<string> RecognizeAsync(string path)
    {
        try
        {
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                return string.Empty;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth > OcrEngine.MaxImageDimension || decoder.PixelHeight > OcrEngine.MaxImageDimension)
            {
                return string.Empty;
            }

            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            OcrResult result = await engine.RecognizeAsync(bitmap);
            return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ShellLogger.Error($"ScreenshotText: Could not read {path}: {e.Message}");
            return string.Empty;
        }
    }
}
