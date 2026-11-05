using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.AppBar;

namespace IslandBar;

/// <summary>
/// Şarkı sözü altyazısı (kullanıcının tasarımı, seçeneklerden B · Altyazı): adanın üstünde, şarkı kartının yerinde ortalı
/// cam şerit. O anki satır büyük, sıradaki altında soluk; satır değişince eskisi yukarı kayıp gider, sıradaki yerine
/// kayar. Çubuk gizliyken adanın durduğu yerde (şarkı kartı gibi). Fareyi geçirir: bir şeyin üstüne binse de engel olmaz.
/// Sahne küçültülünce sağında tam ekran ve kapat düğmeleri olur; yalnızca imleç bir düğmenin üstündeyken tıklanır,
/// şeridin gerisi yine fareyi geçirir (imleç <see cref="TrackPointer"/> ile izlenir).
/// </summary>
internal sealed class LyricsBand : Window
{
    private const double ShadowMargin = 20;
    private const double BandWidth = 380;
    private const double BandHeight = 54;
    private const double Corner = 12;
    private const double BarGap = 10;
    private const double CurrentTop = 8;
    private const double NextTop = 30;
    private const int LineMs = 420;
    private const int MoveMs = 280;
    private const int OpenMs = 360;
    private const int CloseMs = 190;
    private const double BudDistance = 12;
    private const int ExStyle = -20;
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    private readonly System.Drawing.Rectangle _island;
    private readonly AppBarEdge _edge;
    private readonly GlassCard _card;
    private readonly Canvas _lines;
    private TextBlock _current;
    private TextBlock _next;
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _scale = new();
    private readonly GlassWindowWatch _glassWatch;
    private readonly Stopwatch _moveClock = new();
    private System.Drawing.Point _moveFrom;
    private bool _moving;
    private bool _atBarPlace;
    private bool _shown;
    private bool _hiding;
    private string _currentLine = "";
    private string _nextLine = "";
    private const double ExpandSize = 34;
    private const double ExpandInset = 10;
    private const double ButtonGap = 2;
    private readonly Border _expand;
    private readonly Border _shut;
    private Action? _expandAction;
    private Action? _shutAction;
    private bool _clickable;
    private double _textLeft = 12;
    private double _textWidth = BandWidth - 24;

    public LyricsBand(System.Drawing.Rectangle island, AppBarEdge edge, bool barHidden)
    {
        _island = island;
        _edge = edge;
        _atBarPlace = barHidden;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");

        _lines = new Canvas { Width = BandWidth, Height = BandHeight, ClipToBounds = true };
        _expand = BandButton("\uE740", 14, "Tam ekran", () => _expandAction?.Invoke());
        _shut = BandButton("\uE711", 12, "Sözleri kapat", () => _shutAction?.Invoke());
        Canvas.SetLeft(_shut, BandWidth - ExpandInset - ExpandSize);
        Canvas.SetLeft(_expand, BandWidth - ExpandInset - 2 * ExpandSize - ButtonGap);
        _current = Line(current: true);
        _next = Line(current: false);
        _lines.Children.Add(_current);
        _lines.Children.Add(_next);
        Rest(_current, CurrentTop);
        Rest(_next, NextTop);

        _card = new GlassCard
        {
            Width = BandWidth,
            Height = BandHeight,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Corner),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, edge == AppBarEdge.Top ? 0 : 1),
            RenderTransform = new TransformGroup { Children = { _scale, _slide } },
            Content = _lines
        };
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = _card };
        _glassWatch = new GlassWindowWatch(this, () => [_card], edge);
        SizeChanged += (_, _) => Place();
    }

    public bool IsShown => _shown && !_hiding;

    private Border BandButton(string glyph, double size, string tip, Action click)
    {
        var button = new Border
        {
            Width = ExpandSize,
            Height = ExpandSize,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            ToolTip = tip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextPrimary")
            }
        };
        Canvas.SetTop(button, (BandHeight - ExpandSize) / 2);
        button.MouseEnter += (_, _) => button.Background = (Brush)FindResource("HoverBrush");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, _) => click();
        _lines.Children.Add(button);
        return button;
    }

    /// <summary>Tam ekran ve kapat düğmeleri (null: yok). Düğmeler varken satırlar onlara yer bırakır.</summary>
    public void SetButtons(Action? expand, Action? shut)
    {
        _expandAction = expand;
        _shutAction = shut;
        bool shown = expand != null;
        if (shown == (_expand.Visibility == Visibility.Visible))
        {
            return;
        }

        _expand.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _shut.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        // Satırlar soldan başlar, yalnızca düğmelerin yanında daralır (iki yandan daralınca erken kesiliyordu).
        _textLeft = 12;
        _textWidth = BandWidth - _textLeft - (shown ? ExpandInset + 2 * ExpandSize + ButtonGap + 4 : 12);
        foreach (TextBlock line in _lines.Children.OfType<TextBlock>())
        {
            line.Width = _textWidth;
            Canvas.SetLeft(line, _textLeft);
        }
        if (!shown)
        {
            SetClickable(false);
        }
    }

    /// <summary>
    /// İmleç bir düğmenin üstündeyse pencere tıklanır olur, değilse yine fareyi geçirir (kip her 100 ms'de çağırır).
    /// </summary>
    public void TrackPointer()
    {
        bool over = false;
        if (_expandAction != null && IsShown && _expand.IsVisible && GetCursorPos(out NativePoint cursor))
        {
            over = Contains(_expand, cursor) || Contains(_shut, cursor);
        }
        SetClickable(over);
    }

    private static bool Contains(FrameworkElement button, NativePoint cursor)
    {
        Point topLeft = button.PointToScreen(new Point(0, 0));
        Point bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        return cursor.X >= topLeft.X && cursor.X < bottomRight.X && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y;
    }

    private void SetClickable(bool clickable)
    {
        if (clickable == _clickable || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        _clickable = clickable;
        IsHitTestVisible = clickable;
        int style = GetWindowLong(source.Handle, ExStyle);
        SetWindowLong(source.Handle, ExStyle, clickable ? style & ~ExTransparent : style | ExTransparent);
        if (!clickable)
        {
            _expand.Background = Brushes.Transparent;
            _shut.Background = Brushes.Transparent;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | ExTransparent | ExToolWindow | ExNoActivate);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMoving();
        base.OnClosed(e);
    }

    /// <summary>Şeridi gösterir (yeniden açılabilir); camın arkası hazır olunca çubuktan kayarak çıkar.</summary>
    public async void Present()
    {
        if (_shown && !_hiding)
        {
            return;
        }

        _hiding = false;
        _shown = true;
        if (!IsVisible)
        {
            Show();
        }
        Place();
        await _card.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (!_shown || _hiding)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            _card.Opacity = 1;
            return;
        }

        Vector toward = TowardBar() * BudDistance;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Animate(_slide, TranslateTransform.XProperty, toward.X, 0, OpenMs, ease);
        Animate(_slide, TranslateTransform.YProperty, toward.Y, 0, OpenMs, ease);
        Animate(_scale, ScaleTransform.ScaleXProperty, 0.92, 1, OpenMs, ease);
        Animate(_scale, ScaleTransform.ScaleYProperty, 0.92, 1, OpenMs, ease);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Çubuğa doğru kayıp söner, pencere gizlenir (kapanmaz: kip açıkken yeniden çıkar).</summary>
    public void Conceal()
    {
        if (!_shown || _hiding)
        {
            return;
        }

        _hiding = true;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible)
        {
            Finish();
            return;
        }

        Vector toward = TowardBar() * BudDistance;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Animate(_slide, TranslateTransform.XProperty, 0, toward.X, CloseMs, ease);
        Animate(_slide, TranslateTransform.YProperty, 0, toward.Y, CloseMs, ease);
        var fade = new DoubleAnimation(_card.Opacity, 0, TimeSpan.FromMilliseconds(CloseMs)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_hiding)
            {
                Finish();
            }
        };
        _card.BeginAnimation(OpacityProperty, fade);

        void Finish()
        {
            _shown = false;
            _hiding = false;
            _card.BeginAnimation(OpacityProperty, null);
            _card.Opacity = 0;
            Hide();
        }
    }

    /// <summary>
    /// Satırlar: değişen satırda eski yukarı kayıp söner, sıradaki küçükten büyüyüp yerine çıkar, yeni sıradaki alttan
    /// gelir. Aynıysa bir şey olmaz.
    /// </summary>
    public void SetLines(string current, string next)
    {
        if (current == _currentLine && next == _nextLine)
        {
            return;
        }

        bool advanced = current != _currentLine;
        _currentLine = current;
        _nextLine = next;
        if (!advanced || !SystemParameters.ClientAreaAnimation || !IsShown)
        {
            _current.Text = current;
            _next.Text = next;
            Rest(_current, CurrentTop);
            Rest(_next, NextTop);
            return;
        }

        // Giden satır kendi kopyasıyla yukarı kayar.
        TextBlock leaving = _current;
        _current = Line(current: true);
        _current.Text = current;
        _lines.Children.Add(_current);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide(leaving, CurrentTop, CurrentTop - 20, 1, 0, ease, () => _lines.Children.Remove(leaving));

        // Sıradaki büyüklüğünde değil: büyük satır olarak alttan, sıradakinin yerinden çıkar.
        Slide(_current, NextTop, CurrentTop, 0.4, 1, ease, null);

        TextBlock oldNext = _next;
        _next = Line(current: false);
        _next.Text = next;
        _lines.Children.Add(_next);
        _lines.Children.Remove(oldNext);
        Slide(_next, NextTop + 16, NextTop, 0, 1, ease, null);
    }

    /// <summary>Çubuk gizlenip açılınca şarkı kartı gibi yer değiştirir.</summary>
    public void FollowBar(bool barHidden)
    {
        if (barHidden == _atBarPlace || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            _atBarPlace = barHidden;
            return;
        }

        _atBarPlace = barHidden;
        if (!IsVisible || !SystemParameters.ClientAreaAnimation || !GetWindowRect(source.Handle, out NativeRect rect))
        {
            Place();
            return;
        }

        _moveFrom = new System.Drawing.Point(rect.Left, rect.Top);
        _moveClock.Restart();
        if (!_moving)
        {
            _moving = true;
            CompositionTarget.Rendering += Move_Rendering;
        }
    }

    private TextBlock Line(bool current)
    {
        var line = new TextBlock
        {
            Width = _textWidth,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = current ? 14 : 11.5,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(current ? "TextPrimary" : "TextSecondary"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TranslateTransform()
        };
        Canvas.SetLeft(line, _textLeft);
        Canvas.SetTop(line, 0);
        return line;
    }

    private static void Rest(TextBlock line, double top)
    {
        var move = (TranslateTransform)line.RenderTransform;
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.Y = top;
        line.BeginAnimation(OpacityProperty, null);
        line.Opacity = 1;
    }

    private static void Slide(TextBlock line, double from, double to, double fromOpacity, double toOpacity, IEasingFunction ease, Action? done)
    {
        var move = (TranslateTransform)line.RenderTransform;
        var slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(LineMs)) { EasingFunction = ease };
        if (done != null)
        {
            slide.Completed += (_, _) => done();
        }
        move.BeginAnimation(TranslateTransform.YProperty, slide);
        line.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, TimeSpan.FromMilliseconds(LineMs * 0.7)));
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, int ms, IEasingFunction ease)
    {
        target.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    private Vector TowardBar()
    {
        return _edge switch
        {
            AppBarEdge.Top => new Vector(0, -1),
            AppBarEdge.Left => new Vector(-1, 0),
            AppBarEdge.Right => new Vector(1, 0),
            _ => new Vector(0, 1)
        };
    }

    private void Move_Rendering(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            StopMoving();
            return;
        }

        double t = Math.Clamp(_moveClock.Elapsed.TotalMilliseconds / MoveMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        System.Drawing.Point to = Spot(source);
        SetWindowPos(source.Handle, IntPtr.Zero, (int)Math.Round(_moveFrom.X + (to.X - _moveFrom.X) * eased),
            (int)Math.Round(_moveFrom.Y + (to.Y - _moveFrom.Y) * eased), 0, 0, NoSize | NoZOrder | NoActivate);
        if (t >= 1)
        {
            StopMoving();
            _ = _glassWatch.RefreshAsync(_card, isHidden: false);
        }
    }

    private void StopMoving()
    {
        if (_moving)
        {
            _moving = false;
            CompositionTarget.Rendering -= Move_Rendering;
        }
    }

    private void Place()
    {
        if (_moving || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        System.Drawing.Point spot = Spot(source);
        SetWindowPos(source.Handle, IntPtr.Zero, spot.X, spot.Y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    /// <summary>Dönüştür kutusuyla aynı kural: adanın üstünde (gizliyken yerinde), yan çubukta yanında, ortalı.</summary>
    private System.Drawing.Point Spot(HwndSource source)
    {
        double scale = source.CompositionTarget.TransformToDevice.M11;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        int shadow = (int)Math.Round(ShadowMargin * scale);
        int gap = (int)Math.Round(BarGap * scale);
        int centerX = _island.Left + _island.Width / 2;
        int centerY = _island.Top + _island.Height / 2;
        (int x, int y) = (_edge, _atBarPlace) switch
        {
            (AppBarEdge.Top, true) => (centerX - width / 2, _island.Top - shadow),
            (AppBarEdge.Top, false) => (centerX - width / 2, _island.Bottom + gap - shadow),
            (AppBarEdge.Left, true) => (_island.Left - shadow, centerY - height / 2),
            (AppBarEdge.Left, false) => (_island.Right + gap - shadow, centerY - height / 2),
            (AppBarEdge.Right, true) => (_island.Right - width + shadow, centerY - height / 2),
            (AppBarEdge.Right, false) => (_island.Left - gap - width + shadow, centerY - height / 2),
            (_, true) => (centerX - width / 2, _island.Bottom - height + shadow),
            _ => (centerX - width / 2, _island.Top - gap - height + shadow)
        };
        return new System.Drawing.Point(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
