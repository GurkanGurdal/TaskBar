using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace IslandBar;

internal enum ConversionKind
{
    Image,
    Word,
    Excel,
    PowerPoint,
    PdfPages,
    Media
}

/// <param name="Label">Menüde.</param>
/// <param name="Short">Çubuktaki kartta, "rapor.pdf → Word".</param>
internal sealed record ConversionTarget(string Label, string Short, string Extension, ConversionKind Kind);

/// <summary>
/// Dosya dönüştürmeler, kurulu bir şey gerektirmeden (kullanıcının seçtiği dört tür): resimler Windows'un kodlayıcılarıyla
/// (WIC; HEIC, WebP, RAW yalnızca okunur), belgeler kullanıcının Office'iyle (görünmez açılır, kendi açtığımız kapanır),
/// PDF sayfaları Windows'un PDF okuyucusuyla, ses ve video Windows'un dönüştürücüsüyle (Media Foundation). Sonuç özgün
/// dosyanın yanına, aynı adla yazılır (varsa "ad (2)"); özgün dosyaya dokunulmaz.
/// </summary>
internal static class FileConversions
{
    private const int JpegQuality = 92;

    /// <summary>PDF sayfası 96 DPI ölçüsünün iki katı (192 DPI): ekranda keskin, dosya makul.</summary>
    private const double PdfScale = 2;

    // Office sabitleri.
    private const int WordDocx = 12;
    private const int WordPdf = 17;
    private const int ExcelPdf = 0;
    private const int PowerPointPdf = 32;
    private const int NoAlerts = 0;
    private const int PowerPointNoAlerts = 1;
    private const int MacrosOff = 3;
    private const int DontSave = 0;

    private static readonly ConversionTarget Png = new("PNG resmi", "PNG", ".png", ConversionKind.Image);
    private static readonly ConversionTarget Jpeg = new("JPEG resmi", "JPEG", ".jpg", ConversionKind.Image);
    private static readonly ConversionTarget Bmp = new("BMP resmi", "BMP", ".bmp", ConversionKind.Image);
    private static readonly ConversionTarget Tiff = new("TIFF resmi", "TIFF", ".tif", ConversionKind.Image);
    private static readonly ConversionTarget Gif = new("GIF resmi", "GIF", ".gif", ConversionKind.Image);
    private static readonly ConversionTarget WordFromPdf = new("Word belgesi (.docx)", "Word", ".docx", ConversionKind.Word);
    private static readonly ConversionTarget PdfPages = new("PNG resimleri (her sayfa)", "PNG", ".png", ConversionKind.PdfPages);
    private static readonly ConversionTarget WordToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.Word);
    private static readonly ConversionTarget WordToDocx = new("Word belgesi (.docx)", "Word", ".docx", ConversionKind.Word);
    private static readonly ConversionTarget ExcelToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.Excel);
    private static readonly ConversionTarget PowerPointToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.PowerPoint);
    private static readonly ConversionTarget Mp3 = new("MP3 ses", "MP3", ".mp3", ConversionKind.Media);
    private static readonly ConversionTarget M4a = new("M4A ses", "M4A", ".m4a", ConversionKind.Media);
    private static readonly ConversionTarget Wav = new("WAV ses", "WAV", ".wav", ConversionKind.Media);
    private static readonly ConversionTarget Wma = new("WMA ses", "WMA", ".wma", ConversionKind.Media);
    private static readonly ConversionTarget Mp4 = new("MP4 video", "MP4", ".mp4", ConversionKind.Media);
    private static readonly ConversionTarget Wmv = new("WMV video", "WMV", ".wmv", ConversionKind.Media);

    private static readonly ConversionTarget[] ImageTargets = [Png, Jpeg, Bmp, Tiff, Gif];
    private static readonly ConversionTarget[] AudioTargets = [Mp3, M4a, Wav, Wma];
    private static readonly ConversionTarget[] VideoTargets = [Mp4, Wmv, Mp3, M4a];

    /// <summary>Kaynak uzantı → olabilecek hedefler; dosyanın kendi biçimi de listede (birden çok dosyada atlanır).</summary>
    private static readonly Dictionary<string, ConversionTarget[]> TargetsByExtension = Build();

    private static Dictionary<string, ConversionTarget[]> Build()
    {
        var map = new Dictionary<string, ConversionTarget[]>(StringComparer.OrdinalIgnoreCase);
        void Add(ConversionTarget[] targets, params string[] extensions)
        {
            foreach (string extension in extensions)
            {
                map[extension] = targets;
            }
        }

        Add(ImageTargets, ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".heic", ".heif", ".hif",
            ".avif", ".webp", ".jxr", ".wdp", ".ico", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf");
        Add([WordFromPdf, PdfPages], ".pdf");
        Add([WordToPdf, WordToDocx], ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt");
        Add([ExcelToPdf], ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods");
        Add([PowerPointToPdf], ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp");
        Add(AudioTargets, ".mp3", ".m4a", ".wav", ".wma", ".aac", ".flac");
        Add(VideoTargets, ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv");
        return map;
    }

    /// <summary>
    /// Seçilen dosyaların hepsine uyan hedefler, menünün sırasıyla. Dosya zaten o biçimdeyse atlanır; hepsi o biçimdeyse
    /// hedef listede yok.
    /// </summary>
    public static IReadOnlyList<ConversionTarget> TargetsFor(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || files.Any(file => !TargetsByExtension.ContainsKey(Path.GetExtension(file))))
        {
            return [];
        }

        return TargetsByExtension[Path.GetExtension(files[0])]
            .Where(target => files.All(file => TargetsByExtension[Path.GetExtension(file)].Contains(target)))
            .Where(target => files.Any(file => !IsAlready(file, target)))
            .ToList();
    }

    /// <summary>Dosya zaten hedef biçimde (jpg ile jpeg aynı); PDF sayfaları hiçbir zaman.</summary>
    public static bool IsAlready(string file, ConversionTarget target)
    {
        string extension = Path.GetExtension(file).ToLowerInvariant();
        return target.Kind != ConversionKind.PdfPages && target.Extension switch
        {
            ".jpg" => extension is ".jpg" or ".jpeg" or ".jpe" or ".jfif",
            ".tif" => extension is ".tif" or ".tiff",
            ".docx" => extension == ".docx",
            _ => extension == target.Extension
        };
    }

    /// <summary>Tek dosya; yazılan dosyaların (PDF sayfalarında klasörün) yolu.</summary>
    /// <param name="progress">0..1; ilerleme bilinmeyen dönüşümler çağırmaz.</param>
    public static Task<string> ConvertAsync(string file, ConversionTarget target, IProgress<double> progress, CancellationToken token)
    {
        return target.Kind switch
        {
            ConversionKind.Image => OnSta(() => ConvertImage(file, target)),
            ConversionKind.Word => OnSta(() => ConvertWithWord(file, target)),
            ConversionKind.Excel => OnSta(() => ConvertWithExcel(file)),
            ConversionKind.PowerPoint => OnSta(() => ConvertWithPowerPoint(file)),
            ConversionKind.PdfPages => RenderPdfPagesAsync(file, progress, token),
            _ => TranscodeAsync(file, target, progress, token)
        };
    }

    /// <summary>Özgün dosyanın yanında, aynı adla; varsa "ad (2)". Uzantı boşsa klasör adı.</summary>
    public static string FreePath(string file, string extension)
    {
        string directory = Path.GetDirectoryName(file)!;
        string name = Path.GetFileNameWithoutExtension(file);
        string path = Path.Combine(directory, name + extension);
        for (int number = 2; File.Exists(path) || Directory.Exists(path); number++)
        {
            path = Path.Combine(directory, $"{name} ({number}){extension}");
        }
        return path;
    }

    /// <summary>WPF görüntüleri ve Office STA ister; her dönüşüm kendi iş parçacığında.</summary>
    private static Task<string> OnSta(Func<string> work)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = nameof(FileConversions)
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    #region Images
    /// <summary>
    /// En büyük kare (ICO'da en büyük boy, GIF ve TIFF'te ilk kare). Telefon fotoğraflarındaki yön bilgisi görüntüye
    /// işlenir, çünkü yeni dosyada üst bilgi yok. JPEG, BMP ve GIF saydamlık tutmaz: saydam yerler beyaz olur.
    /// </summary>
    private static string ConvertImage(string file, ConversionTarget target)
    {
        BitmapSource image;
        using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames.MaxBy(candidate => candidate.PixelWidth * candidate.PixelHeight)!;
            image = Oriented(frame, Orientation(frame));
        }

        BitmapEncoder encoder;
        switch (target.Extension)
        {
            case ".png":
                encoder = new PngBitmapEncoder();
                break;
            case ".tif":
                encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                break;
            case ".jpg":
                encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
                image = OnWhite(image);
                break;
            case ".bmp":
                encoder = new BmpBitmapEncoder();
                image = OnWhite(image);
                break;
            default:
                encoder = new GifBitmapEncoder();
                image = OnWhite(image);
                break;
        }

        encoder.Frames.Add(BitmapFrame.Create(image));
        string output = FreePath(file, target.Extension);
        using var stream = new FileStream(output, FileMode.CreateNew);
        try
        {
            encoder.Save(stream);
        }
        catch
        {
            stream.Dispose();
            File.Delete(output);
            throw;
        }
        return output;
    }

    private static ushort Orientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value ? value : (ushort)1;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or COMException)
        {
            return 1;
        }
    }

    /// <summary>EXIF yönü: 2 aynala, 3 180°, 4 dikey aynala, 5 aynala + 270°, 6 90°, 7 aynala + 90°, 8 270° (saat yönü).</summary>
    private static BitmapSource Oriented(BitmapSource image, ushort orientation)
    {
        (bool mirror, double angle) = orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0)
        };
        if (!mirror && angle == 0)
        {
            return image;
        }

        var transform = new TransformGroup();
        if (mirror)
        {
            transform.Children.Add(new ScaleTransform(-1, 1));
        }
        transform.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(image, transform);
    }

    /// <summary>Saydamlık beyaz zemine karıştırılır.</summary>
    private static BitmapSource OnWhite(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            for (int channel = 0; channel < 3; channel++)
            {
                pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha)) / 255);
            }
            pixels[i + 3] = 255;
        }

        double dpiX = image.DpiX > 0 ? image.DpiX : 96;
        double dpiY = image.DpiY > 0 ? image.DpiY : 96;
        return BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, dpiX, dpiY, PixelFormats.Bgr32, null, pixels, stride);
    }
    #endregion

    #region Office
    /// <summary>
    /// Görünmez Word: PDF'yi açınca Word kendisi dönüştürür (düzen çoğunlukla korunur, karmaşık sayfada kayabilir).
    /// Uyarılar ve makrolar kapalı. Parolalı belge sorulmaz: sahte parola hatayla döner. Word kullanıcının açık
    /// Word'ü çıkarsa (belge açıksa) yalnızca bizim belge kapanır.
    /// </summary>
    private static string ConvertWithWord(string file, ConversionTarget target)
    {
        dynamic word = Office("Word.Application");
        bool ours = word.Documents.Count == 0 && !word.Visible;
        dynamic? document = null;
        try
        {
            word.DisplayAlerts = NoAlerts;
            word.AutomationSecurity = MacrosOff;
            document = word.Documents.Open(FileName: file, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: "\u0001", Visible: false, NoEncodingDialog: true);
            string output = FreePath(file, target.Extension);
            document.SaveAs2(FileName: output, FileFormat: target.Extension == ".pdf" ? WordPdf : WordDocx, AddToRecentFiles: false);
            return output;
        }
        finally
        {
            document?.Close(SaveChanges: DontSave);
            if (ours)
            {
                word.Quit(SaveChanges: DontSave);
            }
        }
    }

    private static string ConvertWithExcel(string file)
    {
        dynamic excel = Office("Excel.Application");
        bool ours = excel.Workbooks.Count == 0 && !excel.Visible;
        dynamic? workbook = null;
        try
        {
            excel.DisplayAlerts = false;
            excel.AutomationSecurity = MacrosOff;
            workbook = excel.Workbooks.Open(Filename: file, UpdateLinks: 0, ReadOnly: true, Password: "\u0001",
                IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false);
            string output = FreePath(file, ".pdf");
            workbook.ExportAsFixedFormat(Type: ExcelPdf, Filename: output);
            return output;
        }
        finally
        {
            workbook?.Close(SaveChanges: false);
            if (ours)
            {
                excel.Quit();
            }
        }
    }

    /// <summary>PowerPoint tek kopya çalışır: açıksa onun içinde, pencere açmadan.</summary>
    private static string ConvertWithPowerPoint(string file)
    {
        dynamic powerPoint = Office("PowerPoint.Application");
        bool ours = powerPoint.Presentations.Count == 0 && powerPoint.Visible == 0;
        dynamic? presentation = null;
        try
        {
            powerPoint.DisplayAlerts = PowerPointNoAlerts;
            powerPoint.AutomationSecurity = MacrosOff;
            presentation = powerPoint.Presentations.Open(FileName: file, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            string output = FreePath(file, ".pdf");
            presentation.SaveAs(FileName: output, FileFormat: PowerPointPdf);
            return output;
        }
        finally
        {
            presentation?.Close();
            if (ours)
            {
                powerPoint.Quit();
            }
        }
    }

    private static dynamic Office(string programId)
    {
        Type type = Type.GetTypeFromProgID(programId) ?? throw new ConversionException("Office bulunamadı");
        return Activator.CreateInstance(type) ?? throw new ConversionException("Office açılamadı");
    }
    #endregion

    #region PDF pages
    /// <summary>Tek sayfa "ad.png"; çok sayfa "ad" klasöründe "Sayfa 1.png" ... (kullanıcının seçtiği hedef: her sayfa).</summary>
    private static async Task<string> RenderPdfPagesAsync(string file, IProgress<double> progress, CancellationToken token)
    {
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(file));
        uint count = pdf.PageCount;
        string output = count == 1 ? FreePath(file, ".png") : FreePath(file, "");
        StorageFolder folder;
        if (count == 1)
        {
            folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        }
        else
        {
            Directory.CreateDirectory(output);
            folder = await StorageFolder.GetFolderFromPathAsync(output);
        }

        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using PdfPage page = pdf.GetPage(i);
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Round(page.Size.Width * PdfScale) };
            string name = count == 1 ? Path.GetFileName(output) : $"Sayfa {i + 1}.png";
            StorageFile image = await folder.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
            using (var stream = await image.OpenAsync(FileAccessMode.ReadWrite))
            {
                await page.RenderToStreamAsync(stream, options);
            }
            progress.Report((i + 1) / (double)count);
        }
        return output;
    }
    #endregion

    #region Media
    /// <summary>Videodan ses hedefi seçilirse görüntü atılır. Donanım hızlandırma açık.</summary>
    private static async Task<string> TranscodeAsync(string file, ConversionTarget target, IProgress<double> progress, CancellationToken token)
    {
        MediaEncodingProfile profile = target.Extension switch
        {
            ".mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            ".m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
            ".wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
            ".wma" => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High),
            ".wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
            _ => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto)
        };

        StorageFile source = await StorageFile.GetFileFromPathAsync(file);
        string output = FreePath(file, target.Extension);
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        StorageFile destination = await folder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.FailIfExists);
        try
        {
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
            if (!prepared.CanTranscode)
            {
                throw new ConversionException(prepared.FailureReason == TranscodeFailureReason.CodecNotFound ? "Windows'ta kodlayıcısı yok" : "biçim okunamadı");
            }

            var operation = prepared.TranscodeAsync();
            operation.Progress = (_, percent) => progress.Report(percent / 100);
            using (token.Register(operation.Cancel))
            {
                await operation;
            }
            return output;
        }
        catch
        {
            await destination.DeleteAsync();
            throw;
        }
    }
    #endregion
}

/// <summary>Kullanıcıya gösterilecek kısa neden.</summary>
internal sealed class ConversionException(string message) : Exception(message);
