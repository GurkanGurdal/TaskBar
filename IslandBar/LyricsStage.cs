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

/// <summary>Sahnenin o anki hali: satır (anahtarı değişince yeni satır gelir) ve şarkı.</summary>
internal sealed record LyricsView(string Key, string Text, string Next, TimeSpan LineLength, string Title, string Artist,
    BitmapSource? Cover, Color Accent, string? Note, bool Playing);

/// <summary>
/// İkinci ekranda tam ekran sözler (kullanıcının tasarımı, seçeneklerden C · Poster): ekran kararır, kenarda ince
/// çerçeve ve onun üstünde iki karşılıklı parça kovalar (rengi kapaktan; gradyan ve parıltı yok). Satır kelimelere
/// bölünür, kısa satırlar halinde büyük ve alt alta dizilir; kelimeler sırayla bulanıktan netleşip yukarı oturur
/// (lyric klip gibi). Altta soluk sıradaki satır, sol altta kapak ve şarkı. Fare kıpırdayınca sağ üstte Kapat çıkar,
/// durunca imleç de gizlenir. Kelime zamanları LRCLIB'de yok: kelimeler satırın süresine yayılır.
/// </summary>
internal sealed class LyricsStage : Window
{
    private const double FrameInset = 16;
    private const double FrameCorner = 14;
    private const double FrameLine = 1.5;
    private const double ChaserLine = 3;
    private const double ChaserShare = 0.07;
    private const int ChaseSeconds = 16;
    private const int RowChars = 11;
    private const int WordMs = 380;
    private const int MaxStaggerMs = 300;
    private const int LeaveMs = 300;
    private const int IdleMs = 2500;
    private const double PosterReserve = 360;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    private readonly System.Drawing.Rectangle _bounds;
    private readonly Grid _root = new();
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
    public LyricsStage(System.Drawing.Rectangle bounds, int frameStyle, Action close)
    {
        _bounds = bounds;
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
        _poster.Margin = new Thickness(0, 0, 0, 90);

        _next = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 92),
            FontSize = 22,
            Foreground = (Brush)FindResource("LyricsStageDim"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _note = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 92),
            FontSize = 20,
            Foreground = (Brush)FindResource("LyricsStageMuted")
        };

        _cover = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(8), Background = _coverImage };
        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("LyricsStageText"), TextTrimming = TextTrimming.CharacterEllipsis };
        _artist = new TextBlock { FontSize = 13.5, Foreground = (Brush)FindResource("LyricsStageMuted"), TextTrimming = TextTrimming.CharacterEllipsis };
        var song = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(48, 0, 0, 44),
            Children = { _cover, new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Children = { _title, _artist } } }
        };

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
        _close.MouseLeftButtonUp += (_, _) => close();

        _root.Children.Add(_frame);
        _root.Children.Add(_poster);
        _root.Children.Add(_next);
        _root.Children.Add(_note);
        _root.Children.Add(song);
        _root.Children.Add(_close);
        Content = _root;

        _idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleMs) };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            Fade(_close, 0, 300);
            Cursor = Cursors.None;
        };
        MouseMove += (_, _) =>
        {
            Cursor = null;
            Fade(_close, 1, 150);
            _idle.Stop();
            _idle.Start();
        };
        Cursor = Cursors.None;
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
        }

        _coverImage.ImageSource = view.Cover;
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
        ShowLine(view.Text, view.LineLength);
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

    /// <summary>Eski poster yukarı kayıp söner; yenisi kelime kelime gelir.</summary>
    private void ShowLine(string text, TimeSpan length)
    {
        if (_block is { } old)
        {
            _block = null;
            var lift = new TranslateTransform();
            old.RenderTransform = lift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -46, TimeSpan.FromMilliseconds(LeaveMs)) { EasingFunction = ease });
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(LeaveMs)) { EasingFunction = ease };
            fade.Completed += (_, _) => _poster.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        List<List<string>> rows = Rows(text);
        double size = FontSizeFor(rows);
        var block = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var words = new List<TextBlock>();
        foreach (List<string> row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -size * 0.08, 0, -size * 0.08) };
            foreach (string word in row)
            {
                var block1 = new TextBlock
                {
                    Text = word,
                    FontFamily = Display,
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("LyricsStageText"),
                    Margin = new Thickness(size * 0.13, 0, size * 0.13, 0),
                    Opacity = 0,
                    RenderTransform = new TranslateTransform()
                };
                line.Children.Add(block1);
                words.Add(block1);
            }
            block.Children.Add(line);
        }
        _poster.Children.Add(block);
        _block = block;

        // Kelimeler satırın süresinin ilk yarısına yayılır; uzun satırda çok beklemesin.
        double stagger = Math.Min(MaxStaggerMs, length.TotalMilliseconds * 0.5 / Math.Max(1, words.Count));
        for (int i = 0; i < words.Count; i++)
        {
            Reveal(words[i], size, TimeSpan.FromMilliseconds(LeaveMs * 0.85 + i * stagger));
        }
    }

    private static void Reveal(TextBlock word, double size, TimeSpan begin)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            word.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(WordMs);
        var blur = new BlurEffect { Radius = size * 0.18, RenderingBias = RenderingBias.Performance };
        word.Effect = blur;
        ((TranslateTransform)word.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(size * 0.28, 0, duration) { BeginTime = begin, EasingFunction = ease });
        word.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin });
        var sharpen = new DoubleAnimation(blur.Radius, 0, duration) { BeginTime = begin, EasingFunction = ease };
        // Bitince efekt kalkar: yazı yeniden keskin ve ucuz çizilsin.
        sharpen.Completed += (_, _) => word.Effect = null;
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
    }

    /// <summary>Kelimeler kısa satırlara (en çok ~11 harf) toplanır: poster gibi alt alta.</summary>
    private static List<List<string>> Rows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        int length = 0;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (row.Count > 0 && length + 1 + word.Length > RowChars)
            {
                rows.Add(row);
                row = [];
                length = 0;
            }
            row.Add(word);
            length += (length > 0 ? 1 : 0) + word.Length;
        }
        if (row.Count > 0)
        {
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>En uzun satır ekranın genişliğine, satırların toplamı yüksekliğine sığacak boy.</summary>
    private double FontSizeFor(List<List<string>> rows)
    {
        double width = Math.Max(400, ActualWidth);
        double height = Math.Max(300, ActualHeight);
        int longest = rows.Max(row => row.Sum(word => word.Length) + row.Count - 1);
        double byWidth = width * 0.74 / (Math.Max(4, longest) * 0.58);

        // Bir satırın gerçek yüksekliği yazı boyunun ~1,33 katı, kenar payları çıkınca ~1,17; üstte çerçeve, altta
        // sıradaki satır ve şarkı için yer kalsın (uzun satırda poster sıradakinin üstüne biniyordu).
        double byHeight = (height - PosterReserve) / (rows.Count * 1.17);
        return Math.Min(height * 0.16, Math.Min(byWidth, byHeight));
    }

    private static void Fade(UIElement element, double to, int ms)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? ms : 0)));
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
