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
    Media,

    /// <summary>Her resim kendi PDF'ine, tek sayfa.</summary>
    ImagePdf,

    /// <summary>Bütün resimler tek PDF'te, sırayla birer sayfa.</summary>
    ImagesPdf,

    /// <summary>Office belgesi önce PDF'e, sonra her sayfası (slaytı) bir PNG'ye.</summary>
    DocumentPages
}

/// <param name="Label">Menüde.</param>
/// <param name="Short">Çubuktaki kartta, "rapor.pdf → Word".</param>
/// <param name="Format">Office'in kayıt biçimi kodu (Word, Excel, PowerPoint hedeflerinde).</param>
internal sealed record ConversionTarget(string Label, string Short, string Extension, ConversionKind Kind, int Format = 0);

/// <summary>
/// Dosya dönüştürmeler, kurulu bir şey gerektirmeden (kullanıcının seçtiği dört tür): resimler Windows'un kodlayıcılarıyla
/// (WIC; HEIC, WebP, RAW yalnızca okunur), belgeler kullanıcının Office'iyle (görünmez açılır, kendi açtığımız kapanır),
/// PDF sayfaları Windows'un PDF okuyucusuyla, ses ve video Windows'un dönüştürücüsüyle (Media Foundation), resimden PDF
/// kendi yazdığımız küçük PDF'le (<see cref="ImagePdf"/>). Sonuç verilen klasöre, aynı adla yazılır (varsa "ad (2)");
/// özgün dosyaya dokunulmaz.
/// </summary>
internal static class FileConversions
{
    private const int JpegQuality = 92;

    /// <summary>PDF sayfası 96 DPI ölçüsünün iki katı (192 DPI): ekranda keskin, dosya makul.</summary>
    private const double PdfScale = 2;

    // Office sabitleri: Word wdSaveFormat, Excel xlFileFormat, PowerPoint ppSaveAsFileType.
    private const int WordText = 2;
    private const int WordRtf = 6;
    private const int WordDocx = 12;
    private const int WordPdf = 17;
    private const int WordOdt = 23;
    private const int ExcelPdf = 0;
    private const int ExcelXlsx = 51;
    private const int ExcelOds = 60;
    private const int ExcelCsvUtf8 = 62;
    private const int PowerPointPptx = 24;
    private const int PowerPointPdf = 32;
    private const int PowerPointOdp = 35;
    private const int Utf8 = 65001;
    private const int NoAlerts = 0;
    private const int PowerPointNoAlerts = 1;
    private const int MacrosOff = 3;
    private const int DontSave = 0;

    private static readonly ConversionTarget Png = new("PNG resmi", "PNG", ".png", ConversionKind.Image);
    private static readonly ConversionTarget Jpeg = new("JPEG resmi", "JPEG", ".jpg", ConversionKind.Image);
    private static readonly ConversionTarget Bmp = new("BMP resmi", "BMP", ".bmp", ConversionKind.Image);
    private static readonly ConversionTarget Tiff = new("TIFF resmi", "TIFF", ".tif", ConversionKind.Image);
    private static readonly ConversionTarget Gif = new("GIF resmi", "GIF", ".gif", ConversionKind.Image);
    private static readonly ConversionTarget Ico = new("Simge (.ico)", "ICO", ".ico", ConversionKind.Image);
    private static readonly ConversionTarget ImageToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.ImagePdf);
    private static readonly ConversionTarget ImagesToOnePdf = new("Tek PDF (hepsi bir arada)", "PDF", ".pdf", ConversionKind.ImagesPdf);
    private static readonly ConversionTarget WordFromPdf = new("Word belgesi (.docx)", "Word", ".docx", ConversionKind.Word, WordDocx);
    private static readonly ConversionTarget PdfPages = new("PNG resimleri (her sayfa)", "PNG", ".png", ConversionKind.PdfPages);
    private static readonly ConversionTarget WordToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.Word, WordPdf);
    private static readonly ConversionTarget WordToDocx = new("Word belgesi (.docx)", "Word", ".docx", ConversionKind.Word, WordDocx);
    private static readonly ConversionTarget WordToRtf = new("Zengin metin (.rtf)", "RTF", ".rtf", ConversionKind.Word, WordRtf);
    private static readonly ConversionTarget WordToOdt = new("OpenDocument metni (.odt)", "ODT", ".odt", ConversionKind.Word, WordOdt);
    private static readonly ConversionTarget WordToText = new("Düz metin (.txt)", "TXT", ".txt", ConversionKind.Word, WordText);
    private static readonly ConversionTarget DocumentPages = new("PNG resimleri (her sayfa)", "PNG", ".png", ConversionKind.DocumentPages);
    private static readonly ConversionTarget SlidePages = new("PNG resimleri (her slayt)", "PNG", ".png", ConversionKind.DocumentPages);
    private static readonly ConversionTarget ExcelToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.Excel, ExcelPdf);
    private static readonly ConversionTarget ExcelToXlsx = new("Excel çalışma kitabı (.xlsx)", "Excel", ".xlsx", ConversionKind.Excel, ExcelXlsx);
    private static readonly ConversionTarget ExcelToCsv = new("CSV (ilk sayfa)", "CSV", ".csv", ConversionKind.Excel, ExcelCsvUtf8);
    private static readonly ConversionTarget ExcelToOds = new("OpenDocument tablosu (.ods)", "ODS", ".ods", ConversionKind.Excel, ExcelOds);
    private static readonly ConversionTarget PowerPointToPdf = new("PDF belgesi", "PDF", ".pdf", ConversionKind.PowerPoint, PowerPointPdf);
    private static readonly ConversionTarget PowerPointToPptx = new("PowerPoint sunusu (.pptx)", "PowerPoint", ".pptx", ConversionKind.PowerPoint, PowerPointPptx);
    private static readonly ConversionTarget PowerPointToOdp = new("OpenDocument sunusu (.odp)", "ODP", ".odp", ConversionKind.PowerPoint, PowerPointOdp);
    private static readonly ConversionTarget Mp3 = new("MP3 ses", "MP3", ".mp3", ConversionKind.Media);
    private static readonly ConversionTarget M4a = new("M4A ses", "M4A", ".m4a", ConversionKind.Media);
    private static readonly ConversionTarget Wav = new("WAV ses", "WAV", ".wav", ConversionKind.Media);
    private static readonly ConversionTarget Wma = new("WMA ses", "WMA", ".wma", ConversionKind.Media);
    private static readonly ConversionTarget Flac = new("FLAC ses (kayıpsız)", "FLAC", ".flac", ConversionKind.Media);
    private static readonly ConversionTarget Mp4 = new("MP4 video", "MP4", ".mp4", ConversionKind.Media);
    private static readonly ConversionTarget Wmv = new("WMV video", "WMV", ".wmv", ConversionKind.Media);

    private static readonly ConversionTarget[] ImageTargets = [Png, Jpeg, Bmp, Tiff, Gif, Ico, ImageToPdf];
    private static readonly ConversionTarget[] AudioTargets = [Mp3, M4a, Wav, Wma, Flac];
    private static readonly ConversionTarget[] VideoTargets = [Mp4, Wmv, Mp3, M4a];

    private static readonly string[] WordExtensions = [".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt", ".htm", ".html"];
    private static readonly string[] ExcelExtensions = [".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv"];
    private static readonly string[] PowerPointExtensions = [".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp"];

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
        Add([WordFromPdf, PdfPages, WordToText], ".pdf");
        Add([WordToPdf, WordToDocx, WordToRtf, WordToOdt, WordToText, DocumentPages], WordExtensions);
        Add([ExcelToPdf, ExcelToXlsx, ExcelToCsv, ExcelToOds, DocumentPages], ExcelExtensions);
        Add([PowerPointToPdf, PowerPointToPptx, PowerPointToOdp, SlidePages], PowerPointExtensions);
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

        List<ConversionTarget> targets = TargetsByExtension[Path.GetExtension(files[0])]
            .Where(target => files.All(file => TargetsByExtension[Path.GetExtension(file)].Contains(target)))
            .Where(target => files.Any(file => !IsAlready(file, target)))
            .ToList();

        // Birden çok resim: ayrı ayrı PDF'in yanında hepsi bir arada da.
        int single = targets.IndexOf(ImageToPdf);
        if (files.Count > 1 && single >= 0)
        {
            targets.Insert(single + 1, ImagesToOnePdf);
        }
        return targets;
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
    public static Task<string> ConvertAsync(string file, ConversionTarget target, string folder, IProgress<double> progress, CancellationToken token)
    {
        return target.Kind switch
        {
            ConversionKind.Image => OnSta(() => ConvertImage(file, folder, target)),
            ConversionKind.Word => OnSta(() => ConvertWithWord(file, folder, target)),
            ConversionKind.Excel => OnSta(() => ConvertWithExcel(file, folder, target)),
            ConversionKind.PowerPoint => OnSta(() => ConvertWithPowerPoint(file, folder, target)),
            ConversionKind.PdfPages => RenderPdfPagesAsync(file, folder, progress, token),
            ConversionKind.DocumentPages => RenderDocumentPagesAsync(file, folder, progress, token),
            ConversionKind.ImagePdf or ConversionKind.ImagesPdf => CombineImagesAsync([file], folder, progress),
            _ => TranscodeAsync(file, folder, target, progress, token)
        };
    }

    /// <summary>Resimler tek PDF'te, verilen sırayla birer sayfa; tek resimde dosyanın adıyla, çokta "Resimler.pdf".</summary>
    /// <param name="progress">0..1, her resimden sonra.</param>
    public static Task<string> CombineImagesAsync(IReadOnlyList<string> files, string folder, IProgress<double> progress)
    {
        string output = FreePath(files.Count == 1 ? files[0] : "Resimler", ".pdf", folder);
        return OnSta(() =>
        {
            ImagePdf.Write(output, files.Select(file => new JpegBitmapEncoder { QualityLevel = JpegQuality, Frames = { BitmapFrame.Create(OnWhite(LoadImage(file))) } }), progress, files.Count);
            return output;
        });
    }

    /// <summary>Verilen klasörde, özgün dosyanın adıyla; varsa "ad (2)". Uzantı boşsa klasör adı.</summary>
    public static string FreePath(string file, string extension, string folder)
    {
        string directory = folder;
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
    private static string ConvertImage(string file, string folder, ConversionTarget target)
    {
        BitmapSource image = LoadImage(file);
        if (target.Extension == ".ico")
        {
            string icon = FreePath(file, ".ico", folder);
            IconFile.Write(icon, image);
            return icon;
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
        string output = FreePath(file, target.Extension, folder);
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

    /// <summary>En büyük kare, yönü düzeltilmiş.</summary>
    private static BitmapSource LoadImage(string file)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames.MaxBy(candidate => candidate.PixelWidth * candidate.PixelHeight)!;
        return Oriented(frame, Orientation(frame));
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
    private static string ConvertWithWord(string file, string folder, ConversionTarget target)
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
            string output = FreePath(file, target.Extension, folder);
            if (target.Format == WordText)
            {
                // Düz metin UTF-8: Türkçe karakterler her yerde doğru açılsın.
                document.SaveAs2(FileName: output, FileFormat: WordText, AddToRecentFiles: false, Encoding: Utf8);
            }
            else
            {
                document.SaveAs2(FileName: output, FileFormat: target.Format, AddToRecentFiles: false);
            }
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

    /// <summary>PDF bütün sayfalar; CSV yalnızca ilk sayfa (biçim tek sayfa tutar), UTF-8.</summary>
    private static string ConvertWithExcel(string file, string folder, ConversionTarget target)
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
            string output = FreePath(file, target.Extension, folder);
            if (target.Format == ExcelPdf)
            {
                workbook.ExportAsFixedFormat(Type: ExcelPdf, Filename: output);
            }
            else
            {
                if (target.Format == ExcelCsvUtf8)
                {
                    workbook.Worksheets.Item(1).Activate();
                }
                workbook.SaveAs(Filename: output, FileFormat: target.Format, AddToMru: false);
            }
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
    private static string ConvertWithPowerPoint(string file, string folder, ConversionTarget target)
    {
        dynamic powerPoint = Office("PowerPoint.Application");
        bool ours = powerPoint.Presentations.Count == 0 && powerPoint.Visible == 0;
        dynamic? presentation = null;
        try
        {
            powerPoint.DisplayAlerts = PowerPointNoAlerts;
            powerPoint.AutomationSecurity = MacrosOff;
            presentation = powerPoint.Presentations.Open(FileName: file, ReadOnly: -1, Untitled: 0, WithWindow: 0);
            string output = FreePath(file, target.Extension, folder);
            presentation.SaveAs(FileName: output, FileFormat: target.Format);
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

    /// <summary>
    /// Office belgesinin sayfaları (slaytları) PNG: belge önce geçici bir PDF'e, sonra PDF sayfaları gibi. Adlandırma
    /// PDF sayfalarıyla aynı ("Sayfa 1.png"). Geçici PDF iş bitince silinir.
    /// </summary>
    private static async Task<string> RenderDocumentPagesAsync(string file, string folder, IProgress<double> progress, CancellationToken token)
    {
        string temporary = Path.Combine(folder, ".pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string extension = Path.GetExtension(file);
            string pdf = await OnSta(() =>
                ExcelExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? ConvertWithExcel(file, temporary, ExcelToPdf)
                : PowerPointExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? ConvertWithPowerPoint(file, temporary, PowerPointToPdf)
                : ConvertWithWord(file, temporary, WordToPdf));
            return await RenderPdfPagesAsync(pdf, folder, progress, token, nameFrom: file);
        }
        finally
        {
            try
            {
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // PDF okuyucusu dosyayı bırakmadıysa geçici klasör bekleme yerinde kalır; kutuda görünmez.
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
    /// <param name="nameFrom">Çıktının adını aldığı dosya (Office belgesinin geçici PDF'inde asıl belge).</param>
    private static async Task<string> RenderPdfPagesAsync(string file, string folder, IProgress<double> progress, CancellationToken token, string? nameFrom = null)
    {
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(file));
        uint count = pdf.PageCount;
        string output = count == 1 ? FreePath(nameFrom ?? file, ".png", folder) : FreePath(nameFrom ?? file, "", folder);
        StorageFolder pages;
        if (count == 1)
        {
            pages = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        }
        else
        {
            Directory.CreateDirectory(output);
            pages = await StorageFolder.GetFolderFromPathAsync(output);
        }

        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using PdfPage page = pdf.GetPage(i);
            var options = new PdfPageRenderOptions { DestinationWidth = (uint)Math.Round(page.Size.Width * PdfScale) };
            string name = count == 1 ? Path.GetFileName(output) : $"Sayfa {i + 1}.png";
            StorageFile image = await pages.CreateFileAsync(name, CreationCollisionOption.FailIfExists);
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
    private static async Task<string> TranscodeAsync(string file, string folder, ConversionTarget target, IProgress<double> progress, CancellationToken token)
    {
        MediaEncodingProfile profile = target.Extension switch
        {
            ".mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            ".m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
            ".wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
            ".wma" => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High),
            ".flac" => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
            ".wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
            _ => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto)
        };

        StorageFile source = await StorageFile.GetFileFromPathAsync(file);
        string output = FreePath(file, target.Extension, folder);
        StorageFolder outputFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(output)!);
        StorageFile destination = await outputFolder.CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.FailIfExists);
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
