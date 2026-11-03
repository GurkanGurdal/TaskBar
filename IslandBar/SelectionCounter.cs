using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IslandBar;

/// <summary>
/// Çeviri kipinde sürüklerken seçimin uzunluğu (kullanıcının seçimi: uygulamanın kendi seçimi kalır, yanında sayaç):
/// "120 / 300", sınırı geçince kehribar, o zaman başı çevrilir. Seçimin başladığı yerin üstünde durur, imleci izlemez:
/// cam arkası bir kez alınır. Fareye görünmez, odak almaz.
/// </summary>
internal sealed class SelectionCounter : Window
{
    private const double ShadowMargin = 20;

    /// <summary>Basılan nokta satırın ortası: sayaç satırın üstünü kapatmasın.</summary>
    private const double Gap = 22;
    private const double PlateWidth = 86;

    /// <summary>Bundan fazlası okunmaz, "999+" yazar.</summary>
    public const int MaxShown = 999;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const int ExStyle = -20;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;

    private readonly System.Drawing.Point _start;
    private readonly GlassCard _card;
    private readonly TextBlock _text;
    private bool _closing;

    /// <param name="start">Sürüklemenin başladığı yer, ekran pikseli.</param>
    public SelectionCounter(System.Drawing.Point start)
    {
        _start = start;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _text = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            Foreground = (Brush)FindResource("TextPrimary")
        };
        _card = new GlassCard
        {
            Width = PlateWidth,
            Padding = new Thickness(8, 4, 8, 5),
            CornerRadius = new CornerRadius(6),
            BlurRadius = 18,
            Opacity = 0,
            Content = _text
        };
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = _card };
    }

    public void Update(int count)
    {
        bool over = count > Translator.MaxLength;
        _text.Text = $"{(count >= MaxShown ? $"{MaxShown}+" : count)} / {Translator.MaxLength}";
        _text.Foreground = (Brush)FindResource(over ? "AttentionFill" : "TextPrimary");
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
        SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | ExTransparent | ExToolWindow | ExNoActivate);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        await _card.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (!_closing)
        {
            _card.Opacity = 1;
        }
    }

    /// <summary>Başlangıcın üstünde, sola hizalı; yer yoksa altında. Ekranın çalışma alanında kalır.</summary>
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
        int gap = (int)Math.Round(Gap * scale);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(_start).WorkingArea;

        int x = Math.Clamp(_start.X - shadow, area.Left - shadow, Math.Max(area.Left - shadow, area.Right - width + shadow));
        int y = _start.Y - gap - height + shadow;
        if (y + shadow < area.Top)
        {
            y = _start.Y + 2 * gap - shadow;
        }
        SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
