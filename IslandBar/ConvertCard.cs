using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ManagedShell.AppBar;

namespace IslandBar;

/// <summary>
/// Dönüştür aracı (Alt + S tekerleği): adanın üstünde kare cam kutu (kullanıcının tasarımı, seçeneklerden C · Çekmece).
/// Dosyalar kutuya bırakılır (kopyalanır gibi; kullanıcının dosyasına hiç dokunulmaz), üst üste yelpaze gibi durur, yanda
/// "Ne yapılsın?" listesi açılır. İşlem seçilince kutu çekmece gibi çubuğun içine kayar, dışarıda tutamaklı ön yüzü ve
/// ilerleme çizgisi kalır; bitince yeni dosyalarla dışarı kayar. Kutuda yalnızca dönüşmüş dosyalar durur (IslandBar'ın
/// kendi klasöründe, <see cref="FileConverter.Root"/>); tutup sürüklenince bırakılan yere taşınır. Kutu içinde dosya varken
/// kapatılırsa onlar Geri Dönüşüm Kutusu'na gider (kullanıcının seçimi). Kapat düğmesi ya da tekerlekteki dilim kapatır;
/// dışarı tıklamak kapatmaz (dosyalar Gezgin'den sürüklenir).
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
    private const int MaxShown = 3;
    private const int ExpandMs = 380;
    private const int StaggerMs = 40;
    private const double FrontSize = 44;
    private const double TrackInset = 16;
    private const double SweepWidth = 30;
    private const int SweepMs = 1100;
    private const int DrawerCloseMs = 420;
    private const int DrawerOpenMs = 520;
    private const int IconHideMs = 140;
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
    private readonly Border _closeButton;
    private readonly List<string> _files = [];
    private Stage _stage;
    private Point? _pressedAt;
    private bool _draggingOut;
    private DragDropEffects _dragOutEffect;
    private readonly ShellDrag.TargetImage _dragImage;
    private bool _closing;

    /// <summary>Çubuk gizli: kart adanın durduğu yerde; açıkken onun üstünde (şarkı kartı gibi).</summary>
    private bool _atBarPlace;
    private readonly ScaleTransform _budScale = new();
    private readonly TranslateTransform _budSlide = new();
    private readonly Stopwatch _moveClock = new();
    private System.Drawing.Point _moveFrom;
    private bool _moving;

    /// <summary>Çekmecenin ön yüzü: kutu çubuğa girince görünen kısım; tutamak, ne olduğu ve ilerleme.</summary>
    private readonly FrameworkElement _front;
    private readonly TextBlock _frontLabel;
    private readonly Border _fill;
    private readonly TranslateTransform _sweep = new();
    private readonly TranslateTransform _drawer = new();
    private readonly Stopwatch _drawerClock = new();
    private double _drawerFrom;
    private double _drawerTo;
    private double _drawerValue;
    private int _drawerMs;
    private Func<double, double> _drawerEase = t => t;
    private TaskCompletionSource? _drawerDone;
    private bool _drawerMoving;

    /// <summary>
    /// Masaüstünde ada iki saat damlasına daralmış: kutunun arkasına gireceği geniş ada yok, düz kesik açıkta kalırdı
    /// (kullanıcı gördü). O zaman kapalı çekmece köşeleri yuvarlak küçük bir parça olur, saatin üstünde aynı boşlukla durur.
    /// </summary>
    private bool _barIsChip;

    /// <summary>Cam adadaki gibi arkası değişince tazelenir; adaya bakan kenar okunmaz.</summary>
    private readonly GlassWindowWatch _glassWatch;

    /// <param name="island">Ana ekrandaki adanın duracağı yer, ekran pikseli.</param>
    /// <param name="barHidden">Ada şu an ekran dışında (otomatik gizleme).</param>
    /// <param name="barIsChip">Ada masaüstünde saat damlalarına daralmış.</param>
    public ConvertCard(System.Drawing.Rectangle island, AppBarEdge edge, bool barHidden, bool barIsChip)
    {
        _island = island;
        _edge = edge;
        _atBarPlace = barHidden;
        _barIsChip = barIsChip;
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

        _frontLabel = new TextBlock
        {
            Margin = new Thickness(8, 15, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondary")
        };
        _fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
            CornerRadius = new CornerRadius(1.5),
            Background = (Brush)FindResource("AccentFill"),
            RenderTransform = _sweep
        };
        _front = CreateFront();
        _closeButton = CloseButton();

        var face = new Grid { Background = Brushes.Transparent, Children = { layer, _hint, _caption, _front, _dropFrame, _closeButton } };
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
        _square.RenderTransform = new TransformGroup { Children = { _budScale, _budSlide, _drawer } };
        bool sideFirst = edge == AppBarEdge.Right;
        _side.Margin = sideFirst ? new Thickness(0, 0, SideGap, 0) : new Thickness(SideGap, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(sideFirst ? _side : _square);
        row.Children.Add(sideFirst ? _square : _side);
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = row };
        _glassWatch = new GlassWindowWatch(this, () => [_square, _side], edge);

        // Sürükleme görüntüsünü (ikonlar) kutunun üstünde de kabuk çizer, içeri de dışarı da.
        _dragImage = new ShellDrag.TargetImage(this);
        DragEnter += (sender, e) =>
        {
            Card_DragOver(sender, e);
            _dragImage.Enter(e);
        };
        DragOver += (sender, e) =>
        {
            Card_DragOver(sender, e);
            _dragImage.Over(e);
        };
        DragLeave += (_, _) =>
        {
            _dropFrame.Visibility = Visibility.Collapsed;
            _dragImage.Leave();
        };
        Drop += (sender, e) =>
        {
            Card_Drop(sender, e);
            _dragImage.Drop(e);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
            }
        };
        SizeChanged += (_, _) => Place();
        FileConverter.RemoveEmptyFolders();
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

    /// <summary>Ada saat damlalarına daraldı ya da yeniden açıldı; kapalı çekmecenin şekli ona uyar.</summary>
    public void SetBarIsChip(bool chip)
    {
        if (chip == _barIsChip)
        {
            return;
        }

        _barIsChip = chip;
        if (_drawerValue > 0 && !_drawerMoving)
        {
            SetDrawer(_drawerValue);
        }
    }

    /// <summary>Tekerlekteki dilime bırakılan ya da karta sürüklenen dosyalar; kutudakilere eklenir.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        if (_stage == Stage.Running)
        {
            return;
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
        StopDrawer();
        base.OnClosed(e);
        if (Current == this)
        {
            Current = null;
        }

        // Kutuda bırakılan dönüşümler (kullanıcının seçimi): Geri Dönüşüm Kutusu.
        FileConverter.Recycle(_files.Where(FileConverter.IsOurs).Where(File.Exists));
        FileConverter.RemoveEmptyFolders();
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
                _sideContent.Children.Add(Row(target.Label, () => Run(target)));
            }
        }
        _sideContent.Children.Add(Row("Temizle", Clear, muted: true));
    }

    /// <summary>İşlem seçildi: kutu çekmece gibi çubuğa girer, bitince yeni dosyalarla çıkar.</summary>
    private async void Run(ConversionTarget target)
    {
        var job = new ConversionJob(_files.ToList(), target);
        ShowStage(Stage.Running);
        _frontLabel.Text = target.Short;
        await CloseDrawerAsync();
        if (_closing)
        {
            return;
        }

        await FileConverter.RunAsync(job, () => SetProgress(job.Progress, target));
        if (_closing)
        {
            return;
        }

        // Kutuda yalnızca dönüşmüş haller (kullanıcının seçimi); dönüşmeyen bizim dosyalarımız da kalır, kullanıcınınkiler
        // zaten yerinde.
        HashSet<string> converted = job.Converted.Select(pair => pair.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> result = job.Converted.Select(pair => pair.Output).Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(_files.Where(file => !converted.Contains(file) && FileConverter.IsOurs(file) && File.Exists(file)))
            .ToList();
        _files.Clear();
        _files.AddRange(result);
        SetProgress(1, target);
        await OpenDrawerAsync();
        if (_closing)
        {
            return;
        }

        _stage = Stage.Done;
        _hint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _caption.Text = Summary(_files);
        _sideContent.Children.Clear();
        if (job.FailedCount > 0)
        {
            _sideContent.Children.Add(Note(job.Converted.Count == 0
                ? $"Dönüştürülemedi: {job.Problem}"
                : $"{job.FailedCount} dosya dönüştürülemedi: {job.Problem}", attention: true));
        }
        if (_files.Count > 0)
        {
            _sideContent.Children.Add(Note(_files.Count == 1 ? "Dosyayı kutudan tutup istediğin yere sürükle." : "Dosyaları kutudan tutup istediğin yere sürükle.", false, muted: true));
            _sideContent.Children.Add(Row("Başka işlem yap", () => ShowStage(Stage.Choose)));
        }
        _sideContent.Children.Add(Row("Temizle", Clear, muted: true));
        ShowSide();
        if (_files.Count == 0)
        {
            _stage = Stage.Empty;
        }
    }

    /// <summary>Kutu boşalır; içindeki dönüşümler Geri Dönüşüm Kutusu'na gider, kullanıcının dosyalarına dokunulmaz.</summary>
    private void Clear()
    {
        FileConverter.Recycle(_files.Where(FileConverter.IsOurs).Where(File.Exists));
        FileConverter.RemoveEmptyFolders();
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

    #region Drawer
    /// <summary>
    /// Ön yüz: kutunun çubuğa uzak kenarında. Yatay çubukta tutamak, ne yapıldığı ve ilerleme çizgisi; yan çubukta aynı
    /// şerit dik çevrilir.
    /// </summary>
    private FrameworkElement CreateFront()
    {
        var track = new Border
        {
            Height = 3,
            Margin = new Thickness(TrackInset, 0, TrackInset, 9),
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(1.5),
            Background = (Brush)FindResource("RailTrack"),
            ClipToBounds = true,
            Child = _fill
        };
        var handle = new Border
        {
            Width = 28,
            Height = 3,
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(1.5),
            Background = (Brush)FindResource("TextSecondary"),
            Opacity = 0.6
        };
        var front = new Grid
        {
            Width = Square,
            Height = FrontSize,
            Opacity = 0,
            IsHitTestVisible = false,
            Children = { handle, _frontLabel, track }
        };
        switch (_edge)
        {
            case AppBarEdge.Top:
                front.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case AppBarEdge.Left:
                front.HorizontalAlignment = HorizontalAlignment.Right;
                front.LayoutTransform = new RotateTransform(90);
                break;
            case AppBarEdge.Right:
                front.HorizontalAlignment = HorizontalAlignment.Left;
                front.LayoutTransform = new RotateTransform(-90);
                break;
            default:
                front.VerticalAlignment = VerticalAlignment.Top;
                break;
        }
        return front;
    }

    /// <summary>Deste ve yazılar kaybolur, kutu ön yüzü kalana dek çubuğa kayar; sonra cam o yerin arkasını alır.</summary>
    private async Task CloseDrawerAsync()
    {
        _pressedAt = null;
        _closeButton.Visibility = Visibility.Collapsed;
        _caption.Visibility = Visibility.Collapsed;
        var hide = TimeSpan.FromMilliseconds(IconHideMs);
        foreach (Image icon in _icons)
        {
            Animate(icon, OpacityProperty, icon.Opacity, 0, hide, TimeSpan.Zero, null);
        }
        Animate(_front, OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(DrawerCloseMs), TimeSpan.FromMilliseconds(DrawerCloseMs / 3), null);
        await MoveDrawerAsync(Square - FrontSize, DrawerCloseMs, EaseInOut);
        if (_closing)
        {
            return;
        }

        _square.CaptureShift = TowardBar() * _drawerValue;
        await _glassWatch.RefreshAsync(_square, isHidden: false);
    }

    /// <summary>Çekmece yaylanarak dışarı kayar; yeni deste ortadan açılır.</summary>
    private async Task OpenDrawerAsync()
    {
        _sweep.BeginAnimation(TranslateTransform.XProperty, null);
        Animate(_front, OpacityProperty, 1, 0, TimeSpan.FromMilliseconds(DrawerOpenMs / 2), TimeSpan.Zero, null);
        _square.CaptureShift = default;
        await MoveDrawerAsync(0, DrawerOpenMs, EaseOutBack);
        if (_closing)
        {
            return;
        }

        _closeButton.Visibility = Visibility.Visible;
        _caption.Visibility = Visibility.Visible;
        _ = _glassWatch.RefreshAsync(_square, isHidden: false);
        await ShowDeckAsync(fromCenter: true);
    }

    /// <summary>
    /// Çekmece her karede ilerler: kayma ve onu çubuğun çizgisinde kesen kırpma birlikte değişir (kırpma kartın kendi
    /// koordinatında, dönüşümle birlikte kayar).
    /// </summary>
    private Task MoveDrawerAsync(double to, int ms, Func<double, double> ease)
    {
        _drawerDone?.TrySetResult();
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetDrawer(to);
            return Task.CompletedTask;
        }

        _drawerFrom = _drawerValue;
        _drawerTo = to;
        _drawerMs = ms;
        _drawerEase = ease;
        _drawerDone = new TaskCompletionSource();
        _drawerClock.Restart();
        if (!_drawerMoving)
        {
            _drawerMoving = true;
            CompositionTarget.Rendering += Drawer_Rendering;
        }
        return _drawerDone.Task;
    }

    private void Drawer_Rendering(object? sender, EventArgs e)
    {
        double t = Math.Clamp(_drawerClock.Elapsed.TotalMilliseconds / _drawerMs, 0, 1);
        SetDrawer(_drawerFrom + (_drawerTo - _drawerFrom) * _drawerEase(t));
        if (t >= 1 || _closing)
        {
            StopDrawer();
        }
    }

    private void StopDrawer()
    {
        if (_drawerMoving)
        {
            _drawerMoving = false;
            CompositionTarget.Rendering -= Drawer_Rendering;
        }
        _drawerDone?.TrySetResult();
        _drawerDone = null;
    }

    private void SetDrawer(double distance)
    {
        _drawerValue = distance;
        Vector toward = TowardBar();
        _drawer.X = toward.X * distance;
        _drawer.Y = toward.Y * distance;
        if (distance <= 0)
        {
            _square.Clip = null;
            return;
        }

        // Saat kipinde arkasına girilecek ada yok: kesik kartın kendi yerinin kenarında, köşeleri yuvarlak; kalan parça
        // saatin üstünde, açıkken olduğu boşlukla durur. Yan gölge payı bu arada kırpılır.
        if (_barIsChip && !_atBarPlace)
        {
            double kept = Square - distance;
            Rect piece = _edge switch
            {
                AppBarEdge.Top => new Rect(0, distance, Square, kept),
                AppBarEdge.Left => new Rect(distance, 0, kept, Square),
                AppBarEdge.Right => new Rect(0, 0, kept, Square),
                _ => new Rect(0, 0, Square, kept)
            };
            _square.Clip = new RectangleGeometry(piece, Corner, Corner);
            return;
        }

        // Çubuğun çizgisi kartın yerinden BarGap ötede; ötesi görünmez. Gölge payı öteki kenarlarda açık kalır.
        double s = ShadowMargin;
        double line = Square + BarGap - distance;
        Rect visible = _edge switch
        {
            AppBarEdge.Top => new Rect(-s, distance - BarGap, Square + 2 * s, Square + s - (distance - BarGap)),
            AppBarEdge.Left => new Rect(distance - BarGap, -s, Square + s - (distance - BarGap), Square + 2 * s),
            AppBarEdge.Right => new Rect(-s, -s, line + s, Square + 2 * s),
            _ => new Rect(-s, -s, Square + 2 * s, line + s)
        };
        _square.Clip = new RectangleGeometry(visible);
    }

    private static double EaseInOut(double t)
    {
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Yerini biraz geçip geri oturur.</summary>
    private static double EaseOutBack(double t)
    {
        const double overshoot = 1.4;
        double u = t - 1;
        return 1 + (overshoot + 1) * u * u * u + overshoot * u * u;
    }

    /// <summary>Ön yüzdeki çizgi; ilerleme bilinmiyorsa kısa bir parça gidip gelir.</summary>
    private void SetProgress(double? progress, ConversionTarget target)
    {
        double track = Square - 2 * TrackInset;
        if (progress is { } value)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, null);
            _sweep.X = 0;
            _fill.Width = track * Math.Clamp(value, 0, 1);
            _frontLabel.Text = $"{target.Short} · %{Math.Round(Math.Clamp(value, 0, 1) * 100)}";
            return;
        }

        _frontLabel.Text = target.Short;
        _fill.Width = SweepWidth;
        if (SystemParameters.ClientAreaAnimation && !_sweep.HasAnimatedProperties)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-SweepWidth, track, TimeSpan.FromMilliseconds(SweepMs))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
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

    #region Deck
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

    /// <summary>İlk üç dosyanın ikonu desteye; ortadan (çekmece açılınca) ya da hafif küçükten gelir.</summary>
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
    /// <summary>İçeri bırakılan dosya kopyalanır gibi görünür (kullanıcının isteği): asıl dosya yerinde kalır.</summary>
    private void Card_DragOver(object sender, DragEventArgs e)
    {
        // Kutudan çıkan sürükleme daha kutunun üstündeyken yasak imleci göstermesin (alınamıyor sanılıyordu, kullanıcı):
        // gideceği efekt gösterilir, kutuya geri bırakmak bir şey yapmaz.
        if (_draggingOut)
        {
            e.Effects = _dragOutEffect;
            e.Handled = true;
            return;
        }

        bool can = _stage != Stage.Running && e.Data.GetDataPresent(DataFormats.FileDrop)
            && !e.Data.GetDataPresent(ToolsPanel.InternalDragFormat) && e.AllowedEffects.HasFlag(DragDropEffects.Copy);
        e.Effects = can ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _dropFrame.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        _dropFrame.Visibility = Visibility.Collapsed;
        if (_draggingOut)
        {
            // Kutudan çıkıp yine kutuya bırakıldı: hiçbir şey olmadı.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (_stage != Stage.Running && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddFiles(paths);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    /// <summary>Kutunun her yerinden tutulunca hepsi sürüklenir (tek dosya ikondan seçmek kafa karıştırdı, kullanıcı).</summary>
    private void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = _files.Count > 0 && _stage is Stage.Choose or Stage.Done ? e.GetPosition(this) : null;
    }

    /// <summary>
    /// Dışarı sürükleme: dönüşmüş dosyalar (bizim klasörümüzde) bırakılan yere taşınır (kullanıcının seçimi); arada
    /// kullanıcının kendi dosyası varsa yalnızca kopyalanır, asıl dosya yerinden oynamaz.
    /// </summary>
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

        string[] dragged = _files.ToArray();
        _pressedAt = null;
        _dragOutEffect = dragged.All(FileConverter.IsOurs) ? DragDropEffects.Move : DragDropEffects.Copy;
        _draggingOut = true;
        try
        {
            // Kabuğun sürüklemesi, imlecin yanında kendi yelpazemiz.
            BitmapSource? picture = DragPicture(out Point hotspot);
            ShellDrag.DoDragDrop(this, new WindowInteropHelper(this).Handle, dragged, _dragOutEffect, picture, hotspot);
        }
        finally
        {
            _draggingOut = false;
        }

        // Taşınan dosya artık burada değil.
        _files.RemoveAll(path => !File.Exists(path) && !Directory.Exists(path));
        FileConverter.RemoveEmptyFolders();
        if (_files.Count == 0)
        {
            Clear();
            return;
        }

        _caption.Text = Summary(_files);
        _ = ShowDeckAsync(fromCenter: false);
        if (_stage == Stage.Choose)
        {
            ShowStage(Stage.Choose);
        }
    }
    #endregion

    private const double PictureSize = 96;
    private const double PictureIconTop = 14;
    private const double PictureCursorGap = 20;

    /// <summary>
    /// Sürükleme görüntüsü (kullanıcının seçimi, B · Yelpaze): zeminsiz, destedeki gibi eğik ikonlar, yumuşak gölgeli;
    /// birden çok dosyada altında sayı etiketi. İmleç destenin ortasında.
    /// </summary>
    private BitmapSource? DragPicture(out Point hotspot)
    {
        hotspot = default;
        int count = Math.Min(_files.Count, MaxShown);
        if (count == 0 || _icons[0].Source == null)
        {
            return null;
        }

        var root = new Grid { Width = PictureSize, Height = PictureSize };
        for (int i = count - 1; i >= 0; i--)
        {
            (double x, double y, double angle) = DeckSpot(i, count);
            root.Children.Add(new Image
            {
                Source = _icons[i].Source,
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, PictureIconTop, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new RotateTransform(angle), new TranslateTransform(x, y) } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Direction = 270, Opacity = 0.35 }
            });
        }

        if (_files.Count > 1)
        {
            root.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, PictureIconTop + IconSize - 2, 0, 0),
                Padding = new Thickness(7, 1, 7, 2),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("DragBadgeFill"),
                BorderBrush = (Brush)FindResource("DragBadgeEdge"),
                BorderThickness = new Thickness(0.5),
                Child = new TextBlock
                {
                    Text = _files.Count.ToString(),
                    FontFamily = FontFamily,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("DragBadgeText")
                }
            });
        }

        root.Measure(new Size(PictureSize, PictureSize));
        root.Arrange(new Rect(0, 0, PictureSize, PictureSize));
        root.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(PictureSize * dpi.DpiScaleX), (int)Math.Ceiling(PictureSize * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        // İmleç destenin altında: Windows'un oku ve "Taşı" yazısı imlecin yanına çizilir, ikonların üstüne binmesin.
        hotspot = new Point(PictureSize / 2 * dpi.DpiScaleX, (PictureIconTop + IconSize + PictureCursorGap) * dpi.DpiScaleY);
        return bitmap;
    }

    #region Placement
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
    #endregion
}
