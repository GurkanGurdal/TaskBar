using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Bir pencerenin canlı küçük resmi. Görüntüyü Windows (DWM) doğrudan çizer: görsel ağaçta yer tutar ama
/// WPF'in saydamlık ve dönüşümleri ona işlemez, her şeyin üstünde durur. Bu yüzden kart belirdikten sonra açılır.
/// </summary>
public sealed class WindowThumbnail : FrameworkElement
{
    public static readonly DependencyProperty SourceWindowProperty = DependencyProperty.Register(
        nameof(SourceWindow), typeof(IntPtr), typeof(WindowThumbnail),
        new PropertyMetadata(IntPtr.Zero, (d, _) => ((WindowThumbnail)d).Reload()));

    /// <summary>Üst öğede açılır, alttaki tüm küçük resimlere geçer.</summary>
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(WindowThumbnail),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as WindowThumbnail)?.Sync()));

    private static readonly DependencyProperty ThumbnailOpacityProperty = DependencyProperty.Register(
        "ThumbnailOpacity", typeof(double), typeof(WindowThumbnail),
        new PropertyMetadata(0d, (d, _) => ((WindowThumbnail)d).UpdateProperties()));

    private const uint RectDestination = 0x1;
    private const uint Opacity255 = 0x4;
    private const uint Visible = 0x8;

    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _thumbnail;
    private Native.Rect _lastRect;
    private byte _lastOpacity;

    public WindowThumbnail()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Sync();
        LayoutUpdated += (_, _) => UpdateProperties();

        // Kaynak pencere yeniden boyutlanınca oran değişir; DWM bunu bildirmiyor.
        _sizeTimer.Tick += (_, _) => UpdateProperties();
    }

    public IntPtr SourceWindow
    {
        get => (IntPtr)GetValue(SourceWindowProperty);
        set => SetValue(SourceWindowProperty, value);
    }

    public static void SetIsShown(DependencyObject element, bool value)
    {
        element.SetValue(IsShownProperty, value);
    }

    private void Reload()
    {
        Unregister();
        Sync();
    }

    private void Sync()
    {
        if (IsLoaded && (bool)GetValue(IsShownProperty) && SourceWindow != IntPtr.Zero)
        {
            Register();
        }
        else
        {
            Unregister();
        }
    }

    private void Register()
    {
        if (_thumbnail != IntPtr.Zero || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        if (Native.DwmRegisterThumbnail(source.Handle, SourceWindow, out _thumbnail) != 0)
        {
            _thumbnail = IntPtr.Zero;
            return;
        }

        _lastRect = default;
        _lastOpacity = 0;
        _sizeTimer.Start();
        BeginAnimation(ThumbnailOpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
    }

    private void Unregister()
    {
        if (_thumbnail == IntPtr.Zero)
        {
            return;
        }

        _sizeTimer.Stop();
        BeginAnimation(ThumbnailOpacityProperty, null);
        Native.DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = IntPtr.Zero;
    }

    private void UpdateProperties()
    {
        if (_thumbnail == IntPtr.Zero
            || PresentationSource.FromVisual(this) is not HwndSource { RootVisual: { } root }
            || !root.IsAncestorOf(this)
            || Native.DwmQueryThumbnailSourceSize(_thumbnail, out Native.Size size) != 0
            || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // Kutuya sığdır ve ortala; oran bozulmasın, küçük pencere büyütülmesin.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Point origin = TransformToAncestor(root).Transform(new Point(0, 0));
        double boxWidth = ActualWidth * dpi.DpiScaleX;
        double boxHeight = ActualHeight * dpi.DpiScaleY;
        double scale = Math.Min(1, Math.Min(boxWidth / size.Width, boxHeight / size.Height));
        double width = size.Width * scale;
        double height = size.Height * scale;
        double left = origin.X * dpi.DpiScaleX + (boxWidth - width) / 2;
        double top = origin.Y * dpi.DpiScaleY + (boxHeight - height) / 2;

        var rect = new Native.Rect
        {
            Left = (int)Math.Round(left),
            Top = (int)Math.Round(top),
            Right = (int)Math.Round(left + width),
            Bottom = (int)Math.Round(top + height)
        };
        var opacity = (byte)Math.Round((double)GetValue(ThumbnailOpacityProperty) * 255);

        if (rect.Equals(_lastRect) && opacity == _lastOpacity)
        {
            return;
        }

        _lastRect = rect;
        _lastOpacity = opacity;

        var properties = new Native.ThumbnailProperties
        {
            Flags = RectDestination | Opacity255 | Visible,
            Destination = rect,
            Opacity = opacity,
            IsVisible = true
        };
        Native.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public record struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Size
        {
            public int Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ThumbnailProperties
        {
            public uint Flags;
            public Rect Destination;
            public Rect Source;
            public byte Opacity;
            public bool IsVisible;
            public bool SourceClientAreaOnly;
        }

        [DllImport("dwmapi")]
        public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

        [DllImport("dwmapi")]
        public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

        [DllImport("dwmapi")]
        public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

        [DllImport("dwmapi")]
        public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);
    }
}
