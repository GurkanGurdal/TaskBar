using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.AppBar;

namespace IslandBar;

/// <summary>
/// Dönüştür aracı (Alt + S tekerleği): adanın üstünde kare cam kart, dosyalar içine bırakılır (kullanıcının tasarımı;
/// seçenekler arasından A · Deste seçildi). Dosyalar üst üste yelpaze gibi durur, yanda "Ne yapılsın?" listesi açılır,
/// sonra "Orijinal dosyalar kalsın mı?" sorulur. Dönüşürken deste ortada halkaya dürülür, halka ilerlemeyi gösterir;
/// bitince yeni dosyalarla (orijinaller kaldıysa onlarla birlikte) yeniden açılır. Hayır denirse dönüşen dosyaların
/// orijinali Geri Dönüşüm Kutusu'na gider. Destedeki dosyalar dışarı sürüklenebilir. Kapat düğmesi ya da tekerlekteki
/// dilim kapatır; dışarı tıklamak kapatmaz (dosyalar Gezgin'den sürüklenir).
/// </summary>
internal sealed class ConvertCard : Window
{
    private const double ShadowMargin = 20;
    private const double Square = 132;
    private const double Corner = 15;
    private const double SideWidth = 214;
    private const double SideGap = 10;
    private const double BarGap = 10;
    private const double IconSize = 40;
    private const double DeckY = 54;
    private const double RingSize = 56;
    private const double RingThickness = 4;
    private const int MaxShown = 3;
    private const int CollapseMs = 260;
    private const int ExpandMs = 380;
    private const int StaggerMs = 40;
    private const int SpinMs = 1000;
    private const double SpinShare = 0.28;
    private const int MoveMs = 280;
    private const int OpenMs = 420;
    private const int OpenFadeMs = 150;
    private const int CloseMs = 190;
    private const double BudScale = 0.45;
    private const double BudDistance = 14;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    private enum Stage
    {
        Empty,
        Choose,
        Ask,
        Running,
        Done
    }

    private readonly System.Drawing.Rectangle _island;
    private readonly AppBarEdge _edge;
    private readonly GlassCard _square;
    private readonly GlassCard _side;
    private readonly StackPanel _sideContent = new();
    private readonly TranslateTransform _sideSlide = new();
    private readonly Image[] _icons = new Image[MaxShown];
    private readonly FrameworkElement _hint;
    private readonly TextBlock _caption;
    private readonly Border _dropFrame;
    private readonly Canvas _ring;
    private readonly System.Windows.Shapes.Path _arc;
    private readonly RotateTransform _spin = new();
    private readonly ScaleTransform _ringScale = new(0.5, 0.5);
    private readonly TextBlock _percent;
    private readonly List<string> _files = [];
    private Stage _stage;
    private ConversionTarget? _target;
    private Point? _pressedAt;
    private bool _draggingOut;
    private bool _closing;

    /// <summary>Çubuk gizli: kart adanın durduğu yerde; açıkken onun üstünde (şarkı kartı gibi).</summary>
    private bool _atBarPlace;
    private readonly ScaleTransform _budScale = new();
    private readonly TranslateTransform _budSlide = new();
    private readonly System.Diagnostics.Stopwatch _moveClock = new();
    private System.Drawing.Point _moveFrom;
    private bool _moving;

    /// <summary>Cam adadaki gibi arkası değişince tazelenir; adaya bakan kenar okunmaz.</summary>
    private readonly GlassWindowWatch _glassWatch;

    /// <param name="island">Ana ekrandaki adanın duracağı yer, ekran pikseli.</param>
    /// <param name="barHidden">Ada şu an ekran dışında (otomatik gizleme).</param>
    public ConvertCard(System.Drawing.Rectangle island, AppBarEdge edge, bool barHidden)
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
        AllowDrop = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)FindResource("UiFont");

        var layer = new Canvas { Width = Square, Height = Square, ClipToBounds = false };
        for (int i = MaxShown - 1; i >= 0; i--)
        {
            var icon = new Image
            {
                Width = IconSize,
                Height = IconSize,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new RotateTransform(), new TranslateTransform() } }
            };
            Canvas.SetLeft(icon, (Square - IconSize) / 2);
            Canvas.SetTop(icon, DeckY - IconSize / 2);
            layer.Children.Add(icon);
            _icons[i] = icon;
        }

        _arc = new System.Windows.Shapes.Path
        {
            // Halka boyunda: dönerken merkezi halkanın merkezi olsun, yayın kendi kutusu değil.
            Width = RingSize,
            Height = RingSize,
            Stroke = (Brush)FindResource("TextPrimary"),
            StrokeThickness = RingThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _spin
        };
        _percent = new TextBlock
        {
            Width = RingSize,
            TextAlignment = TextAlignment.Center,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary")
        };
        Canvas.SetTop(_percent, RingSize / 2 - 9);
        _ring = new Canvas
        {
            Width = RingSize,
            Height = RingSize,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _ringScale,
            Children =
            {
                new System.Windows.Shapes.Ellipse
                {
                    Width = RingSize,
                    Height = RingSize,
                    Stroke = (Brush)FindResource("RailTrack"),
                    StrokeThickness = RingThickness
                },
                _arc,
                _percent
            }
        };
        Canvas.SetLeft(_ring, (Square - RingSize) / 2);
        Canvas.SetTop(_ring, DeckY - RingSize / 2);
        layer.Children.Add(_ring);

        _hint = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children =
            {
                new TextBlock
                {
                    Text = "",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)FindResource("TextSecondary")
                },
                new TextBlock
                {
                    Text = "Dosyaları bırak",
                    Margin = new Thickness(0, 10, 0, 0),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextSecondary")
                }
            }
        };
        _caption = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 10),
            VerticalAlignment = VerticalAlignment.Bottom,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5,
            IsHitTestVisible = false,
            Foreground = (Brush)FindResource("TextSecondary")
        };
        _dropFrame = new Border
        {
            CornerRadius = new CornerRadius(Corner),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)FindResource("DropHighlight"),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        var face = new Grid { Background = Brushes.Transparent, Children = { layer, _hint, _caption, _dropFrame, CloseButton() } };
        face.MouseLeftButtonDown += Face_MouseLeftButtonDown;
        face.MouseMove += Face_MouseMove;
        face.MouseLeftButtonUp += (_, _) => _pressedAt = null;

        _square = new GlassCard
        {
            Width = Square,
            Height = Square,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Corner),
            Opacity = 0,
            AllowDrop = true,
            Content = face
        };
        _side = new GlassCard
        {
            Width = SideWidth,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(13),
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            RenderTransform = _sideSlide,
            Content = _sideContent
        };

        // Kart adanın üstündeyse yan kart ona alttan, altındaysa üstten hizalı; sağ kenardaki çubukta solda.
        VerticalAlignment align = edge switch
        {
            AppBarEdge.Top => VerticalAlignment.Top,
            AppBarEdge.Left or AppBarEdge.Right => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom
        };
        _square.VerticalAlignment = align;
        _side.VerticalAlignment = align;

        // Açılırken çubuktan küçük bir damla olarak çıkar: çubuğa bakan kenarın ortasından büyür.
        _square.RenderTransformOrigin = edge switch
        {
            AppBarEdge.Top => new Point(0.5, 0),
            AppBarEdge.Left => new Point(0, 0.5),
            AppBarEdge.Right => new Point(1, 0.5),
            _ => new Point(0.5, 1)
        };
        _square.RenderTransform = new TransformGroup { Children = { _budScale, _budSlide } };
        bool sideFirst = edge == AppBarEdge.Right;
        _side.Margin = sideFirst ? new Thickness(0, 0, SideGap, 0) : new Thickness(SideGap, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(sideFirst ? _side : _square);
        row.Children.Add(sideFirst ? _square : _side);
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = row };
        _glassWatch = new GlassWindowWatch(this, () => [_square, _side], edge);

        DragEnter += Card_DragOver;
        DragOver += Card_DragOver;
        DragLeave += (_, _) => _dropFrame.Visibility = Visibility.Collapsed;
        Drop += Card_Drop;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
            }
        };
        SizeChanged += (_, _) => Place();
        ShowStage(Stage.Empty);
    }

    public static ConvertCard? Current { get; private set; }

    /// <summary>Açılışın tersi: yan kart kareye çekilir, kare çubuğa doğru küçülüp kaybolur.</summary>
    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (Current == this)
        {
            // Kapanırken tekerlek yeniden açabilsin, gelen dosyalar giden karta eklenmesin.
            Current = null;
        }

        if (!SystemParameters.ClientAreaAnimation || _square.Opacity == 0 || !IsVisible)
        {
            Close();
            return;
        }

        IsHitTestVisible = false;
        var duration = TimeSpan.FromMilliseconds(CloseMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        if (_side.Visibility == Visibility.Visible)
        {
            Animate(_sideSlide, TranslateTransform.XProperty, _sideSlide.X, _edge == AppBarEdge.Right ? 10 : -10, TimeSpan.FromMilliseconds(140), TimeSpan.Zero, ease);
            Animate(_side, OpacityProperty, _side.Opacity, 0, TimeSpan.FromMilliseconds(140), TimeSpan.Zero, null);
        }

        Vector toward = TowardBar() * BudDistance;
        Animate(_budScale, ScaleTransform.ScaleXProperty, 1, BudScale, duration, TimeSpan.Zero, ease);
        Animate(_budScale, ScaleTransform.ScaleYProperty, 1, BudScale, duration, TimeSpan.Zero, ease);
        Animate(_budSlide, TranslateTransform.XProperty, 0, toward.X, duration, TimeSpan.Zero, ease);
        Animate(_budSlide, TranslateTransform.YProperty, 0, toward.Y, duration, TimeSpan.Zero, ease);
        var fade = new DoubleAnimation(_square.Opacity, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => Close();
        _square.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Çubuk gizlenip açılınca kart şarkı kartı gibi yer değiştirir: gizliyken adanın yerinde, açıkken üstünde.
    /// Pencere kayarak gider (içeriği kaydırmak pencerenin kenarında kesilirdi); bitince camın arkası yeniden alınır.
    /// </summary>
    public void FollowBar(bool barHidden)
    {
        if (_closing || barHidden == _atBarPlace || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        _atBarPlace = barHidden;
        if (!SystemParameters.ClientAreaAnimation || !GetWindowRect(source.Handle, out NativeRect rect))
        {
            Place();
            _ = RefreshVisibleGlassAsync();
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

    private void Move_Rendering(object? sender, EventArgs e)
    {
        if (_closing || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            StopMoving();
            return;
        }

        double t = Math.Clamp(_moveClock.Elapsed.TotalMilliseconds / MoveMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);
        System.Drawing.Point to = Spot(source);
        int x = (int)Math.Round(_moveFrom.X + (to.X - _moveFrom.X) * eased);
        int y = (int)Math.Round(_moveFrom.Y + (to.Y - _moveFrom.Y) * eased);
        SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
        if (t >= 1)
        {
            StopMoving();
            _ = RefreshVisibleGlassAsync();
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

    private async Task RefreshVisibleGlassAsync()
    {
        await _glassWatch.RefreshAsync(_square, isHidden: false);
        if (!_closing && _side.Visibility == Visibility.Visible && _side.Opacity > 0)
        {
            await _glassWatch.RefreshAsync(_side, isHidden: false);
        }
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

    /// <summary>Tekerlekteki dilime bırakılan ya da karta sürüklenen dosyalar.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (_stage == Stage.Running)
        {
            return;
        }

        // Biten işin üstüne yeni dosya: yeni iş.
        if (_stage == Stage.Done)
        {
            _files.Clear();
        }

        foreach (string path in paths.Where(File.Exists))
        {
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _files.Add(path);
            }
        }

        if (_files.Count > 0)
        {
            _ = ShowDeckAsync(fromCenter: false);
            ShowStage(Stage.Choose);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Current = this;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        await _square.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (!_closing)
        {
            Reveal();
        }
    }

    /// <summary>Çubuğun içinden küçük bir damla olarak çıkar, biraz geçip yerine oturur (şarkı kartının çıkışı gibi).</summary>
    private void Reveal()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _square.Opacity = 1;
            return;
        }

        Vector toward = TowardBar() * BudDistance;
        var duration = TimeSpan.FromMilliseconds(OpenMs);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        Animate(_budScale, ScaleTransform.ScaleXProperty, BudScale, 1, duration, TimeSpan.Zero, ease);
        Animate(_budScale, ScaleTransform.ScaleYProperty, BudScale, 1, duration, TimeSpan.Zero, ease);
        Animate(_budSlide, TranslateTransform.XProperty, toward.X, 0, duration, TimeSpan.Zero, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(_budSlide, TranslateTransform.YProperty, toward.Y, 0, duration, TimeSpan.Zero, new CubicEase { EasingMode = EasingMode.EaseOut });
        Animate(_square, OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(OpenFadeMs), TimeSpan.Zero, null);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMoving();
        base.OnClosed(e);
        if (Current == this)
        {
            Current = null;
        }
    }

    #region Stages
    private void ShowStage(Stage stage)
    {
        _stage = stage;
        _hint.Visibility = stage == Stage.Empty ? Visibility.Visible : Visibility.Collapsed;
        _sideContent.Children.Clear();
        switch (stage)
        {
            case Stage.Empty:
                _caption.Text = "";
                HideSide();
                return;
            case Stage.Choose:
                _caption.Text = Summary(_files);
                FillChoices();
                break;
            case Stage.Ask:
                _caption.Text = $"{Summary(_files)} → {_target!.Short}";
                FillQuestion();
                break;
            case Stage.Running:
                HideSide();
                return;
        }
        ShowSide();
    }

    private void FillChoices()
    {
        IReadOnlyList<ConversionTarget> targets = FileConversions.TargetsFor(_files);
        if (targets.Count == 0)
        {
            _sideContent.Children.Add(Note(_files.Count == 1 ? "Bu dosya dönüştürülemiyor" : "Bu dosyaların hepsine uyan dönüştürme yok", false));
        }
        else
        {
            _sideContent.Children.Add(Note("Ne yapılsın?", false, muted: true));
            foreach (ConversionTarget target in targets)
            {
                _sideContent.Children.Add(Row(target.Label, () =>
                {
                    _target = target;
                    ShowStage(Stage.Ask);
                }));
            }
        }
        _sideContent.Children.Add(Row("Temizle", Clear, muted: true));
    }

    private void FillQuestion()
    {
        _sideContent.Children.Add(Note(_files.Count == 1 ? "Orijinal dosya kalsın mı?" : "Orijinal dosyalar kalsın mı?", false));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 4) };
        buttons.Children.Add(Choice("Evet, kalsın", primary: true, () => Run(keepOriginals: true)));
        buttons.Children.Add(Choice("Hayır, sil", primary: false, () => Run(keepOriginals: false)));
        _sideContent.Children.Add(buttons);
        _sideContent.Children.Add(Note("Silinenler Geri Dönüşüm Kutusu'na gider.", false, muted: true));
        _sideContent.Children.Add(Row("Geri", () => ShowStage(Stage.Choose), muted: true));
    }

    private async void Run(bool keepOriginals)
    {
        var job = new ConversionJob(_files.ToList(), _target!, keepOriginals);
        ShowStage(Stage.Running);
        _caption.Text = "Dönüştürülüyor";
        Collapse();

        await FileConverter.RunAsync(job, () => SetProgress(job.Progress));
        if (_closing)
        {
            return;
        }

        // Yeni dosyalar önde; dönüşmeyen ya da korunan orijinaller arkada.
        List<string> result = job.Converted.Select(pair => pair.Output)
            .Concat(_files.Where(file => File.Exists(file)))
            .ToList();
        _files.Clear();
        _files.AddRange(result);
        await ExpandAsync();
        if (_closing)
        {
            return;
        }

        _stage = Stage.Done;
        _caption.Text = Summary(_files);
        _sideContent.Children.Clear();
        if (job.FailedCount > 0)
        {
            _sideContent.Children.Add(Note(job.Converted.Count == 0
                ? $"Dönüştürülemedi: {job.Problem}"
                : $"{job.FailedCount} dosya dönüştürülemedi: {job.Problem}", attention: true));
        }
        if (job.Converted.Count > 0)
        {
            string first = job.Converted[0].Output;
            _sideContent.Children.Add(Row("Klasörde göster", () => FileConverter.Reveal(first)));
        }
        if (_files.Count > 0)
        {
            _sideContent.Children.Add(Row("Başka işlem yap", () => ShowStage(Stage.Choose)));
        }
        _sideContent.Children.Add(Row("Temizle", Clear, muted: true));
        ShowSide();
    }

    private void Clear()
    {
        _files.Clear();
        foreach (Image icon in _icons)
        {
            icon.BeginAnimation(OpacityProperty, null);
            icon.Opacity = 0;
        }
        ShowStage(Stage.Empty);
    }

    /// <summary>"rapor.pdf"; birden çoksa türlerine göre "2 Word · 2 PDF", ikiden çok türde "4 dosya" (sığmıyordu).</summary>
    private static string Summary(IReadOnlyList<string> files)
    {
        if (files.Count == 1)
        {
            return Path.GetFileName(files[0]);
        }

        List<IGrouping<string, string>> kinds = files.GroupBy(KindName).ToList();
        return kinds.Count > 2 ? $"{files.Count} dosya" : string.Join(" · ", kinds.Select(group => $"{group.Count()} {group.Key}"));
    }

    private static string KindName(string path)
    {
        if (Directory.Exists(path))
        {
            return "klasör";
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".doc" or ".docx" => "Word",
            ".xls" or ".xlsx" => "Excel",
            ".ppt" or ".pptx" => "PowerPoint",
            ".jpeg" or ".jpg" => "JPEG",
            "" => "dosya",
            var extension => extension[1..].ToUpperInvariant()
        };
    }
    #endregion

    #region Side card
    private async void ShowSide()
    {
        bool wasHidden = _side.Visibility != Visibility.Visible || _side.Opacity == 0;
        _side.Visibility = Visibility.Visible;
        UpdateLayout();
        Place();
        await _glassWatch.RefreshAsync(_side, isHidden: wasHidden);
        if (_closing || _side.Visibility != Visibility.Visible || !wasHidden)
        {
            return;
        }

        _side.Opacity = 1;
        if (SystemParameters.ClientAreaAnimation)
        {
            // Solma yerine kayma (kullanıcının tercihi): kareden yana çıkar.
            double from = _edge == AppBarEdge.Right ? 10 : -10;
            _sideSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void HideSide()
    {
        _side.Opacity = 0;
        _side.Visibility = Visibility.Collapsed;
    }

    private Border Row(string text, Action chosen, bool muted = false)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 6, 9, 6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = (Brush)FindResource(muted ? "TextSecondary" : "TextPrimary")
            }
        };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("HoverBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => chosen();
        return row;
    }

    private Border Choice(string text, bool primary, Action chosen)
    {
        Brush rest = (Brush)FindResource(primary ? "AccentFill" : "MutedFill");
        var choice = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = rest,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = (Brush)FindResource(primary ? "AccentInk" : "TextPrimary")
            }
        };
        choice.MouseEnter += (_, _) => choice.Opacity = 0.85;
        choice.MouseLeave += (_, _) => choice.Opacity = 1;
        choice.MouseLeftButtonUp += (_, _) => chosen();
        return choice;
    }

    private TextBlock Note(string text, bool attention, bool muted = false)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(9, 5, 9, 5),
            TextWrapping = TextWrapping.Wrap,
            FontSize = muted ? 11.5 : 12.5,
            Foreground = (Brush)FindResource(attention ? "AttentionFill" : muted ? "TextSecondary" : "TextPrimary")
        };
    }

    private Border CloseButton()
    {
        var close = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 6, 6, 0),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Kapat",
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondary")
            }
        };
        close.MouseEnter += (_, _) => close.Background = (Brush)FindResource("HoverBrush");
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, _) => Dismiss();
        return close;
    }
    #endregion

    #region Deck and ring
    /// <summary>Destedeki yer: en üstteki ortada, ötekiler iki yana yatık.</summary>
    private static (double X, double Y, double Angle) DeckSpot(int index, int count)
    {
        if (count == 1)
        {
            return (0, 0, 0);
        }
        if (count == 2)
        {
            return index == 0 ? (6, 0, 6) : (-6, 2, -6);
        }
        return index switch
        {
            0 => (0, -2, 0),
            1 => (-13, 3, -12),
            _ => (13, 3, 12)
        };
    }

    /// <summary>İlk üç dosyanın ikonu desteye; ortadan (dönüşüm bitince) ya da hafif küçükten gelir.</summary>
    private async Task ShowDeckAsync(bool fromCenter)
    {
        int count = Math.Min(_files.Count, MaxShown);
        ImageSource?[] images = await Task.WhenAll(_files.Take(count).Select(path => AppIcon.LoadAsync(path)));
        if (_closing)
        {
            return;
        }

        for (int i = 0; i < MaxShown; i++)
        {
            Image icon = _icons[i];
            if (i >= count)
            {
                icon.BeginAnimation(OpacityProperty, null);
                icon.Opacity = 0;
                continue;
            }

            icon.Source = images[i];
            (double x, double y, double angle) = DeckSpot(i, count);
            var duration = TimeSpan.FromMilliseconds(fromCenter ? ExpandMs : 240);
            var begin = TimeSpan.FromMilliseconds(fromCenter ? i * StaggerMs : 0);
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
            (ScaleTransform scale, RotateTransform rotate, TranslateTransform move) = Parts(icon);
            double startScale = fromCenter ? 0.3 : icon.Opacity > 0 ? scale.ScaleX : 0.7;
            Animate(scale, ScaleTransform.ScaleXProperty, startScale, 1, duration, begin, ease);
            Animate(scale, ScaleTransform.ScaleYProperty, startScale, 1, duration, begin, ease);
            Animate(rotate, RotateTransform.AngleProperty, fromCenter ? 0 : rotate.Angle, angle, duration, begin, ease);
            Animate(move, TranslateTransform.XProperty, fromCenter ? 0 : move.X, x, duration, begin, ease);
            Animate(move, TranslateTransform.YProperty, fromCenter ? 0 : move.Y, y, duration, begin, ease);
            Animate(icon, OpacityProperty, fromCenter ? 0 : icon.Opacity, 1, TimeSpan.FromMilliseconds(160), begin, null);
        }
    }

    /// <summary>Deste ortada küçülüp kaybolur, yerinde halka büyür.</summary>
    private void Collapse()
    {
        var duration = TimeSpan.FromMilliseconds(CollapseMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        for (int i = 0; i < MaxShown; i++)
        {
            Image icon = _icons[i];
            (ScaleTransform scale, RotateTransform rotate, TranslateTransform move) = Parts(icon);
            var begin = TimeSpan.FromMilliseconds(i * StaggerMs / 2);
            Animate(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, 0.3, duration, begin, ease);
            Animate(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, 0.3, duration, begin, ease);
            Animate(rotate, RotateTransform.AngleProperty, rotate.Angle, 0, duration, begin, ease);
            Animate(move, TranslateTransform.XProperty, move.X, 0, duration, begin, ease);
            Animate(move, TranslateTransform.YProperty, move.Y, 0, duration, begin, ease);
            Animate(icon, OpacityProperty, icon.Opacity, 0, duration, begin, null);
        }

        var ringBegin = TimeSpan.FromMilliseconds(CollapseMs * 0.6);
        var grow = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        Animate(_ringScale, ScaleTransform.ScaleXProperty, 0.5, 1, TimeSpan.FromMilliseconds(320), ringBegin, grow);
        Animate(_ringScale, ScaleTransform.ScaleYProperty, 0.5, 1, TimeSpan.FromMilliseconds(320), ringBegin, grow);
        Animate(_ring, OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(180), ringBegin, null);
    }

    /// <summary>Halka küçülüp kaybolur, yeni deste ortadan açılır.</summary>
    private async Task ExpandAsync()
    {
        SetProgress(1);
        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Animate(_ringScale, ScaleTransform.ScaleXProperty, 1, 0.5, duration, TimeSpan.Zero, ease);
        Animate(_ringScale, ScaleTransform.ScaleYProperty, 1, 0.5, duration, TimeSpan.Zero, ease);
        Animate(_ring, OpacityProperty, 1, 0, duration, TimeSpan.Zero, null);
        await Task.Delay(SystemParameters.ClientAreaAnimation ? 150 : 0);
        _spin.BeginAnimation(RotateTransform.AngleProperty, null);
        await ShowDeckAsync(fromCenter: true);
    }

    /// <summary>Halkanın dolusu; null'da kısa bir yay döner.</summary>
    private void SetProgress(double? progress)
    {
        if (progress is { } value)
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin.Angle = 0;
            _arc.Data = Arc(Math.Clamp(value, 0, 1));
            _percent.Text = $"%{Math.Round(Math.Clamp(value, 0, 1) * 100)}";
            return;
        }

        _percent.Text = "";
        _arc.Data = Arc(SpinShare);
        if (SystemParameters.ClientAreaAnimation && _spin.HasAnimatedProperties == false)
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(SpinMs)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    /// <summary>Tepeden saat yönünde yay; halkanın çizgisinin ortasından geçer.</summary>
    private static Geometry Arc(double fraction)
    {
        double radius = (RingSize - RingThickness) / 2;
        var center = new Point(RingSize / 2, RingSize / 2);
        if (fraction >= 0.999)
        {
            return new EllipseGeometry(center, radius, radius);
        }

        double angle = 2 * Math.PI * Math.Max(fraction, 0.001) - Math.PI / 2;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    private static (ScaleTransform, RotateTransform, TranslateTransform) Parts(Image icon)
    {
        var group = (TransformGroup)icon.RenderTransform;
        return ((ScaleTransform)group.Children[0], (RotateTransform)group.Children[1], (TranslateTransform)group.Children[2]);
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, TimeSpan duration, TimeSpan begin, IEasingFunction? ease)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(from, to, duration) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }

    private static void Animate(UIElement target, DependencyProperty property, double from, double to, TimeSpan duration, TimeSpan begin, IEasingFunction? ease)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(from, to, duration) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd });
    }
    #endregion

    #region Dragging
    private void Card_DragOver(object sender, DragEventArgs e)
    {
        bool can = !_draggingOut && _stage != Stage.Running && BarWindow.SetDropEffect(e);
        if (!can)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
        _dropFrame.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        _dropFrame.Visibility = Visibility.Collapsed;
        if (!_draggingOut && _stage != Stage.Running && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
            e.Handled = true;
        }
    }

    private void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = _files.Count > 0 && _stage is Stage.Choose or Stage.Done ? e.GetPosition(this) : null;
    }

    /// <summary>Destedeki dosyalar dışarı: Gezgin gerçek dosya gibi davranır (Dosya rafı gibi kopyala ya da taşı).</summary>
    private void Face_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not { } pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - pressed;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedAt = null;
        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, _files.ToArray()), DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _draggingOut = false;
        }

        // Taşınan dosya artık burada değil.
        _files.RemoveAll(path => !File.Exists(path) && !Directory.Exists(path));
        if (_files.Count == 0)
        {
            Clear();
            return;
        }

        _caption.Text = Summary(_files);
        _ = ShowDeckAsync(fromCenter: false);
    }
    #endregion

    private void Place()
    {
        // Kayarken yeri her karede Spot'tan alınıyor.
        if (_moving || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        System.Drawing.Point spot = Spot(source);
        SetWindowPos(source.Handle, IntPtr.Zero, spot.X, spot.Y, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    /// <summary>
    /// Pencerenin sol üstü. Kare adanın ortasının üstünde (çubuk üstteyse altında, yandaysa yanında); çubuk gizliyken
    /// adanın durduğu yerde, kenarı adanınkiyle hizalı. Yatayda çalışma alanında kalır.
    /// </summary>
    private System.Drawing.Point Spot(HwndSource source)
    {
        double scale = source.CompositionTarget.TransformToDevice.M11;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        int shadow = (int)Math.Round(ShadowMargin * scale);
        int gap = (int)Math.Round(BarGap * scale);
        int square = (int)Math.Round(Square * scale);
        int centerX = _island.Left + _island.Width / 2;
        int centerY = _island.Top + _island.Height / 2;
        (int x, int y) = (_edge, _atBarPlace) switch
        {
            (AppBarEdge.Top, true) => (centerX - square / 2 - shadow, _island.Top - shadow),
            (AppBarEdge.Top, false) => (centerX - square / 2 - shadow, _island.Bottom + gap - shadow),
            (AppBarEdge.Left, true) => (_island.Left - shadow, centerY - height / 2),
            (AppBarEdge.Left, false) => (_island.Right + gap - shadow, centerY - height / 2),
            (AppBarEdge.Right, true) => (_island.Right - width + shadow, centerY - height / 2),
            (AppBarEdge.Right, false) => (_island.Left - gap - width + shadow, centerY - height / 2),
            (_, true) => (centerX - square / 2 - shadow, _island.Bottom - height + shadow),
            _ => (centerX - square / 2 - shadow, _island.Top - gap - height + shadow)
        };

        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(centerX, centerY)).WorkingArea;
        x = Math.Clamp(x, area.Left - shadow, Math.Max(area.Left - shadow, area.Right - width + shadow));
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
}
