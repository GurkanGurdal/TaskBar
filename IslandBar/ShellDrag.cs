using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

/// <summary>
/// Dosyaları Gezgin'in kendi sürüklemesiyle başlatır: kabuğun veri nesnesi (dosyaların kabuk öğeleri) ve
/// <c>SHDoDragDrop</c>. WPF'in <see cref="DragDrop.DoDragDrop"/>'u sürükleme görüntüsü vermiyordu; içeri sürüklerken
/// görünen ikonlar dışarı sürüklerken yoktu (kullanıcı gördü). Kabuk görüntüyü (ikonlar, sayı) kendisi çizer.
/// </summary>
internal static class ShellDrag
{
    private const int Dropped = 0x00040100;
    private const int Canceled = 0x00040101;

    private static readonly Guid DataObjectHandler = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid DataObjectId = new("0000010E-0000-0000-C000-000000000046");

    /// <summary>Sürükleme bitene dek döner; yapılan efekt. Kabuk nesnesi kurulamazsa WPF'in sürüklemesine düşer.</summary>
    /// <param name="picture">İmlecin yanında gidecek görüntü (kullanıcının seçtiği B · Yelpaze); null ise kabuğunki.</param>
    /// <param name="hotspot">İmlecin görüntüdeki yeri, piksel.</param>
    public static DragDropEffects DoDragDrop(DependencyObject source, IntPtr window, string[] paths, DragDropEffects allowed,
        BitmapSource? picture = null, Point hotspot = default)
    {
        IntPtr dataObject = CreateDataObject(paths);
        if (dataObject == IntPtr.Zero)
        {
            return DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, paths), allowed);
        }

        try
        {
            if (picture != null)
            {
                SetPicture(dataObject, picture, hotspot);
            }

            int result = SHDoDragDrop(window, dataObject, IntPtr.Zero, (uint)allowed, out uint effect);
            return result == Dropped ? (DragDropEffects)effect : result == Canceled ? DragDropEffects.None : (DragDropEffects)effect;
        }
        finally
        {
            Marshal.Release(dataObject);
        }
    }

    /// <summary>
    /// Görüntü veri nesnesine yazılır (kabuk sürüklemede onu kullanır). Kabuk alfa için çarpmayı kendisi yapar: ön
    /// çarpılmamış BGRA verilir, yoksa yarı saydam yerler iki kez koyulaşır.
    /// </summary>
    private static void SetPicture(IntPtr dataObject, BitmapSource picture, Point hotspot)
    {
        var straight = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int width = straight.PixelWidth;
        int height = straight.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        straight.CopyPixels(pixels, stride, 0);

        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32
        };
        IntPtr bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            return;
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var image = new ShDragImage
        {
            Width = width,
            Height = height,
            OffsetX = (int)Math.Round(hotspot.X),
            OffsetY = (int)Math.Round(hotspot.Y),
            Bitmap = bitmap,
            ColorKey = NoColorKey
        };

        try
        {
            var helper = (IDragSourceHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(DragDropHelperClass)!)!;
            try
            {
                // Başarılıysa görüntünün sahibi kabuk.
                if (helper.InitializeFromBitmap(ref image, dataObject) == 0)
                {
                    return;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(helper);
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"ShellDrag: drag image: {e.Message}");
        }
        DeleteObject(bitmap);
    }

    private const uint NoColorKey = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShDragImage
    {
        public int Width;
        public int Height;
        public int OffsetX;
        public int OffsetY;
        public IntPtr Bitmap;
        public uint ColorKey;
    }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        [PreserveSig]
        int InitializeFromBitmap(ref ShDragImage image, IntPtr dataObject);

        [PreserveSig]
        int InitializeFromWindow(IntPtr window, IntPtr point, IntPtr dataObject);
    }

    [DllImport("gdi32")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>Dosyaların kabuk öğe dizisinden veri nesnesi; dosyalar ayrı klasörlerde olabilir.</summary>
    private static IntPtr CreateDataObject(string[] paths)
    {
        var items = new IntPtr[paths.Length];
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                items[i] = ILCreateFromPathW(paths[i]);
                if (items[i] == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
            }

            if (SHCreateShellItemArrayFromIDLists((uint)items.Length, items, out IShellItemArray array) != 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                Guid handler = DataObjectHandler;
                Guid id = DataObjectId;
                return array.BindToHandler(IntPtr.Zero, ref handler, ref id, out IntPtr data) == 0 ? data : IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        catch (COMException e)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"ShellDrag: {e.Message}");
            return IntPtr.Zero;
        }
        finally
        {
            foreach (IntPtr item in items)
            {
                if (item != IntPtr.Zero)
                {
                    ILFree(item);
                }
            }
        }
    }

    /// <summary>
    /// Bırakma yeri olan pencere için: sürükleme görüntüsünü onun üstünde de Gezgin'deki gibi çizer (görüntüyü hedef
    /// çizer; bu olmadan kutunun üstüne gelince ikonlar kayboluyordu).
    /// </summary>
    internal sealed class TargetImage(Window window)
    {
        private IDropTargetHelper? _helper;

        public void Enter(DragEventArgs e)
        {
            try
            {
                _helper ??= (IDropTargetHelper)Activator.CreateInstance(Type.GetTypeFromCLSID(DragDropHelperClass)!)!;
                GetCursorPos(out NativePoint point);
                _helper.DragEnter(new System.Windows.Interop.WindowInteropHelper(window).Handle, (System.Runtime.InteropServices.ComTypes.IDataObject)e.Data, ref point, (int)e.Effects);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _helper = null;
            }
        }

        public void Over(DragEventArgs e)
        {
            GetCursorPos(out NativePoint point);
            Try(() => _helper?.DragOver(ref point, (int)e.Effects));
        }

        public void Leave()
        {
            Try(() => _helper?.DragLeave());
        }

        public void Drop(DragEventArgs e)
        {
            GetCursorPos(out NativePoint point);
            Try(() => _helper?.Drop((System.Runtime.InteropServices.ComTypes.IDataObject)e.Data, ref point, (int)e.Effects));
        }

        private void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _helper = null;
            }
        }
    }

    private static readonly Guid DragDropHelperClass = new("4657278A-411B-11D2-839A-00C04FD918D0");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr window, System.Runtime.InteropServices.ComTypes.IDataObject data, ref NativePoint point, int effect);
        void DragLeave();
        void DragOver(ref NativePoint point, int effect);
        void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, ref NativePoint point, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [DllImport("user32")]
    private static extern bool GetCursorPos(out NativePoint point);

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr context, ref Guid handler, ref Guid id, out IntPtr result);
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32")]
    private static extern void ILFree(IntPtr item);

    [DllImport("shell32")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] items, out IShellItemArray array);

    [DllImport("shell32")]
    private static extern int SHDoDragDrop(IntPtr window, IntPtr dataObject, IntPtr dropSource, uint allowed, out uint effect);
}
