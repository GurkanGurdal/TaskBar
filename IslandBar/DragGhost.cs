using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace IslandBar;

/// <summary>
/// Sürüklenen görüntünün imleci izleyen küçük kopyası. Fareye görünmez: bırakılan yeri Windows altındaki pencereden sorar,
/// bu pencere araya girmez. Windows'un kendi sürükleme görseli WPF'in veri nesnesiyle çalışmıyor (SetData desteklenmiyor).
/// </summary>
internal sealed class DragGhost : Window
{
    private const int ExStyleIndex = -20;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private const uint NoSize = 0x0001;
    private const uint NoActivate = 0x0010;
    private const double ShadowMargin = 16;
    private const double CursorGap = 8;
    private const double CornerRadiusLength = 10;
    private static readonly IntPtr TopMost = new(-1);

    private IntPtr _handle;

    public DragGhost(ImageSource image, double width, double height)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var scale = new ScaleTransform(0.8, 0.8);
        Content = new Border
        {
            Margin = new Thickness(ShadowMargin),
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(CornerRadiusLength),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.5 },
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = scale
        };

        var grow = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(_handle, ExStyleIndex, GetWindowLong(_handle, ExStyleIndex) | ExTransparent | ExToolWindow | ExNoActivate);
        FollowCursor();
    }

    public void FollowCursor()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        int offset = (int)Math.Round((CursorGap - ShadowMargin) * scale);
        SetWindowPos(_handle, TopMost, cursor.X + offset, cursor.Y + offset, 0, 0, NoSize | NoActivate);
    }

    [DllImport("user32")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
