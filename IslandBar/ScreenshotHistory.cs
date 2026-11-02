using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.Common.Logging;

namespace IslandBar;

/// <summary>Geçmişteki bir ekran görüntüsü. Dosya bizim kopyamız: kullanıcının Resimler klasörüne dokunulmaz.</summary>
public sealed class ScreenshotItem(string path, BitmapSource thumbnail, DateTime takenAt, string? hash)
{
    public string Path { get; } = path;

    public BitmapSource Thumbnail { get; } = thumbnail;

    public DateTime TakenAt { get; } = takenAt;

    public string TimeLabel => TakenAt.ToString("g");

    internal string? Hash { get; } = hash;
}

/// <summary>
/// Son ekran görüntüleri. Windows'ta tek bir "görüntü alındı" olayı yok, iki yoldan izlenir: Win+PrtScn yalnızca Ekran Görüntüleri
/// klasörüne yazar, Win+Shift+S panoya da yazar, otomatik kaydetme kapalıysa yalnızca panoya. İkisi aynı görüntüyü getirince pikselleri
/// karşılaştırılıp tek sayılır. Panoya kopyalanan her resim alınmaz: yalnızca ekran görüntüsü araçlarından gelen ya da sahipsiz olan.
/// </summary>
public sealed class ScreenshotHistory
{
    public const int MaxItems = 10;

    private const int WmClipboardUpdate = 0x031D;
    private const int ClipboardSettleMs = 150;
    private const int ClipboardAttempts = 5;
    private const int ClipboardRetryMs = 60;
    private const int FileAttempts = 20;
    private const int FileRetryMs = 100;
    private const int ThumbnailWidth = 420;
    private const uint CreateKnownFolder = 0x8000;
    private const uint ClipboardBitmap = 2;
    private const uint ClipboardDib = 8;

    private static readonly Guid ScreenshotsFolderId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    private static readonly HashSet<string> ScreenshotTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool", "ScreenClippingHost", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "PicPick", "SnagitCapture", "Snagit32", "SnagitEditor", "flameshot"
    };

    private static readonly string StoreFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandBar", "Screenshots");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(ClipboardSettleMs) };
    private HwndSource? _clipboardWindow;
    private FileSystemWatcher? _folderWatcher;
    private bool _toolWroteClipboard;
    private bool _appWroteClipboard;
    private bool _isLoaded;

    private ScreenshotHistory()
    {
        _clipboardTimer.Tick += ClipboardTimer_Tick;
    }

    public static ScreenshotHistory Shared { get; } = new();

    public ObservableCollection<ScreenshotItem> Items { get; } = new();

    /// <summary>İzlemeyi açar ya da kapatır. Kapalıyken eskiler durur, yenisi eklenmez.</summary>
    public async void SetWatching(bool watching)
    {
        try
        {
            if (!watching)
            {
                StopWatching();
            }
            else if (_clipboardWindow == null)
            {
                StartClipboardWatch();
                StartFolderWatch();
            }

            if (!_isLoaded)
            {
                _isLoaded = true;
                await LoadAsync();
            }
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    public void StopWatching()
    {
        _clipboardTimer.Stop();
        if (_clipboardWindow != null)
        {
            RemoveClipboardFormatListener(_clipboardWindow.Handle);
            _clipboardWindow.Dispose();
            _clipboardWindow = null;
        }

        _folderWatcher?.Dispose();
        _folderWatcher = null;
    }

    public void Remove(ScreenshotItem item)
    {
        Items.Remove(item);
        DeleteFile(item.Path);
    }

    public void Clear()
    {
        foreach (ScreenshotItem item in Items.ToList())
        {
            Remove(item);
        }
    }

    public void CopyToClipboard(ScreenshotItem item)
    {
        try
        {
            using FileStream stream = File.OpenRead(item.Path);
            BitmapFrame image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Clipboard.SetImage(image);
        }
        catch (Exception e) when (e is IOException or COMException or UnauthorizedAccessException or NotSupportedException)
        {
            ShellLogger.Error($"ScreenshotHistory: Could not copy: {e.Message}");
        }
    }

    #region Clipboard
    private void StartClipboardWatch()
    {
        // Yalnızca mesaj alan pencere; ekranda hiç yok.
        const int messageOnly = -3;
        _clipboardWindow = new HwndSource(new HwndSourceParameters("IslandBar.Clipboard") { ParentWindow = messageOnly, WindowStyle = 0 });
        _clipboardWindow.AddHook(ClipboardWindow_Message);
        AddClipboardFormatListener(_clipboardWindow.Handle);
    }

    private IntPtr ClipboardWindow_Message(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // Sahibi burada okunur: araç birkaç milisaniye sonra panoyu bırakıyor, sonraki bildirimde sahip boş görünüyor.
            string? owner = ClipboardOwnerName();
            _toolWroteClipboard |= owner != null && ScreenshotTools.Contains(owner);
            _appWroteClipboard |= owner != null && !ScreenshotTools.Contains(owner);
            _clipboardTimer.Stop();
            _clipboardTimer.Start();
        }
        return IntPtr.Zero;
    }

    private async void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        _clipboardTimer.Stop();

        // Sahipsiz: Windows'un kendi PrtScn'si (ekran alıntısı ayarı kapalıyken).
        bool isScreenshot = _toolWroteClipboard || !_appWroteClipboard;
        _toolWroteClipboard = false;
        _appWroteClipboard = false;
        if (!isScreenshot || !IsClipboardFormatAvailable(ClipboardDib) && !IsClipboardFormatAvailable(ClipboardBitmap))
        {
            return;
        }

        // Pano o an başkasında olabilir.
        for (int attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                if (IsMarkedPrivate())
                {
                    return;
                }

                if (ReadClipboardImage() is { } image)
                {
                    await AddAsync(image, DateTime.Now);
                }
                return;
            }
            catch (COMException)
            {
                await Task.Delay(ClipboardRetryMs);
            }
            catch (Exception error)
            {
                Report(error);
                return;
            }
        }
    }

    /// <summary>Parola yöneticilerinin kullandığı işaretler; uygulama içeriği izleyicilerden saklamak istemiş.</summary>
    internal static bool IsMarkedPrivate()
    {
        return Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")
            || Clipboard.ContainsData("Clipboard Viewer Ignore")
            || (Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream { Length: >= 4 } allowed && new BinaryReader(allowed).ReadInt32() == 0);
    }

    private static BitmapSource? ReadClipboardImage()
    {
        // PNG varsa o: Clipboard.GetImage saydamlığı bozabiliyor.
        BitmapSource? image = Clipboard.GetData("PNG") is MemoryStream png
            ? BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
            : Clipboard.GetImage();
        return image == null ? null : Normalize(image);
    }

    private static string? ClipboardOwnerName()
    {
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(owner, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
    #endregion

    #region Screenshots folder
    private void StartFolderWatch()
    {
        try
        {
            _folderWatcher = new FileSystemWatcher(ScreenshotsFolder()) { NotifyFilter = NotifyFilters.FileName };
            _folderWatcher.Created += (_, e) => OnFolderFile(e.FullPath);
            _folderWatcher.Renamed += (_, e) => OnFolderFile(e.FullPath);
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            // Klasör izlenemezse pano yolu çalışmaya devam eder.
            ShellLogger.Error($"ScreenshotHistory: Could not watch folder: {e.Message}");
        }
    }

    /// <summary>OneDrive'a yönlendirilmiş olabilir; yolu Windows söyler.</summary>
    private static string ScreenshotsFolder()
    {
        Guid id = ScreenshotsFolderId;
        if (SHGetKnownFolderPath(ref id, CreateKnownFolder, IntPtr.Zero, out IntPtr pointer) == 0)
        {
            string path = Marshal.PtrToStringUni(pointer)!;
            Marshal.FreeCoTaskMem(pointer);
            return path;
        }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    private void OnFolderFile(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current?.Dispatcher.BeginInvoke(async () => await AddFileAsync(path));
        }
    }

    private async Task AddFileAsync(string path)
    {
        // Windows dosyayı yazmayı bitirene dek açılamaz ya da yarım okunur; yarım dosyanın hatası türüne göre seçilemiyor.
        Exception? last = null;
        for (int attempt = 0; attempt < FileAttempts; attempt++)
        {
            try
            {
                BitmapSource image = await Task.Run(() =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Normalize(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
                });
                await AddAsync(image, DateTime.Now);
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(FileRetryMs);
            }
        }

        if (last is not null and not FileNotFoundException)
        {
            Report(last);
        }
    }
    #endregion

    /// <summary>Pano ve dosya aynı görüntüyü farklı biçimlerde getirir; karşılaştırılabilsin diye tek biçime iner.</summary>
    private static BitmapSource Normalize(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        converted.Freeze();
        return converted;
    }

    private async Task AddAsync(BitmapSource image, DateTime takenAt)
    {
        // Pano ve klasör neredeyse aynı anda gelir; biri bitmeden öbürü karşılaştırılmasın.
        await _gate.WaitAsync();
        try
        {
            (byte[] pixels, string hash) = await Task.Run(() => ReadPixels(image));
            if (Items.Any(item => item.Hash == hash))
            {
                return;
            }

            ScreenshotItem item = await Task.Run(() => Store(image, pixels, hash, takenAt));
            Items.Insert(0, item);
            while (Items.Count > MaxItems)
            {
                Remove(Items[^1]);
            }
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (byte[] Pixels, string Hash) ReadPixels(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        // Bgr32'nin dördüncü baytı kullanılmıyor, kaynağa göre değişebilir.
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
        }
        return (pixels, Convert.ToHexString(SHA256.HashData(pixels)));
    }

    private static ScreenshotItem Store(BitmapSource image, byte[] pixels, string hash, DateTime takenAt)
    {
        Directory.CreateDirectory(StoreFolder);

        // Bırakılan yerde dosya adı görünüyor; okunur ve benzersiz olsun.
        string name = $"Screenshot {takenAt:yyyy-MM-dd HH.mm.ss}";
        string path = System.IO.Path.Combine(StoreFolder, name + ".png");
        for (int copy = 2; File.Exists(path); copy++)
        {
            path = System.IO.Path.Combine(StoreFolder, $"{name} ({copy}).png");
        }

        BitmapSource source = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, image.PixelWidth * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (FileStream stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return new ScreenshotItem(path, LoadThumbnail(path), takenAt, hash);
    }

    private async Task LoadAsync()
    {
        List<ScreenshotItem> items = await Task.Run(() =>
        {
            var loaded = new List<ScreenshotItem>();
            if (!Directory.Exists(StoreFolder))
            {
                return loaded;
            }

            IEnumerable<FileInfo> files = new DirectoryInfo(StoreFolder).GetFiles("*.png").OrderByDescending(file => file.LastWriteTime);
            foreach (FileInfo file in files)
            {
                try
                {
                    if (loaded.Count >= MaxItems)
                    {
                        file.Delete();
                        continue;
                    }

                    loaded.Add(new ScreenshotItem(file.FullName, LoadThumbnail(file.FullName), file.LastWriteTime, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
                {
                    ShellLogger.Error($"ScreenshotHistory: Could not load {file.Name}: {e.Message}");
                }
            }
            return loaded;
        });

        // Yükleme sürerken yeni görüntü gelmiş olabilir; onlar önde kalır.
        foreach (ScreenshotItem item in items)
        {
            if (Items.Count < MaxItems)
            {
                Items.Add(item);
            }
        }
    }

    private static BitmapSource LoadThumbnail(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = ThumbnailWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Yan özellik: görüntü işlenemese de çubuk kapanmasın. Beklenmeyen hata uygulamayı sessizce kapatırdı.</summary>
    private static void Report(Exception e)
    {
        ShellLogger.Error($"ScreenshotHistory: {e}");
        App.WriteCrashLog(e);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"ScreenshotHistory: Could not delete {path}: {e.Message}");
        }
    }

    [DllImport("user32", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
