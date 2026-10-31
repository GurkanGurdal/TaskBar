using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GrupBar;

/// <summary>
/// Bir uygulamanın, dosyanın ya da klasörün kendi ikonu; Windows'tan istenir.
/// "Yalnızca ikon" istenmezse Store uygulamalarında Windows küçük bir küçük resim verir,
/// bu da Spotify gibi uygulamalarda beyaz yüksek kontrast varyantı oluyor.
/// </summary>
internal static class AppIcon
{
    private const int PixelSize = 64;
    private const byte VisibleAlpha = 96;
    private const int BiggerSizeOk = 0x1;
    private const int IconOnly = 0x4;
    private const uint StockApplication = 2;
    private const uint StockIcon = 0x100;
    private const uint StockLargeIcon = 0;
    private const uint FileInfoIcon = 0x100;
    private const uint FileInfoLargeIcon = 0;

    private static readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource?> Result)> Requests = new();
    private static readonly Thread Worker = StartWorker();

    /// <summary>Dosya bulunamazsa genel uygulama ikonu döner.</summary>
    public static Task<ImageSource?> LoadAsync(string path)
    {
        var result = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add((path, result));
        return result.Task;
    }

    /// <summary>
    /// Kabuk çağrıları başlatılmış COM ister. Havuz iş parçacığında bu garanti değil:
    /// açılışta ilk yüklemeler sessizce boş dönüyordu.
    /// </summary>
    private static Thread StartWorker()
    {
        var worker = new Thread(() =>
        {
            foreach ((string path, TaskCompletionSource<ImageSource?> result) in Requests.GetConsumingEnumerable())
            {
                try
                {
                    result.SetResult(Load(path));
                }
                catch (Exception e) when (e is COMException or ArgumentException)
                {
                    result.SetResult(null);
                }
            }
        })
        {
            IsBackground = true,
            Name = "AppIcon"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return worker;
    }

    private static ImageSource? Load(string path)
    {
        // Klasörde "yalnızca ikon" isteği çoğu zaman boş sayfa döndürüyor.
        BitmapSource? icon = (Directory.Exists(path) ? FromFileInfo(path) : FromShell(path)) ?? Generic();
        return icon == null ? null : Trim(icon);
    }

    /// <summary>
    /// Her uygulama ikonunun kenarında farklı boşluk bırakıyor, yan yana bazıları küçük kalıyor.
    /// Boşluk kırpılınca görünür kısım yuvayı doldurur.
    /// </summary>
    private static BitmapSource Trim(BitmapSource icon)
    {
        var converted = new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        int left = width;
        int right = -1;
        int top = height;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] >= VisibleAlpha)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0 || (right - left + 1 == width && bottom - top + 1 == height))
        {
            return icon;
        }

        var trimmed = new CroppedBitmap(icon, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        trimmed.Freeze();
        return trimmed;
    }

    private static BitmapSource? FromFileInfo(string path)
    {
        var info = new FileInfoResult();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfoResult>(), FileInfoIcon | FileInfoLargeIcon) == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }
        return FromIcon(info.Icon);
    }

    private static BitmapSource? FromShell(string path)
    {
        Guid iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory? factory) != 0 || factory == null)
        {
            return null;
        }

        if (factory.GetImage(new Size32 { Width = PixelSize, Height = PixelSize }, BiggerSizeOk | IconOnly, out IntPtr bitmap) != 0)
        {
            return null;
        }

        try
        {
            BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DeleteObject(bitmap);
        }
    }

    private static BitmapSource? Generic()
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        return SHGetStockIconInfo(StockApplication, StockIcon | StockLargeIcon, ref info) == 0 ? FromIcon(info.Icon) : null;
    }

    private static BitmapSource FromIcon(IntPtr icon)
    {
        try
        {
            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory? item);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref FileInfoResult info, uint size, uint flags);

    [DllImport("gdi32")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Size32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoResult
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr Icon;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Path;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size32 size, int flags, out IntPtr bitmap);
    }
}
