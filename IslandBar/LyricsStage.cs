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

    /// <param name="bounds">Ekranın tamamı, piksel.</param>
    public LyricsStage(System.Drawing.Rectangle bounds, Action close)
    {
        _bounds = bounds;
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

    /// <summary>Kenar çizgisi ve iki parça: parçalar çizginin üstünde sürekli döner (sürekli animasyon, kullanıcı istedi).</summary>
    private void LayoutFrame()
    {
        double width = Math.Max(0, _frame.ActualWidth - 2 * FrameInset);
        double height = Math.Max(0, _frame.ActualHeight - 2 * FrameInset);
        foreach (Rectangle line in new[] { _edge, _chasers })
        {
            line.Width = width;
            line.Height = height;
            Canvas.SetLeft(line, FrameInset);
            Canvas.SetTop(line, FrameInset);
        }

        // Kesik çizgi uzunlukları çizgi kalınlığı biriminde; çevre köşelerin yayıyla.
        double perimeter = 2 * (width + height) - (8 - 2 * Math.PI) * FrameCorner;
        double units = perimeter / ChaserLine;
        double dash = units * ChaserShare;
        double gap = units / 2 - dash;
        _chasers.StrokeDashArray = [dash, gap, dash, gap];
        if (SystemParameters.ClientAreaAnimation)
        {
            _chasers.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -units, TimeSpan.FromSeconds(ChaseSeconds)) { RepeatBehavior = RepeatBehavior.Forever });
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
