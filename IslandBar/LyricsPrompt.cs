using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>
/// Sözler kipi açılırken ikinci ekran varsa (kullanıcının isteği): "İkinci ekranda açılsın mı?" Tekerleğin bırakıldığı
/// yerde küçük cam kart; "Bir daha sorma" işaretlenirse seçim ayara yazılır (kullanıcının seçimi: bir kez sor, hatırla).
/// Esc ya da dışarı tıklamak vazgeçer, kip açılmaz.
/// </summary>
internal sealed class LyricsPrompt : Window
{
    private const double ShadowMargin = 20;
    private const double CardWidth = 280;

    private readonly Action<bool, bool> _chosen;
    private readonly System.Drawing.Point _center;
    private readonly GlassCard _card;
    private readonly TextBlock _box;
    private bool _remember;
    private bool _done;

    /// <param name="chosen">İkinci ekranda mı, hatırlansın mı.</param>
    public LyricsPrompt(System.Drawing.Point center, Action<bool, bool> chosen)
    {
        _center = center;
        _chosen = chosen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Choice("İkinci ekranda", primary: true, () => Finish(true)));
        buttons.Children.Add(Choice("Burada", primary: false, () => Finish(false)));

        _box = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondary")
        };
        var remember = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Children =
            {
                _box,
                new TextBlock { Text = "Bir daha sorma", Margin = new Thickness(7, 0, 0, 0), FontSize = 12, Foreground = (Brush)FindResource("TextSecondary") }
            }
        };
        remember.MouseLeftButtonUp += (_, _) =>
        {
            _remember = !_remember;
            _box.Text = _remember ? "" : "";
            _box.Foreground = (Brush)FindResource(_remember ? "TextPrimary" : "TextSecondary");
        };

        _card = new GlassCard
        {
            Width = CardWidth,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(15),
            Opacity = 0,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Sözler ikinci ekranda açılsın mı?", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextPrimary") },
                    new TextBlock
                    {
                        Text = "İkinci ekran kararır, sözler ortasında büyük gösterilir. Burada derseniz çubuğun üstünde altyazı olur.",
                        Margin = new Thickness(0, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = (Brush)FindResource("TextSecondary")
                    },
                    buttons,
                    remember
                }
            }
        };
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = _card };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
            }
        };
        Deactivated += (_, _) => Cancel();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            double scale = target.TransformToDevice.M11;
            System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(_center).WorkingArea;
            Left = Math.Clamp(_center.X / scale - ActualWidth / 2, area.Left / scale, area.Right / scale - ActualWidth);
            Top = Math.Clamp(_center.Y / scale - ActualHeight / 2, area.Top / scale, area.Bottom / scale - ActualHeight);
        }
        await _card.RefreshGlassAsync(animateTint: false, isHidden: true);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 140 : 0)));
        TakeForeground();
    }

    /// <summary>
    /// Tekerlek kapanınca ön plan önceki pencereye dönüyor, Windows sonradan gelen `Activate`'i reddediyor: soru öne
    /// geçemeyince Esc ve dışarı tıklama ona ulaşmıyordu (sınamada görüldü). Öndeki pencerenin girdisine bir an bağlanıp
    /// öne geçer (çubuktaki Başlat'ın yöntemi).
    /// </summary>
    private void TakeForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        if (attached)
        {
            AttachThreadInput(thisThread, foregroundThread, false);
        }
        Activate();
    }

    [DllImport("user32")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    private void Finish(bool second)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        Close();
        _chosen(second, _remember);
    }

    private void Cancel()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        Close();
    }

    private Border Choice(string text, bool primary, Action chosen)
    {
        var choice = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 5, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource(primary ? "AccentFill" : "MutedFill"),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = (Brush)FindResource(primary ? "AccentInk" : "TextPrimary") }
        };
        choice.MouseEnter += (_, _) => choice.Opacity = 0.85;
        choice.MouseLeave += (_, _) => choice.Opacity = 1;
        choice.MouseLeftButtonUp += (_, _) => chosen();
        return choice;
    }
}
