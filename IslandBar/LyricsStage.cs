using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>Sahnenin o anki hali: satır (anahtarı değişince yeni satır gelir), şarkı ve konum.</summary>
internal sealed record LyricsView(string Key, string Text, string Next, TimeSpan LineLength, string Title, string Artist,
    BitmapSource? Cover, Color Accent, string? Note, bool Playing)
{
    /// <summary>Şarkının anahtarı; boşsa çalan yok (harita ve düğmeler gizli).</summary>
    public string Song { get; init; } = "";
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<LyricLine> Lines { get; init; } = [];
    public bool CanSeek { get; init; }
}

/// <summary>Sahnenin düğmeleri: Spotify'a Windows'un medya denetimiyle gider.</summary>
internal sealed record LyricsCommands(Action PlayPause, Action Previous, Action Next, Action<TimeSpan> Seek, Action Close);

/// <summary>
/// İkinci ekranda tam ekran sözler (kullanıcının tasarımı, seçeneklerden C · Poster): arka planda kapağın karartılmış ve
/// bulanık hali (kapak yoksa düz koyu; kullanıcının isteği, ilk sürümde düz siyahtı), kenarda ince
/// çerçeve ve onun üstünde iki karşılıklı parça kovalar (rengi kapaktan; gradyan ve parıltı yok). Satır kelimelere
/// bölünür, kısa satırlar halinde büyük ve alt alta dizilir; satırın girişi ritmine göre değişir (çarpma, maskeden kayma,
/// harf harf, bulanıktan netleşme, yandan süzülme), bazı satırlarda bir kelime kapağın renginde (lyric klip gibi). Altta soluk sıradaki satır, sol altta kapak ve şarkı. Fare kıpırdayınca sağ üstte Kapat çıkar,
/// durunca imleç de gizlenir. Kelime zamanları LRCLIB'de yok: kelimeler satırın süresine yayılır.
/// Kontroller (seçeneklerden B · Söz haritası): en altta satırlara bölünmüş ilerleme çizgisi (sözsüz yerler boşluk); bir
/// parçanın üstünde o satırın sözü çıkar, tıklayınca şarkı oraya gider. Şarkının yanında önceki / oynat / sonraki,
/// sağda süre. Bu çubuk kapalı durur (solda yalnızca kapak ve şarkı); imleç alt kenara ya da şarkının üstüne gelince
/// alttan kayarak açılır, şarkı yukarı yer açar (kullanıcının tasarımı). Çerçevesi çubuğun üstüne binen ekolayzırda
/// açılınca çerçevenin alt kısmı aşağı doğru silinir. Klavye: Boşluk, ← / → 5 sn (çubuğu da açar), Esc.
/// </summary>
internal sealed class LyricsStage : Window
{
    private const double FrameInset = 16;
    private const double FrameCorner = 14;
    private const double FrameLine = 1.5;
    private const double ChaserLine = 3;
    private const double ChaserShare = 0.07;
    private const int ChaseSeconds = 16;
    private const int WordMs = 380;
    private const int MaxStaggerMs = 300;
    private const int LeaveMs = 300;
    private const int IdleMs = 2500;
    private const double PosterReserve = 390;
    private const double SideMargin = 48;
    private const double MapLine = 6;
    private const double MapGap = 4;
    private const double MapBottom = 34;
    private const double MapHit = 26;
    private const double MapClosedScale = 0.3;
    private const double SongBottom = 44;
    private const double BarSlide = 16;

    /// <summary>Çubuk imleç alttan bu kadar yakındayken açılır; bunun dışına çıkınca kapanır (arada titremesin).</summary>
    private const double RevealZone = 150;
    private const double KeepZone = 200;

    /// <summary>Çubuk açıkken ekolayzırın silindiği yer: alttan bu yükseklikte tam, bunun altında yok.</summary>
    private const double EffectFadeTop = 210;
    private const double EffectFadeBottom = 120;
    private const double ButtonSize = 44;
    private const double SeekStepSeconds = 5;

    /// <summary>Satıra gidince biraz önceden başlasın: ilk hece kaçmasın.</summary>
    private static readonly TimeSpan LineLeadIn = TimeSpan.FromMilliseconds(300);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    private readonly System.Drawing.Rectangle _bounds;
    private readonly Grid _root = new();
    private readonly Grid _backdrop = new() { IsHitTestVisible = false };
    private BitmapSource? _backdropCover;

    /// <summary>Kapak küçük (120 px) gelir: küçük bir resme bulanık çizilip ekrana büyütülür, pikseller görünmez.</summary>
    private const int BackdropWidth = 192;
    private const int BackdropBlurRadius = 4;
    private const int BackdropFadeMs = 900;
    private readonly Canvas _frame = new();
    private readonly Rectangle _chasers;
    private readonly Rectangle _edge;
    private readonly Grid _poster = new();
    private readonly TextBlock _next;
    private readonly TextBlock _note;
    private readonly Border _cover;
    private readonly ImageBrush _coverImage = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _title;
    private readonly TextBlock _artist;
    private readonly Border _close;
    private readonly DispatcherTimer _idle;
    private readonly SolidColorBrush _accent = new(Colors.White);
    private readonly SolidColorBrush _accentPast = new(Colors.White) { Opacity = 0.45 };
    private readonly LyricsCommands _commands;
    private readonly Grid _mapHost = new();
    private readonly Canvas _map = new();
    private readonly ScaleTransform _mapScale = new(1, MapClosedScale);
    private readonly TranslateTransform _songLift = new();
    private readonly TranslateTransform _buttonsSlide = new(0, BarSlide);
    private readonly TranslateTransform _timeSlide = new(0, BarSlide);
    private readonly TranslateTransform _mapSlide = new(0, BarSlide);
    private GradientStop _fadeMiddle = new(Colors.Black, 0);
    private GradientStop _fadeEnd = new(Colors.Black, 1);
    private bool _barOpen;
    private readonly Rectangle _head;
    private readonly Border _tip;
    private readonly TextBlock _tipText;
    private readonly TextBlock _time;
    private readonly StackPanel _buttons;
    private readonly TextBlock _playGlyph;
    private readonly List<MapPart> _mapParts = [];
    private string _mapKey = "";
    private LyricsView? _view;
    private bool _active;
    private string _key = "";
    private StackPanel? _block;
    private bool _playing = true;

    /// <summary>Çerçeve kipleri (kullanıcının seçtikleri; Ayarlar'da): 0 İkili tur, 1 Kenar ekolayzır, 2 Lunapark.</summary>
    public const int FrameChasers = 0;
    public const int FrameEqualizer = 1;
    public const int FrameMarquee = 2;

    private const double BarSpacing = 26;
    private const double BarLine = 3;
    private const double BarMin = 3;
    private const double BarMax = 46;
    private const double DotSpacing = 24;
    private const double DotSize = 4.5;
    private const double MarqueeSeconds = 4;
    private const double MarqueeTail = 0.11;

    private int _frameStyle;
    private readonly List<(Shape Shape, Point At, Vector Inward, double Along, double Value)> _parts = [];
    private AudioEndpoint.Meter? _meter;
    private bool _animating;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _left;
    private double _right;
    private double _ceiling = MinCeiling;
    private const double MinCeiling = 0.04;
    private const double CeilingFall = 0.996;

    /// <param name="bounds">Ekranın tamamı, piksel.</param>
    /// <param name="frameStyle"><see cref="FrameChasers"/>, <see cref="FrameEqualizer"/> ya da <see cref="FrameMarquee"/>.</param>
    public LyricsStage(System.Drawing.Rectangle bounds, int frameStyle, LyricsCommands commands)
    {
        _bounds = bounds;
        _commands = commands;
        _frameStyle = frameStyle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = (Brush)FindResource("LyricsStageFill");
        FontFamily = Display;

        _edge = new Rectangle { RadiusX = FrameCorner, RadiusY = FrameCorner, StrokeThickness = FrameLine, Stroke = (Brush)FindResource("LyricsStageEdge") };
        _chasers = new Rectangle { RadiusX = FrameCorner, RadiusY = FrameCorner, StrokeThickness = ChaserLine, Stroke = _accent, StrokeDashCap = PenLineCap.Round };
        _frame.Children.Add(_edge);
        _frame.Children.Add(_chasers);
        _frame.SizeChanged += (_, _) => LayoutFrame();

        // Poster ortanın biraz üstünde: altta sıradaki satır ve şarkı var.
        _poster.HorizontalAlignment = HorizontalAlignment.Center;
        _poster.VerticalAlignment = VerticalAlignment.Center;
        _poster.Margin = new Thickness(0, 0, 0, 118);

        _next = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 120),
            FontSize = 22,
            Foreground = (Brush)FindResource("LyricsStageDim"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _note = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 120),
            FontSize = 20,
            Foreground = (Brush)FindResource("LyricsStageMuted")
        };

        _cover = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(8), Background = _coverImage };
        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("LyricsStageText"), TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13.5, Foreground = (Brush)FindResource("LyricsStageMuted"), TextTrimming = TextTrimming.CharacterEllipsis };
        _playGlyph = new TextBlock();
        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = _buttonsSlide,
            Children =
            {
                CreateButton(new TextBlock { Text = "\uE892" }, 18, false, () => _commands.Previous()),
                CreateButton(_playGlyph, 20, true, () => _commands.PlayPause()),
                CreateButton(new TextBlock { Text = "\uE893" }, 18, false, () => _commands.Next())
            }
        };
        var song = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(SideMargin, 0, 0, SongBottom),
            RenderTransform = _songLift,
            Children =
            {
                _cover,
                new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 220, MaxWidth = 420, Children = { _title, _artist } },
                _buttons
            }
        };

        _time = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, SideMargin, 62),
            FontSize = 15,
            Foreground = (Brush)FindResource("LyricsStageMuted"),
            Opacity = 0,
            RenderTransform = _timeSlide
        };

        // Harita: satır başına bir parça. Tutulacak yer çizgiden geniş (ince çizgiye isabet zor).
        _head = new Rectangle { Width = 3, Height = 18, RadiusX = 1.5, RadiusY = 1.5, Fill = (Brush)FindResource("LyricsStageText"), IsHitTestVisible = false };
        _map.Height = MapLine;
        _map.VerticalAlignment = VerticalAlignment.Center;
        _map.RenderTransform = _mapScale;
        _map.RenderTransformOrigin = new Point(0.5, 0.5);
        _map.IsHitTestVisible = false;
        _map.SizeChanged += (_, _) => LayoutMap();
        var headLayer = new Canvas { Height = 0, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Children = { _head } };
        Canvas.SetTop(_head, -_head.Height / 2);
        _mapHost.Height = MapHit;
        _mapHost.VerticalAlignment = VerticalAlignment.Bottom;
        _mapHost.Margin = new Thickness(SideMargin, 0, SideMargin, MapBottom - (MapHit - MapLine) / 2);
        _mapHost.Background = Brushes.Transparent;
        _mapHost.Opacity = 0;
        _mapHost.IsHitTestVisible = false;
        _mapHost.RenderTransform = _mapSlide;
        _mapHost.Children.Add(_map);
        _mapHost.Children.Add(headLayer);
        _mapHost.MouseMove += MapHost_MouseMove;
        _mapHost.MouseLeftButtonDown += MapHost_MouseLeftButtonDown;

        _tipText = new TextBlock { FontSize = 17, Foreground = (Brush)FindResource("LyricsStageText") };
        _tip = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(14, 8, 14, 9),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("LyricsStageButton"),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = _tipText
        };
        _mapHost.MouseLeave += (_, _) => Fade(_tip, 0, 120);

        _close = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 44, 0),
            Padding = new Thickness(14, 7, 14, 8),
            CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("LyricsStageButton"),
            Cursor = Cursors.Hand,
            Opacity = 0,
            Child = new TextBlock { Text = "Sözleri kapat", FontSize = 13, Foreground = (Brush)FindResource("LyricsStageText") }
        };
        _close.MouseLeftButtonUp += (_, _) => _commands.Close();

        _root.Children.Add(_backdrop);
        _root.Children.Add(_frame);
        _root.Children.Add(_poster);
        _root.Children.Add(_next);
        _root.Children.Add(_note);
        _root.Children.Add(song);
        _root.Children.Add(_time);
        _root.Children.Add(_mapHost);
        _root.Children.Add(_tip);
        _root.Children.Add(_close);
        Content = _root;

        _idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleMs) };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();

            // İmleç çubuğun üstünde duruyorsa (balonu okuyor) açık kalır.
            if (InRevealZone(Mouse.GetPosition(this).Y, KeepZone) && IsMouseOver)
            {
                return;
            }
            SetActive(false);
            SetBarOpen(false);
        };
        MouseMove += (_, e) =>
        {
            SetActive(true);
            _idle.Stop();
            _idle.Start();
            double y = e.GetPosition(this).Y;
            if (InRevealZone(y, RevealZone))
            {
                SetBarOpen(true);
            }
            else if (!InRevealZone(y, KeepZone))
            {
                SetBarOpen(false);
            }
        };
        MouseLeave += (_, _) => SetBarOpen(false);
        KeyDown += Stage_KeyDown;
        Cursor = Cursors.None;
    }

    private Border CreateButton(TextBlock glyph, double size, bool filled, Action click)
    {
        glyph.FontFamily = Icons;
        glyph.FontSize = size;
        glyph.Foreground = (Brush)FindResource("LyricsStageText");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        Brush rest = filled ? (Brush)FindResource("LyricsStageButton") : Brushes.Transparent;
        var button = new Border
        {
            Width = ButtonSize,
            Height = ButtonSize,
            Margin = new Thickness(3, 0, 3, 0),
            CornerRadius = new CornerRadius(12),
            Background = rest,
            Cursor = Cursors.Hand,
            Child = glyph
        };
        button.MouseEnter += (_, _) => button.Background = (Brush)FindResource("LyricsStageButtonHover");
        button.MouseLeave += (_, _) => button.Background = rest;
        button.MouseLeftButtonUp += (_, _) => click();
        return button;
    }

    /// <summary>Fare kıpırdadı (true) ya da durdu: Kapat ve imleç.</summary>
    private void SetActive(bool active)
    {
        if (active == _active)
        {
            return;
        }

        _active = active;
        Cursor = active ? null : Cursors.None;
        Fade(_close, active ? 1 : 0, active ? 150 : 300);
    }

    private bool InRevealZone(double y, double zone)
    {
        return ActualHeight > 0 && y >= ActualHeight - zone;
    }

    /// <summary>
    /// Zaman çubuğu: düğmeler, süre ve harita alttan kayarak gelir, harita incelikten kalınlaşır, şarkı yukarı yer açar.
    /// Kayma, solma değil (kullanıcının tercihi); solma yalnızca kaymaya eşlik eder.
    /// </summary>
    private void SetBarOpen(bool open)
    {
        if (open == _barOpen)
        {
            return;
        }

        _barOpen = open;
        bool animate = SystemParameters.ClientAreaAnimation;
        var duration = TimeSpan.FromMilliseconds(animate ? open ? 280 : 200 : 0);
        IEasingFunction ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        foreach ((UIElement part, TranslateTransform slide) in new[] { ((UIElement)_buttons, _buttonsSlide), (_time, _timeSlide), (_mapHost, _mapSlide) })
        {
            part.IsHitTestVisible = open;
            part.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = ease });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(open ? 0 : BarSlide, duration) { EasingFunction = ease });
        }
        _mapScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(open ? 1 : MapClosedScale, duration) { EasingFunction = ease });
        _songLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(open ? SongBottom - 62 : 0, duration) { EasingFunction = ease });
        if (!open)
        {
            Fade(_tip, 0, 120);
        }
        FadeEffectEdge(duration);
    }

    /// <summary>
    /// Ekolayzırın çubukları zaman çubuğunun üstüne biniyor: çubuk açıkken çerçevenin alt kısmı opaklık maskesiyle aşağı
    /// doğru silinir (renk değil, yalnızca görünürlük). Kapanınca geri gelir, maske bitince kalkar (tam ekran maske ucuz değil).
    /// </summary>
    private void FadeEffectEdge(TimeSpan duration)
    {
        bool fade = _barOpen && _frameStyle == FrameEqualizer;
        if (fade && _frame.OpacityMask == null)
        {
            double height = Math.Max(1, _frame.ActualHeight);
            _fadeMiddle = new GradientStop(Colors.Black, (height - EffectFadeBottom) / height);
            _fadeEnd = new GradientStop(Colors.Black, 1);
            _frame.OpacityMask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, height),
                GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, (height - EffectFadeTop) / height), _fadeMiddle, _fadeEnd }
            };
        }
        if (_frame.OpacityMask == null)
        {
            return;
        }

        Color to = fade ? Colors.Transparent : Colors.Black;
        var clear = new ColorAnimation(to, duration);
        if (!fade)
        {
            clear.Completed += (_, _) =>
            {
                if (!_barOpen || _frameStyle != FrameEqualizer)
                {
                    _frame.OpacityMask = null;
                }
            };
        }
        _fadeMiddle.BeginAnimation(GradientStop.ColorProperty, clear);
        _fadeEnd.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, duration));
    }

    private void Stage_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _commands.PlayPause();
                break;
            case Key.Left or Key.Right:
                if (_view is { CanSeek: true } view)
                {
                    _commands.Seek(view.Position + TimeSpan.FromSeconds(e.Key == Key.Left ? -SeekStepSeconds : SeekStepSeconds));

                    // Nereye gittiği görünsün; fare durunca kapanır.
                    SetBarOpen(true);
                    _idle.Stop();
                    _idle.Start();
                }
                break;
            case Key.Escape:
                _commands.Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, NoZOrder | NoActivate);
    }

    protected override void OnClosed(EventArgs e)
    {
        _idle.Stop();
        StopParts();
        base.OnClosed(e);
    }

    /// <summary>Yeni satırda (anahtar değişince) poster yeniden kurulur; aynı satırda yalnızca şarkı ve durum.</summary>
    public void Update(LyricsView view)
    {
        if (_accent.Color != view.Accent)
        {
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(view.Accent, TimeSpan.FromMilliseconds(600)));
            _accentPast.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(view.Accent, TimeSpan.FromMilliseconds(600)));
        }

        _view = view;
        UpdateControls(view);

        _coverImage.ImageSource = view.Cover;
        if (!ReferenceEquals(view.Cover, _backdropCover))
        {
            SetBackdrop(view.Cover);
        }
        _cover.Visibility = view.Cover == null ? Visibility.Collapsed : Visibility.Visible;
        _title.Text = view.Title;
        _artist.Text = view.Artist;
        _note.Text = view.Note ?? "";
        _next.Text = view.Note == null ? view.Next : "";

        if (view.Playing != _playing)
        {
            _playing = view.Playing;
            Fade(_poster, view.Playing ? 1 : 0.45, 300);
        }

        if (view.Key == _key)
        {
            return;
        }

        _key = view.Key;
        ShowLine(view);
    }

    /// <summary>Şarkı değişince yeni kapak eskisinin üstünde belirir (arka plan; geçişte kesinti olmasın), eskisi sonra kalkar.</summary>
    private void SetBackdrop(BitmapSource? cover)
    {
        _backdropCover = cover;
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? BackdropFadeMs : 0);
        List<Image> old = _backdrop.Children.OfType<Image>().ToList();
        foreach (Image image in old)
        {
            var leave = new DoubleAnimation(0, duration);
            leave.Completed += (_, _) => _backdrop.Children.Remove(image);
            image.BeginAnimation(OpacityProperty, leave);
        }

        if (cover == null)
        {
            return;
        }

        var shown = new Image { Source = Backdrop(cover), Stretch = Stretch.Fill, Opacity = 0 };
        _backdrop.Children.Add(shown);
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
    }

    /// <summary>
    /// Arka plan: kapağın ekran oranında kırpılmış, bulanık ve karartılmış hali, ekran boyunda tek opak resim; ekranda 1:1.
    /// Bulanıklık, karartma ve büyütme piksellerde elle (değerler 0-255'te kalır). WPF'in büyütme süzgeci (ekran kartında
    /// da `RenderTargetBitmap`'te de) karanlıkla aydınlığın komşu olduğu yerde sıfırın altına taşıyordu: en karanlık
    /// yerlerde ton çizgisini izleyen beyaz / siyah çatlaklar çıktı (küçük resmin kendisi temizdi, dosyaya yazılıp bakıldı).
    /// </summary>
    private BitmapSource Backdrop(BitmapSource cover)
    {
        (byte[] small, int smallWidth, int smallHeight) = Blurred(cover);
        int width = Math.Max(1, _bounds.Width);
        int height = Math.Max(1, _bounds.Height);
        byte[] full = Enlarge(small, smallWidth, smallHeight, width, height);
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, full, width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Çift doğrusal büyütme (komşu dört pikselin ağırlıklı ortalaması: aşma yok).</summary>
    private static byte[] Enlarge(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var target = new byte[width * height * 4];
        var left = new int[width];
        var right = new int[width];
        var share = new double[width];
        for (int x = 0; x < width; x++)
        {
            double at = Math.Clamp((x + 0.5) * sourceWidth / width - 0.5, 0, sourceWidth - 1);
            left[x] = (int)at;
            right[x] = Math.Min(left[x] + 1, sourceWidth - 1);
            share[x] = at - left[x];
        }

        for (int y = 0; y < height; y++)
        {
            double at = Math.Clamp((y + 0.5) * sourceHeight / height - 0.5, 0, sourceHeight - 1);
            int top = (int)at;
            int bottom = Math.Min(top + 1, sourceHeight - 1);
            double down = at - top;
            int rowTop = top * sourceWidth * 4;
            int rowBottom = bottom * sourceWidth * 4;
            int output = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int a = rowTop + left[x] * 4;
                int b = rowTop + right[x] * 4;
                int c = rowBottom + left[x] * 4;
                int d = rowBottom + right[x] * 4;
                double across = share[x];
                for (int channel = 0; channel < 3; channel++)
                {
                    double upper = source[a + channel] + (source[b + channel] - source[a + channel]) * across;
                    double lower = source[c + channel] + (source[d + channel] - source[c + channel]) * across;
                    target[output + channel] = (byte)(upper + (lower - upper) * down + 0.5);
                }
                target[output + 3] = 255;
                output += 4;
            }
        }
        return target;
    }

    /// <summary>Kapağın küçük, bulanık (üç geçişli kutu bulanıklığı ≈ Gauss) ve karartılmış hali; Bgr32 pikseller.</summary>
    private (byte[] Pixels, int Width, int Height) Blurred(BitmapSource cover)
    {
        double ratio = ActualWidth > 0 && ActualHeight > 0 ? ActualHeight / ActualWidth : 9.0 / 16;
        int width = BackdropWidth;
        int height = Math.Max(1, (int)Math.Round(width * ratio));
        var picture = new Rectangle { Width = width, Height = height, Fill = new ImageBrush(cover) { Stretch = Stretch.UniformToFill } };
        picture.Measure(new Size(width, height));
        picture.Arrange(new Rect(0, 0, width, height));
        var small = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        small.Render(picture);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        small.CopyPixels(pixels, stride, 0);
        byte[] spare = new byte[pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(pixels, spare, width, height, BackdropBlurRadius, horizontal: true);
            BoxBlur(spare, pixels, width, height, BackdropBlurRadius, horizontal: false);
        }
        // Karartma resmin içinde: üstünde ayrı yarı saydam katman yok.
        Color shade = ((SolidColorBrush)FindResource("LyricsStageShade")).Color;
        double keep = 1 - shade.A / 255.0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * keep + shade.B * (1 - keep));
            pixels[i + 1] = (byte)(pixels[i + 1] * keep + shade.G * (1 - keep));
            pixels[i + 2] = (byte)(pixels[i + 2] * keep + shade.R * (1 - keep));
            pixels[i + 3] = 255;
        }
        return (pixels, width, height);
    }

    /// <summary>Tek yönde kayan ortalama; kenarda son piksel tekrarlanır (kenar kararmasın).</summary>
    private static void BoxBlur(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        int step = horizontal ? 4 : width * 4;
        int span = 2 * radius + 1;
        for (int line = 0; line < lines; line++)
        {
            int origin = horizontal ? line * width * 4 : line * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                int Value(int at) => source[origin + Math.Clamp(at, 0, length - 1) * step + channel];
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    sum += Value(k);
                }
                for (int at = 0; at < length; at++)
                {
                    target[origin + at * step + channel] = (byte)(sum / span);
                    sum += Value(at + radius + 1) - Value(at - radius);
                }
            }
        }
    }

    /// <summary>Bir satırın haritadaki parçası: başı, sonu, sözü (null: sözsüz şarkıda tek parça).</summary>
    private sealed record MapPart(TimeSpan Start, TimeSpan End, string? Text, Border Back, Border Fill);

    private void UpdateControls(LyricsView view)
    {
        bool song = view.Song.Length > 0;
        _buttons.Visibility = song ? Visibility.Visible : Visibility.Hidden;
        _playGlyph.Text = view.Playing ? "\uE769" : "\uE768";
        bool timed = song && view.Duration > TimeSpan.Zero;
        _time.Text = timed ? $"{Clock(view.Position)} / {Clock(view.Duration)}" : "";
        _mapHost.Visibility = timed ? Visibility.Visible : Visibility.Hidden;
        _mapHost.Cursor = view.CanSeek ? Cursors.Hand : null;
        if (!timed)
        {
            return;
        }

        string key = $"{view.Song}|{view.Duration}|{view.Lines.Count}";
        if (key != _mapKey)
        {
            _mapKey = key;
            BuildMap(view);
        }
        PaintMap(view.Position);
    }

    /// <summary>Sözlü her satır bir parça, sonraki satıra (boş satır ara demek) ya da şarkının sonuna kadar.</summary>
    private void BuildMap(LyricsView view)
    {
        foreach (MapPart part in _mapParts)
        {
            _map.Children.Remove(part.Back);
        }
        _mapParts.Clear();

        IReadOnlyList<LyricLine> lines = view.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Length == 0 || lines[i].At >= view.Duration)
            {
                continue;
            }

            TimeSpan end = i + 1 < lines.Count && lines[i + 1].At < view.Duration ? lines[i + 1].At : view.Duration;
            AddPart(lines[i].At, end, lines[i].Text);
        }
        if (_mapParts.Count == 0)
        {
            AddPart(TimeSpan.Zero, view.Duration, null);
        }
        LayoutMap();
    }

    private void AddPart(TimeSpan start, TimeSpan end, string? text)
    {
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
        var back = new Border { Height = MapLine, CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("LyricsStageMap"), Child = fill };
        _map.Children.Add(back);
        _mapParts.Add(new MapPart(start, end, text, back, fill));
    }

    private void LayoutMap()
    {
        double width = _map.ActualWidth;
        double total = _view?.Duration.TotalSeconds ?? 0;
        if (width <= 0 || total <= 0)
        {
            return;
        }

        foreach (MapPart part in _mapParts)
        {
            double left = part.Start.TotalSeconds / total * width;
            double right = part.End.TotalSeconds / total * width;
            Canvas.SetLeft(part.Back, left);
            part.Back.Width = Math.Max(MapLine, right - left - MapGap);
        }
        PaintMap(_view!.Position);
    }

    /// <summary>Geçen parçalar soluk renkte, o anki parça kendi içinde dolar; baş çizgisi tam konumda.</summary>
    private void PaintMap(TimeSpan position)
    {
        double width = _map.ActualWidth;
        double total = _view?.Duration.TotalSeconds ?? 0;
        if (width <= 0 || total <= 0)
        {
            return;
        }

        foreach (MapPart part in _mapParts)
        {
            double share = Math.Clamp((position - part.Start).TotalSeconds / (part.End - part.Start).TotalSeconds, 0, 1);
            part.Fill.Width = part.Back.Width * share;
            part.Fill.Background = share >= 1 ? _accentPast : _accent;
        }
        Canvas.SetLeft(_head, Math.Clamp(position.TotalSeconds / total, 0, 1) * width - _head.Width / 2);
    }

    /// <summary>Haritada imlecin altındaki an ve (varsa) sözlü parça.</summary>
    private (TimeSpan At, MapPart? Part) MapPoint(MouseEventArgs e)
    {
        double width = _map.ActualWidth;
        double total = _view?.Duration.TotalSeconds ?? 0;
        double x = e.GetPosition(_map).X;
        var at = TimeSpan.FromSeconds(Math.Clamp(width > 0 ? x / width : 0, 0, 1) * total);
        MapPart? part = _mapParts.FirstOrDefault(candidate => candidate.Text != null && at >= candidate.Start && at < candidate.End);
        return (at, part);
    }

    private void MapHost_MouseMove(object sender, MouseEventArgs e)
    {
        (_, MapPart? part) = MapPoint(e);
        if (part == null || _view == null)
        {
            Fade(_tip, 0, 120);
            return;
        }

        if (_tipText.Text != part.Text)
        {
            _tipText.Text = part.Text;
        }
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Balon parçanın ortasında, ekrandan taşmadan.
        double middle = SideMargin + (part.Start + part.End).TotalSeconds / 2 / _view.Duration.TotalSeconds * _map.ActualWidth;
        double widest = Math.Max(SideMargin, ActualWidth - SideMargin - _tip.DesiredSize.Width);
        _tip.Margin = new Thickness(Math.Clamp(middle - _tip.DesiredSize.Width / 2, SideMargin, widest), 0, 0, MapBottom + 22);
        Fade(_tip, 1, 120);
    }

    private void MapHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_view is not { CanSeek: true })
        {
            return;
        }

        (TimeSpan at, MapPart? part) = MapPoint(e);
        _commands.Seek(part == null ? at : part.Start > LineLeadIn ? part.Start - LineLeadIn : TimeSpan.Zero);
        e.Handled = true;
    }

    private static string Clock(TimeSpan time)
    {
        int seconds = (int)Math.Max(0, time.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>Ayar değişti: çerçeve yeniden kurulur.</summary>
    public void SetFrame(int frameStyle)
    {
        if (frameStyle == _frameStyle)
        {
            return;
        }

        _frameStyle = frameStyle;
        LayoutFrame();
        _frame.OpacityMask = null;
        FadeEffectEdge(TimeSpan.Zero);
    }

    /// <summary>
    /// Çerçeve: İkili turda ince kenar çizgisi ve üstünde iki karşılıklı parça (kesik çizgi, sürekli döner); kenar
    /// ekolayzırında kenar boyunca içe bakan kısa çubuklar ses düzeyiyle uzar; Lunaparkta kenarda noktalar, üstlerinden
    /// sırayla bir ışık dalgası geçer. Sürekli animasyon (kullanıcı istedi); renk kapaktan.
    /// </summary>
    private void LayoutFrame()
    {
        StopParts();
        double width = Math.Max(0, _frame.ActualWidth - 2 * FrameInset);
        double height = Math.Max(0, _frame.ActualHeight - 2 * FrameInset);
        double perimeter = 2 * (width + height) - (8 - 2 * Math.PI) * FrameCorner;
        foreach (Rectangle line in new[] { _edge, _chasers })
        {
            line.Width = width;
            line.Height = height;
            Canvas.SetLeft(line, FrameInset);
            Canvas.SetTop(line, FrameInset);
        }

        _edge.Visibility = _frameStyle == FrameMarquee ? Visibility.Collapsed : Visibility.Visible;
        _chasers.Visibility = _frameStyle == FrameChasers ? Visibility.Visible : Visibility.Collapsed;
        if (perimeter <= 0)
        {
            return;
        }

        if (_frameStyle == FrameChasers)
        {
            // Kesik çizgi uzunlukları çizgi kalınlığı biriminde; çevre köşelerin yayıyla.
            double units = perimeter / ChaserLine;
            double dash = units * ChaserShare;
            double gap = units / 2 - dash;
            _chasers.StrokeDashArray = [dash, gap, dash, gap];
            if (SystemParameters.ClientAreaAnimation)
            {
                _chasers.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -units, TimeSpan.FromSeconds(ChaseSeconds)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            return;
        }

        _chasers.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        bool bars = _frameStyle == FrameEqualizer;
        int count = (int)(perimeter / (bars ? BarSpacing : DotSpacing));
        for (int i = 0; i < count; i++)
        {
            double along = (i + 0.5) / count;
            (Point at, Vector inward) = PerimeterPoint(along * perimeter, width, height);
            Shape shape = bars
                ? new Line { Stroke = _accent, StrokeThickness = BarLine, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, X1 = at.X, Y1 = at.Y, X2 = at.X, Y2 = at.Y }
                : new Ellipse { Fill = _accent, Width = DotSize, Height = DotSize, Opacity = 0.16, RenderTransform = new ScaleTransform(1, 1, DotSize / 2, DotSize / 2) };
            if (!bars)
            {
                Canvas.SetLeft(shape, at.X - DotSize / 2);
                Canvas.SetTop(shape, at.Y - DotSize / 2);
            }
            _frame.Children.Add(shape);
            _parts.Add((shape, at, inward, along, 0));
        }

        if (bars)
        {
            _meter = AudioEndpoint.OpenMeter("Spotify");
        }
        if (SystemParameters.ClientAreaAnimation && !_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += Parts_Rendering;
        }
    }

    private void StopParts()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= Parts_Rendering;
        }
        foreach ((Shape shape, _, _, _, _) in _parts)
        {
            _frame.Children.Remove(shape);
        }
        _parts.Clear();
        _meter = null;
    }

    /// <summary>
    /// Her karede: ekolayzırda sol yarı sol kanal, sağ yarı sağ kanal; her çubuk kendi hızında salınan bir payla
    /// (tek düzey bütün çubukları aynı boyda tutardı), hızlı yükselip yavaş iner. Lunaparkta ışık başı kenarda döner,
    /// arkasındaki noktalar yavaşça söner.
    /// </summary>
    private void Parts_Rendering(object? sender, EventArgs e)
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        if (_frameStyle == FrameEqualizer)
        {
            // Ölçer sesin kendisini (uygulamanın sesi kısıksa küçük) verir: son tepelere göre ölçeklenir, tavan yavaş iner;
            // yoksa çubuklar müzik çalarken de kısa kalıyordu (ölçüldü).
            (float left, float right) = _meter?.Peaks() ?? (0, 0);
            _ceiling = Math.Max(Math.Max(left, right), Math.Max(MinCeiling, _ceiling * CeilingFall));
            left /= (float)_ceiling;
            right /= (float)_ceiling;
            _left += (left - _left) * (left > _left ? 0.5 : 0.12);
            _right += (right - _right) * (right > _right ? 0.5 : 0.12);
            double middle = _frame.ActualWidth / 2;
            for (int i = 0; i < _parts.Count; i++)
            {
                (Shape shape, Point at, Vector inward, double along, double value) = _parts[i];
                double side = at.X < middle ? _left : _right;
                double sway = 0.5 + 0.5 * Math.Sin(seconds * (6 + i % 7 * 1.3) + i * 1.7);
                double target = Math.Min(1, side) * (0.3 + 0.7 * sway);
                value += (target - value) * (target > value ? 0.45 : 0.15);
                double length = BarMin + value * (BarMax - BarMin);
                var line = (Line)shape;
                line.X2 = at.X + inward.X * length;
                line.Y2 = at.Y + inward.Y * length;
                _parts[i] = (shape, at, inward, along, value);
            }
            return;
        }

        double head = seconds / MarqueeSeconds % 1;
        foreach ((Shape shape, _, _, double along, _) in _parts)
        {
            double behind = (head - along + 1) % 1;
            double light = Math.Max(0, 1 - behind / MarqueeTail);
            shape.Opacity = 0.16 + 0.84 * light;
            var grow = (ScaleTransform)shape.RenderTransform;
            grow.ScaleX = grow.ScaleY = 1 + 0.5 * light;
        }
    }

    /// <summary>Yuvarlak köşeli kenarda, sol üst köşenin sonundan saat yönünde verilen uzaklıktaki nokta ve içe dönük yön.</summary>
    private static (Point At, Vector Inward) PerimeterPoint(double distance, double width, double height)
    {
        double r = FrameCorner;
        double top = width - 2 * r;
        double side = height - 2 * r;
        double arc = Math.PI * r / 2;
        double left = FrameInset;
        double upper = FrameInset;
        double[] segments = [top, arc, side, arc, top, arc, side, arc];
        int index = 0;
        while (index < segments.Length - 1 && distance > segments[index])
        {
            distance -= segments[index];
            index++;
        }

        (Point center, double startAngle) corner = index switch
        {
            1 => (new Point(left + width - r, upper + r), -90),
            3 => (new Point(left + width - r, upper + height - r), 0),
            5 => (new Point(left + r, upper + height - r), 90),
            _ => (new Point(left + r, upper + r), 180)
        };
        switch (index)
        {
            case 0:
                return (new Point(left + r + distance, upper), new Vector(0, 1));
            case 2:
                return (new Point(left + width, upper + r + distance), new Vector(-1, 0));
            case 4:
                return (new Point(left + width - r - distance, upper + height), new Vector(0, -1));
            case 6:
                return (new Point(left, upper + height - r - distance), new Vector(1, 0));
            default:
                double angle = (corner.startAngle + distance / arc * 90) * Math.PI / 180;
                var outward = new Vector(Math.Cos(angle), Math.Sin(angle));
                return (corner.center + outward * r, -outward);
        }
    }

    /// <summary>
    /// Satırın girişi, satırın kendi ritminden (kullanıcının seçimi, seçeneklerden A · Ritme göre; hepsi aynı hareketle
    /// gelmesin): tek kelime büyükten çarpar, hızlı satır (kelime başına 0,55 sn'den az) alttan maskeden kayar, ağır satır
    /// (1,1 sn'den çok) harf harf yükselir; ötekiler bulanıktan netleşir, maskeden kayar ya da yandan süzülür (metne göre,
    /// aynı satır hep aynı gelir). Kelime zamanı LRCLIB'de yok: ritim satırın süresinden.
    /// </summary>
    private enum Entrance { Rise, Slam, Mask, Drift, Chars, Soft }

    /// <summary>
    /// Şarkının kipi (kullanıcının isteği: rapte güzel, yavaş şarkıda fazla hareketli ve gereksiz vurgulu): sözlerin söylenme
    /// hızından, saniyedeki hece (sesli harf sayısı; Türkçe kelimeler uzun, kelime sayısı Türkçe rapi İngilizce popla
    /// karıştırıyordu). Spotify'ın tempo / enerji ucu yeni uygulamalara kapalı. Ölçülen: baladlar 1,75-2,33, Blinding
    /// Lights 2,7, rap ve hızlı pop 4,3-5,5.
    /// </summary>
    private enum Mood { Calm, Moderate, Lively }

    private const double CalmRate = 2.6;
    private const double LivelyRate = 3.8;
    private const string Vowels = "aeiouyıöüâêîôûàáèéìíòóùúäëïÿAEIOUYIÖÜÂÊÎÔÛÀÁÈÉÌÍÒÓÙÚÄËÏİ";
    private Mood _mood = Mood.Moderate;
    private IReadOnlyList<LyricLine>? _moodOf;
    private Mood _blockMood;

    private Mood MoodOf(IReadOnlyList<LyricLine> lines)
    {
        if (ReferenceEquals(lines, _moodOf))
        {
            return _mood;
        }

        _moodOf = lines;
        double syllables = 0;
        double seconds = 0;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            double length = Math.Min((lines[i + 1].At - lines[i].At).TotalSeconds, 8);
            if (lines[i].Text.Length == 0 || length <= 0.3)
            {
                continue;
            }
            syllables += lines[i].Text.Count(Vowels.Contains);
            seconds += length;
        }

        // Sözü az olan (ya da hiç olmayan) şarkıda orta kip.
        double rate = seconds > 10 ? syllables / seconds : double.NaN;
        _mood = double.IsNaN(rate) ? Mood.Moderate : rate < CalmRate ? Mood.Calm : rate > LivelyRate ? Mood.Lively : Mood.Moderate;
        if (!double.IsNaN(rate))
        {
            ManagedShell.Common.Logging.ShellLogger.Info($"LyricsStage: hece/sn {rate:0.00}, kip {_mood}");
        }
        return _mood;
    }

    private const double FastWordSeconds = 0.55;
    private const double SlowWordSeconds = 1.1;
    private const int CharStaggerMs = 35;
    private const int KeywordMinScore = 7;

    /// <summary>Vurgulanmayacak kısa ve sık kelimeler (Türkçe ve İngilizce).</summary>
    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "ve", "veya", "ama", "fakat", "bir", "bu", "şu", "o", "de", "da", "ki", "ne", "ben", "sen", "biz", "siz", "onlar",
        "beni", "seni", "bana", "sana", "benim", "senin", "bizi", "sizi", "mi", "mı", "mu", "mü", "ile", "için", "gibi",
        "her", "tek", "yine", "çok", "daha", "en", "kadar", "şimdi", "artık", "bile", "hep", "hiç", "nasıl", "neden",
        "the", "and", "you", "your", "with", "this", "that", "they", "them", "what", "when", "there", "here", "from",
        "have", "just", "like", "dont", "cant", "wont", "im", "its", "were", "will", "into", "yeah", "baby", "ooh"
    };

    private readonly Dictionary<string, int> _songWords = [];
    private IReadOnlyList<LyricLine>? _songWordsOf;

    /// <summary>Dergi dizgisi: vurgulu kelime büyük ve kalın, ötekiler küçük ve ince (boyları satırın temel boyuna göre).</summary>
    private const double BigScale = 1.25;
    private const double SmallScale = 0.62;
    private const double ModerateBigScale = 1.12;
    private const double ModerateSmallScale = 0.78;
    private const double ModerateRowUnits = 10;
    private const double CalmBigScale = 1.08;
    private const double CalmSmallScale = 0.86;
    private const int CalmStaggerMs = 450;
    private const int CalmLeaveMs = 700;
    private const int SoftMs = 1000;

    /// <summary>Bir satırın en çok genişliği, harf × boy biriminde (vurgusuz satırda eski poster gibi ~11 harf).</summary>
    private const double RowUnits = 9;
    private const double PlainRowUnits = 11;
    private const double GapUnits = 0.35;

    /// <summary>Yazının taban çizgisinin altındaki pay (boyun oranı): farklı boydaki kelimeler taban çizgisine oturur.</summary>
    private static readonly double Descent = Display.LineSpacing - Display.Baseline is > 0.05 and < 0.6 and var descent ? descent : 0.26;

    private int _blockVariant;

    /// <summary>
    /// Yeni satır (kullanıcının seçimi: seçeneklerden B · Dergi dizgisi, girişleri A · Ritme göre gibi satırın ritminden):
    /// satırın vurgulu kelimesi büyük ve kalın (eşiği geçerse kapağın renginde), ötekiler küçük ve ince; satırlar sola,
    /// ortaya ya da sağa yaslanır (metne göre, aynı satır hep aynı). Büyük kelime kendi vuruşuyla çarpar. Eski satır
    /// yukarı, sola ya da aşağı çekilerek söner. Vurgulanacak kelime yoksa eski poster gibi hepsi kalın ve ortada.
    /// </summary>
    private void ShowLine(LyricsView view)
    {
        if (_block is { } old)
        {
            _block = null;
            Leave(old, _blockVariant, _blockMood);
        }

        string text = view.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        List<string> words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        Mood mood = MoodOf(view.Lines);
        Entrance entrance = EntranceFor(text, words.Count, view.LineLength, mood);
        (int keyword, bool tinted) = view.Note == null ? KeywordOf(words, view.Lines, mood) : (-1, false);
        if (words.Count == 1 && view.Note == null)
        {
            keyword = 0;
        }

        // Kip dizginin sertliğini belirler: sakinde boy farkı küçük ve satırlar ortada, hareketlide en büyük.
        (double bigScale, double smallScale, double rowUnits) = mood switch
        {
            Mood.Calm => (CalmBigScale, CalmSmallScale, PlainRowUnits),
            Mood.Moderate => (ModerateBigScale, ModerateSmallScale, ModerateRowUnits),
            _ => (BigScale, SmallScale, RowUnits)
        };
        double Scale(int i) => keyword < 0 ? 1 : i == keyword ? bigScale : smallScale;
        List<List<int>> rows = Rows(words, Scale, keyword < 0 ? PlainRowUnits : rowUnits);
        double size = FontSizeFor(words, rows, Scale);
        int variant = text.Sum(letter => letter);
        HorizontalAlignment align = keyword < 0 || mood == Mood.Calm ? HorizontalAlignment.Center : (variant % (mood == Mood.Lively ? 3 : 2)) switch
        {
            0 => HorizontalAlignment.Center,
            1 => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Right
        };
        Color plain = ((SolidColorBrush)FindResource("LyricsStageText")).Color;
        double gap = size * 0.1;

        // Kelimeler satırın süresinin ilk yarısına yayılır (sakinde biraz daha uzun ve aralar geniş); çarpma hepsi birden.
        double stagger = entrance == Entrance.Slam ? 0 : mood == Mood.Calm
            ? Math.Min(CalmStaggerMs, view.LineLength.TotalMilliseconds * 0.55 / Math.Max(1, words.Count))
            : Math.Min(MaxStaggerMs, view.LineLength.TotalMilliseconds * 0.5 / Math.Max(1, words.Count));
        var block = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        int order = 0;
        foreach (List<int> row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(0, 0, 0, size * 0.02) };
            foreach (int i in row)
            {
                var begin = TimeSpan.FromMilliseconds(LeaveMs * 0.85 + order++ * stagger);
                var brush = new SolidColorBrush(plain);
                bool big = i == keyword;
                double wordSize = size * Scale(i);
                Entrance own = big && words.Count > 1 && mood == Mood.Lively ? Entrance.Slam : entrance;
                FontWeight weight = keyword < 0 ? mood == Mood.Calm ? FontWeights.SemiBold : FontWeights.Bold
                    : big ? FontWeights.Bold : mood == Mood.Lively ? FontWeights.Light : FontWeights.Normal;
                FrameworkElement element = CreateWord(words[i], wordSize, weight, brush, own, begin);
                element.VerticalAlignment = VerticalAlignment.Bottom;
                element.Margin = new Thickness(gap, 0, gap, -Descent * wordSize);
                line.Children.Add(element);
                if (big && tinted)
                {
                    // Vurgulu kelime yerine oturduktan sonra kapağın rengine döner.
                    TimeSpan settled = begin + TimeSpan.FromMilliseconds(own switch
                    {
                        Entrance.Chars => words[i].Length * CharStaggerMs + WordMs,
                        Entrance.Soft => SoftMs * 0.6,
                        _ => WordMs * 0.6
                    });
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        var tint = TimeSpan.FromMilliseconds(mood == Mood.Calm ? 900 : 500);
                        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(view.Accent, tint) { BeginTime = settled });
                    }
                    else
                    {
                        brush.Color = view.Accent;
                    }
                }
            }
            block.Children.Add(line);
        }
        _poster.Children.Add(block);
        _block = block;
        _blockVariant = variant;
        _blockMood = mood;
    }

    /// <summary>Eski satır söner; yönü satıra göre değişir (yukarı, sola, aşağı ve biraz küçülerek).</summary>
    private void Leave(StackPanel old, int variant, Mood mood)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(mood == Mood.Calm ? CalmLeaveMs : LeaveMs);
        if (mood == Mood.Calm)
        {
            variant = 0;
        }
        var move = new TranslateTransform();
        var shrink = new ScaleTransform(1, 1);
        old.RenderTransformOrigin = new Point(0.5, 0.5);
        old.RenderTransform = new TransformGroup { Children = { shrink, move } };
        switch (variant % 3)
        {
            case 1:
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -60, duration) { EasingFunction = ease });
                break;
            case 2:
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 30, duration) { EasingFunction = ease });
                shrink.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.96, duration) { EasingFunction = ease });
                shrink.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.96, duration) { EasingFunction = ease });
                break;
            default:
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, mood == Mood.Calm ? -18 : -46, duration) { EasingFunction = ease });
                break;
        }
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => _poster.Children.Remove(old);
        old.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Sakinde hep yavaş netleşme; ortada çarpma yalnızca tek kelimede, öteki satırlar netleşir ya da süzülür.</summary>
    private static Entrance EntranceFor(string text, int words, TimeSpan length, Mood mood)
    {
        if (mood == Mood.Calm)
        {
            return Entrance.Soft;
        }
        if (mood == Mood.Moderate)
        {
            return words <= 1 ? Entrance.Slam : text.Length % 2 == 0 ? Entrance.Rise : Entrance.Drift;
        }
        if (words <= 1)
        {
            return Entrance.Slam;
        }

        double perWord = length.TotalSeconds / words;
        if (perWord < FastWordSeconds)
        {
            return Entrance.Mask;
        }
        if (perWord > SlowWordSeconds)
        {
            return Entrance.Chars;
        }
        return (text.Length % 4) switch
        {
            1 => Entrance.Mask,
            3 => Entrance.Drift,
            _ => Entrance.Rise
        };
    }

    /// <summary>
    /// Vurgulu kelime (satırda bir; LRCLIB'de vurgu bilgisi yok): küçük ve sık kelimeler dışında, uzunluk + şarkıda başka
    /// satırda da geçiyorsa (nakarat) 4 + satır sonundaysa (çoğu zaman kafiye) 2. Büyük yazılır; puanı eşiği geçerse
    /// renklenir de (her satır renklenmesin: "bazı kelimeler"). Aday yoksa -1.
    /// </summary>
    private (int Index, bool Tinted) KeywordOf(List<string> words, IReadOnlyList<LyricLine> lines, Mood mood)
    {
        if (!ReferenceEquals(lines, _songWordsOf))
        {
            _songWordsOf = lines;
            _songWords.Clear();
            foreach (LyricLine line in lines)
            {
                foreach (string word in line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Bare).Distinct())
                {
                    _songWords[word] = _songWords.GetValueOrDefault(word) + 1;
                }
            }
        }

        int best = -1;
        int bestScore = 0;
        bool bestRepeats = false;
        for (int i = 0; i < words.Count; i++)
        {
            string bare = Bare(words[i]);
            if (bare.Length < 4 || SmallWords.Contains(bare))
            {
                continue;
            }

            bool repeats = _songWords.GetValueOrDefault(bare) > 1;
            int score = bare.Length + (repeats ? 4 : 0) + (i == words.Count - 1 ? 2 : 0);
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
                bestRepeats = repeats;
            }
        }

        // Sakin şarkıda renk yalnızca nakarat kelimesinde (kullanıcı: yavaş şarkıda gereksiz vurgu).
        return (best, bestScore >= KeywordMinScore && (mood != Mood.Calm || bestRepeats));
    }

    private static readonly System.Globalization.CultureInfo Turkish = new("tr-TR");

    private static string Bare(string word)
    {
        return new string(word.ToLower(Turkish).Where(char.IsLetter).ToArray());
    }

    private FrameworkElement CreateWord(string word, double size, FontWeight weight, Brush brush, Entrance entrance, TimeSpan begin)
    {
        bool animate = SystemParameters.ClientAreaAnimation;
        TextBlock Glyphs(string text) => new()
        {
            Text = text,
            FontFamily = Display,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush
        };

        switch (entrance)
        {
            case Entrance.Chars:
            {
                var letters = new StackPanel { Orientation = Orientation.Horizontal };
                System.Globalization.TextElementEnumerator parts = System.Globalization.StringInfo.GetTextElementEnumerator(word);
                int i = 0;
                while (parts.MoveNext())
                {
                    TextBlock letter = Glyphs(parts.GetTextElement());
                    letters.Children.Add(letter);
                    Rise(letter, size, begin + TimeSpan.FromMilliseconds(i++ * CharStaggerMs), blur: false);
                }
                return letters;
            }
            case Entrance.Mask:
            {
                // Kelime kendi kutusunun altından çıkar; kutu keser.
                TextBlock text = Glyphs(word);
                var slide = new TranslateTransform(0, animate ? size * 1.4 : 0);
                text.RenderTransform = slide;
                if (animate)
                {
                    slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(size * 1.4, 0, TimeSpan.FromMilliseconds(460))
                    {
                        BeginTime = begin,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                }
                return new Border { ClipToBounds = true, Child = text };
            }
            case Entrance.Slam:
            {
                TextBlock text = Glyphs(word);
                if (!animate)
                {
                    return text;
                }

                var scale = new ScaleTransform(1.9, 1.9);
                text.RenderTransform = scale;
                text.RenderTransformOrigin = new Point(0.5, 0.5);
                text.Opacity = 0;
                var grow = new DoubleAnimationUsingKeyFrames { BeginTime = begin, Duration = TimeSpan.FromMilliseconds(380) };
                grow.KeyFrames.Add(new EasingDoubleKeyFrame(0.96, KeyTime.FromPercent(0.7), new CubicEase { EasingMode = EasingMode.EaseOut }));
                grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { BeginTime = begin });
                return text;
            }
            case Entrance.Drift:
            {
                TextBlock text = Glyphs(word);
                if (!animate)
                {
                    return text;
                }

                var move = new TranslateTransform(-size * 0.5, 0);
                text.RenderTransform = move;
                text.Opacity = 0;
                var duration = TimeSpan.FromMilliseconds(520);
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-size * 0.5, 0, duration) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin });
                return text;
            }
            case Entrance.Soft:
            {
                TextBlock text = Glyphs(word);
                Rise(text, size, begin, blur: true, soft: true);
                return text;
            }
            default:
            {
                TextBlock text = Glyphs(word);
                Rise(text, size, begin, blur: true);
                return text;
            }
        }
    }

    /// <summary>Bulanıktan netleşip yukarı oturur (harf harf girişte bulanıklık yok: harf başına efekt pahalı).</summary>
    /// <param name="soft">Sakin şarkıda: daha uzun, daha az yol ve bulanıklık.</param>
    private static void Rise(TextBlock word, double size, TimeSpan begin, bool blur, bool soft = false)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        IEasingFunction ease = soft ? new SineEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(soft ? SoftMs : WordMs);
        var move = new TranslateTransform();
        word.RenderTransform = move;
        word.Opacity = 0;
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(size * (soft ? 0.12 : 0.28), 0, duration) { BeginTime = begin, EasingFunction = ease });
        word.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin });
        if (!blur)
        {
            return;
        }

        var effect = new BlurEffect { Radius = size * (soft ? 0.1 : 0.18), RenderingBias = RenderingBias.Performance };
        word.Effect = effect;
        var sharpen = new DoubleAnimation(effect.Radius, 0, duration) { BeginTime = begin, EasingFunction = ease };
        // Bitince efekt kalkar: yazı yeniden keskin ve ucuz çizilsin.
        sharpen.Completed += (_, _) => word.Effect = null;
        effect.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
    }

    /// <summary>Kelimeler kısa satırlara toplanır (genişlik harf × boy biriminde): poster gibi alt alta.</summary>
    private static List<List<int>> Rows(List<string> words, Func<int, double> scale, double limit)
    {
        var rows = new List<List<int>>();
        var row = new List<int>();
        double units = 0;
        for (int i = 0; i < words.Count; i++)
        {
            double width = words[i].Length * scale(i);
            if (row.Count > 0 && units + GapUnits + width > limit)
            {
                rows.Add(row);
                row = [];
                units = 0;
            }
            units += (row.Count > 0 ? GapUnits : 0) + width;
            row.Add(i);
        }
        if (row.Count > 0)
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Temel boy: en geniş satır ekranın genişliğine, satırların toplamı yüksekliğine sığsın; en büyük kelime ekranın
    /// %16'sını geçmesin. Üstte çerçeve, altta sıradaki satır ve şarkı için yer kalır.
    /// </summary>
    private double FontSizeFor(List<string> words, List<List<int>> rows, Func<int, double> scale)
    {
        double width = Math.Max(400, ActualWidth);
        double height = Math.Max(300, ActualHeight);
        double widest = rows.Max(row => row.Sum(i => words[i].Length * scale(i)) + (row.Count - 1) * GapUnits);
        double byWidth = width * 0.78 / (Math.Max(4, widest) * 0.58);
        double tall = rows.Sum(row => row.Max(scale) * 1.17);
        double byHeight = (height - PosterReserve) / tall;
        double largest = rows.Max(row => row.Max(scale));
        return Math.Min(height * 0.16 / largest, Math.Min(byWidth, byHeight));
    }

    private static void Fade(UIElement element, double to, int ms)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? ms : 0)));
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
