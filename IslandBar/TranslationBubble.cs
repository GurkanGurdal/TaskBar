using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Seçilen yazının çevirisi, seçimin hemen altında küçük cam balon (kullanıcının seçimi: A · Balon). Odak almaz:
/// yazının olduğu uygulama önde kalır. Altta dil yönü ve kopyala düğmesi; ipucu kipinde yalnızca tek satır yazı,
/// birkaç saniye sonra kendiliğinden kapanır.
/// </summary>
internal sealed class TranslationBubble : Window
{
    private const double ShadowMargin = 20;
    private const double SelectionGap = 6;
    private const double ScreenGap = 8;
    private const double MaxCardWidth = 300;
    private const int OpenMs = 160;
    private const int CopiedMs = 1200;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const int ExStyle = -20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;

    private readonly Rect _anchor;
    private readonly GlassCard _card;
    private readonly ScaleTransform _scale = new(0.94, 0.94);
    private bool _closing;

    /// <summary>Cam adadaki gibi arkası değişince tazelenir.</summary>
    private readonly GlassWindowWatch _glassWatch;

    /// <param name="anchor">Seçimin ekrandaki yeri (piksel); balon altına, sığmazsa üstüne açılır.</param>
    private TranslationBubble(Rect anchor, FrameworkElement content)
    {
        _anchor = anchor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _card = new GlassCard
        {
            MaxWidth = MaxCardWidth,
            Padding = new Thickness(12, 9, 8, 6),
            CornerRadius = new CornerRadius(11),
            Opacity = 0,
            Content = content,
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = _scale
        };
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = _card };
        _glassWatch = new GlassWindowWatch(this, () => [_card]);
        SizeChanged += (_, _) => Place();
    }

    public static TranslationBubble ForTranslation(Rect anchor, Translation translation)
    {
        var text = new TextBlock
        {
            Text = translation.Text,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 14,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 4, 0),
            Foreground = (Brush)Application.Current.FindResource("TextPrimary")
        };

        var copy = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Kopyala",
            Child = Glyph("", 13, "TextPrimary")
        };
        copy.MouseEnter += (_, _) => copy.Background = (Brush)Application.Current.FindResource("HoverBrush");
        copy.MouseLeave += (_, _) => copy.Background = Brushes.Transparent;
        copy.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                Clipboard.SetText(translation.Text);
            }
            catch (COMException)
            {
                return;
            }

            // Kısa bir onay: simge tik olur, sonra geri döner.
            ((TextBlock)copy.Child).Text = "";
            var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CopiedMs) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                ((TextBlock)copy.Child).Text = "";
            };
            back.Start();
        };

        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        DockPanel.SetDock(copy, Dock.Right);
        footer.Children.Add(copy);
        footer.Children.Add(Glyph("", 12, "TextSecondary", new Thickness(0, 0, 6, 0)));
        footer.Children.Add(new TextBlock
        {
            Text = translation.Direction,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary")
        });

        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(footer);
        return new TranslationBubble(anchor, content);
    }

    /// <summary>Tek satırlık bilgi (çeviri açıldı, kapandı, çevrilemedi); kendiliğinden kapanır.</summary>
    public static TranslationBubble ForHint(Rect anchor, string message, TimeSpan duration)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 3) };
        row.Children.Add(Glyph("", 13, "TextSecondary", new Thickness(0, 0, 8, 0)));
        row.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13,
            Foreground = (Brush)Application.Current.FindResource("TextPrimary")
        });

        var bubble = new TranslationBubble(anchor, row);
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            bubble.Dismiss();
        };
        bubble.Loaded += (_, _) => timer.Start();
        return bubble;
    }

    /// <summary>Balonun ekrandaki yeri (piksel), içine tıklamayı ayırmak için.</summary>
    public bool Contains(System.Drawing.Point point)
    {
        if (PresentationSource.FromVisual(_card) is not HwndSource)
        {
            return false;
        }

        Point topLeft = _card.PointToScreen(new Point(0, 0));
        Point bottomRight = _card.PointToScreen(new Point(_card.ActualWidth, _card.ActualHeight));
        return point.X >= topLeft.X && point.X <= bottomRight.X && point.Y >= topLeft.Y && point.Y <= bottomRight.Y;
    }

    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | ExToolWindow | ExNoActivate);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();

        // Cam arkası hazır olunca belirir; açılırken balon görünmez, kendini görüntüye katmaz.
        await _card.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (_closing)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            _card.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(OpenMs);
        var grow = new DoubleAnimation(1, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    /// <summary>Seçimin altına, sol kenarı seçimle hizalı; sığmazsa üstüne; ekranın çalışma alanında kalır.</summary>
    private void Place()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        double scale = source.CompositionTarget.TransformToDevice.M11;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        int shadow = (int)Math.Round(ShadowMargin * scale);
        int gap = (int)Math.Round(SelectionGap * scale);
        int edge = (int)Math.Round(ScreenGap * scale);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)_anchor.Left, (int)_anchor.Bottom)).WorkingArea;

        int x = (int)_anchor.Left - shadow;
        int y = (int)_anchor.Bottom + gap - shadow;
        if (y + height - shadow > area.Bottom - edge)
        {
            y = (int)_anchor.Top - gap - height + shadow;
        }

        x = Math.Clamp(x, area.Left + edge - shadow, Math.Max(area.Left + edge - shadow, area.Right - width + shadow - edge));
        y = Math.Clamp(y, area.Top + edge - shadow, Math.Max(area.Top + edge - shadow, area.Bottom - height + shadow - edge));
        SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private static TextBlock Glyph(string glyph, double size, string brush, Thickness margin = default)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource(brush)
        };
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
