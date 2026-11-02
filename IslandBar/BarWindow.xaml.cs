using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace IslandBar;

public partial class BarWindow : AppBarWindow
{

    /// <summary>Adanın ekran kenarıyla ve pencerelerle arasındaki boşluk.</summary>
    private const double FloatGap = 8;
    private const int ClockShowDateMs = 220;
    private const int ClockHideDateMs = 160;
    private const double ClockShrunkScale = 0.72;

    /// <summary>Üzerine gelince saatin yukarı kayması, kendi yüksekliğine oranla.</summary>
    private const double ClockLift = 0.22;
    private const double ClockDateRise = 4;
    private const double IslandPadding = 4;
    private const double IslandCornerRadius = 15;
    private const double ShadowBleed = 8;
    private const double SideSampleDistance = 24;
    private const double SideSampleLength = 40;

    /// <summary>Adanın gölgesinin dışı.</summary>
    private const double StripSampleDistance = 16;
    private const double StripSampleWidth = 4;

    private const double IslandContentMargin = 4;
    private const int ClockChipCollapseMs = 760;
    private const int ClockChipExpandMs = 340;

    /// <summary>Çubuğun otomatik gizleme gecikmesiyle aynı: fare bir an ayrılınca hemen küçülmesin. Süre sayacın tıklarıyla.</summary>
    private const int ClockChipDelayTicks = 6;

    /// <summary>Simgeler bu ilerlemeye gelince tamamen sönmüş olur; zemin onlardan sonra daralmayı sürdürür.</summary>
    private const double ClockChipFadeEnd = 0.35;

    /// <summary>Ada bu ilerlemede tek parça en küçük boyuna iner; ayrılma ondan biraz önce başlar, iki hareket birbirine akar.</summary>
    private const double ClockChipShrinkEnd = 0.6;
    private const double ClockChipSplitStart = 0.42;

    /// <summary>Çubuğun saati bu aralıkta söner, damlaların rakamları bu aralıkta büyür, en son sıvı dolar.</summary>
    private const double ClockTextFadeStart = 0.18;
    private const double ClockTextFadeEnd = 0.42;
    private const double ClockDigitsStart = 0.3;
    private const double ClockDigitsEnd = 0.62;
    private const double ClockLiquidStart = 0.68;
    private const double PanelShadowMargin = 20;
    private const int ToolsDragFadeMs = 120;
    private const string PinnedKeyPrefix = "pin:";
    private const int MaxGapSlots = 6;
    private const int GapCloseDelayMs = 120;
    private const double StartPanelHeight = 620;
    private const double StartPanelScreenMargin = 40;
    private const int PreviewShowDelayMs = 350;
    private const int PreviewHideDelayMs = 250;
    private const int PreviewFadeMs = 130;

    /// <summary>Düğmenin üzerinden geçip giderken kart hazırlanmasın.</summary>
    private const int HoverPrepareDelayMs = 30;
    private const int PrepareIntervalMs = 1500;
    private const int WarmDelayMs = 2000;
    private const int WarmTimeoutMs = 3000;
    private const double ChipTextSlack = 2;

    /// <summary>Bir önizleme kutusunun kenar boşluğuyla birlikte kapladığı yer (<c>PreviewTile</c>).</summary>
    private const double PreviewTileWidth = 220;
    private const double PreviewTileHeight = 170;
    private const double CompactThickness = 36;
    private const double EdgeRevealDepth = 2;

    private static readonly int TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private static readonly Guid BatteryIconGuid = new(NotificationArea.POWER_GUID);

    public static readonly DependencyProperty ButtonSizeProperty =
        DependencyProperty.Register(nameof(ButtonSize), typeof(double), typeof(BarWindow), new PropertyMetadata(32d));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(BarWindow), new PropertyMetadata(20d));

    public static readonly DependencyProperty IslandMarginProperty =
        DependencyProperty.Register(nameof(IslandMargin), typeof(Thickness), typeof(BarWindow), new PropertyMetadata(new Thickness(FloatGap)));

    public static readonly DependencyProperty ClockChipProgressProperty =
        DependencyProperty.Register(nameof(ClockChipProgress), typeof(double), typeof(BarWindow), new PropertyMetadata(0d, (d, e) => ((BarWindow)d).ApplyClockChip()));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(BarWindow), new PropertyMetadata(false));

    public double ButtonSize
    {
        get => (double)GetValue(ButtonSizeProperty);
        set => SetValue(ButtonSizeProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Thickness IslandMargin
    {
        get => (Thickness)GetValue(IslandMarginProperty);
        set => SetValue(IslandMarginProperty, value);
    }

    /// <summary>0 tam ada, 1 saat çipi.</summary>
    public double ClockChipProgress
    {
        get => (double)GetValue(ClockChipProgressProperty);
        set => SetValue(ClockChipProgressProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private readonly AppSettings _settings;
    private readonly ShellManager _shellManager;
    private readonly NotificationArea _notificationArea;
    private readonly ICollectionView _taskWindows;
    private readonly ICollectionView _allWindows;
    private readonly ListCollectionView? _trayBattery;
    private readonly ListCollectionView? _trayOthers;
    private IntPtr _windowsTray;
    private AppGroup? _addTargetGroup;
    private readonly DispatcherTimer _dragOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _startMenuWasOpen;
    private readonly ObservableCollection<TaskGroup> _taskGroups = new();
    private TaskGroup? _pressedTaskGroup;
    private TaskGroup? _draggedGroup;
    private Point? _taskDragStart;
    private TaskPanel? _taskPanel;
    private string[]? _gapPaths;
    private readonly Dictionary<string, ImageSource> _gapIcons = new();
    private readonly DispatcherTimer _gapCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(GapCloseDelayMs) };
    private ApplicationWindow? _pressedActiveWindow;
    private readonly DispatcherTimer _previewShowTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewShowDelayMs) };
    private readonly DispatcherTimer _previewHideTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewHideDelayMs) };
    private Button? _previewButton;
    private readonly Dictionary<Popup, GenieEffect> _genieOpening = new();
    private readonly HashSet<Popup> _genieClosing = new();
    private int _geniesPlaying;
    private readonly GenieStage _stage;
    private readonly Dictionary<Popup, PopupPrep> _prep = new();
    private bool _preparing;
    private Popup? _warmingPopup;
    private TaskCompletionSource? _warmDone;
    private readonly NowPlayingWatcher? _nowPlaying;
    private bool _chipAtBarPlace;
    private bool _volumeAtBarPlace;
    private bool _glassWaitsForVolume;
    private Point _volumePlace;
    private Point _chipPlace;

    private readonly IDisposable? _volumeWatcher;
    private readonly GroupRunTracker? _runTracker;
    private Point? _tileDragStart;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _clockMinute;
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private int _autoHideTicks;
    private readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private WriteableBitmap? _glassBitmap;
    private readonly GlassWatch _islandWatch = new();
    private readonly GlassWatch _cardWatch = new();
    private Popup? _watchedCard;
    private bool _glassSampling;
    private bool _glassBusy;
    private bool _recomposeToggle;
    private readonly SolidColorBrush _tintBrush = new(GlassTint.Rest);
    private Color _tintTarget = GlassTint.Rest;
    private bool _fullScreenOnThisBar;
    private bool _cursorInBarZone;
    private bool _desktopClear;
    private bool _clockChipTarget;
    private int _clockChipDelay;
    private readonly RectangleGeometry _clockChipClip = new();
    private FrameworkElement[] _clockChipFades = [];
    private bool _startMenuVisible;

    public BarWindow(ShellManager shellManager, AppSettings settings, AppBarScreen screen)
        : base(shellManager.AppBarManager, shellManager.ExplorerHelper, shellManager.FullScreenHelper,
            screen, settings.Edge, settings.AutoHideBar ? AppBarMode.AutoHide : AppBarMode.Normal, settings.Thickness)
    {
        _settings = settings;
        _stage = new GenieStage(screen.Bounds);

        // Adaların dışı saydam ve tıklamaları alttaki pencerelere geçirir.
        AllowsTransparency = true;

        InitializeComponent();
        DataContext = settings;
        AutoHideElement = BarRoot;
        ApplyThickness();
        _shellManager = shellManager;
        _notificationArea = shellManager.NotificationArea;
        _windowsTray = WindowHelper.FindWindowsTray(_notificationArea.Handle);

        // Her çubuk kendi ekranındaki pencereleri gösterir; pencere taşınınca düğmesi de taşınır.
        _taskWindows = shellManager.Tasks.CreateGroupedWindowsCollection();
        _taskWindows.Filter = item => item is ApplicationWindow { ShowInTaskbar: true } window && window.HMonitor == Screen.HMonitor;
        ((INotifyCollectionChanged)_taskWindows).CollectionChanged += TaskWindows_CollectionChanged;

        // Sabitlenmiş uygulama her çubukta durur; penceresi hangi ekranda olursa olsun durumu görünür.
        _allWindows = shellManager.Tasks.CreateGroupedWindowsCollection();
        _allWindows.Filter = item => item is ApplicationWindow { ShowInTaskbar: true };
        ((INotifyCollectionChanged)_allWindows).CollectionChanged += TaskWindows_CollectionChanged;
        _settings.Pinned.CollectionChanged += TaskWindows_CollectionChanged;
        TaskList.ItemsSource = _taskGroups;
        SyncTaskGroups();
        _previewShowTimer.Tick += PreviewShowTimer_Tick;
        _previewHideTimer.Tick += PreviewHideTimer_Tick;
        _dragOpenTimer.Tick += DragOpenTimer_Tick;
        _gapCloseTimer.Tick += GapCloseTimer_Tick;

        // Tepsi gibi yalnızca ana ekranda. Gizlemek olmaz: IconButton şablonunun Loaded animasyonu,
        // şablonu oluşmamış düğmede hedefini bulamaz ve hata fırlatır.
        if (!Screen.Primary)
        {
            ((Panel)QuickButton.Parent).Children.Remove(QuickButton);
        }
        else
        {
            // İkonlar değişince hemen güncellensin; saatin dakikalık yenilemesi kaçan olaya karşı kalır.
            _volumeWatcher = AudioEndpoint.Watch(UpdateVolumeGlyph);

            // Grup verisi çubuklar arasında ortak; izleyen bir tane yeter.
            _runTracker = new GroupRunTracker(shellManager.Tasks, settings);
            Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;

            // Windows bildirimleri gibi ana ekranda; her çubukta ayrı kart çıkmasın.
            _nowPlaying = new NowPlayingWatcher();
            _nowPlaying.TrackStarted += NowPlaying_TrackStarted;
            _nowPlaying.TrackUpdated += NowPlaying_TrackUpdated;
            _nowPlaying.Start();
        }
        QuickPanel.AllWindows = () => shellManager.Tasks.GroupedWindows.OfType<ApplicationWindow>();
        QuickPanel.CloseRequested += ClosePanel;

        StartPanel.Attach(settings);
        StartPanel.CloseRequested += ClosePanel;
        StartPanel.LaunchFailed += item => ReportFailures([item]);
        StartPanel.PinToBarRequested += item => AddItems(_settings.Pinned, [item]);
        StartPanel.WindowsStartRequested += OpenWindowsStartMenu;
        // Çubuk gizliyken de doğru yerde açılsın: düğme ekran dışında olabilir, pencere değil.
        StartPopup.PlacementTarget = this;
        ChipPopup.PlacementTarget = this;
        VolumePopup.PlacementTarget = this;
        VolumeCard.Dismissed += () =>
        {
            VolumePopup.IsOpen = false;
            RefreshGlassAfterVolume();
        };
        VolumeCard.Slid += RefreshGlassAfterVolume;
        Chip.Dismissed += () => ChipPopup.IsOpen = false;
        Chip.Slid += () =>
        {
            if (!IsIslandOffScreen())
            {
                RefreshGlass();
            }
        };
        Chip.Clicked += () => QuickPanel.OpenMediaApp(Chip.AppId);
        ToolsContent.DragStarted += ToolsContent_DragStarted;
        ToolsContent.DragFinished += ToolsContent_DragFinished;
        ToolsContent.Opened += ClosePanel;

        // Tepsi, Windows'taki gibi yalnızca ana ekranda. Çubukta pil kalır, gerisi tek düğmede toplanır.
        if (Screen.Primary)
        {
            _trayBattery = CreateTrayView(IsBatteryIcon);
            _trayOthers = CreateTrayView(icon => !IsBatteryIcon(icon));
            TrayList.ItemsSource = _trayBattery;
            TrayOverflowList.ItemsSource = _trayOthers;
        }

        Loaded += BarWindow_Loaded;
        Closed += BarWindow_Closed;

        UpdateMenuChecks();
        _settings.PropertyChanged += Settings_PropertyChanged;

        UpdateClock();
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();

        if (AppBarMode == AppBarMode.AutoHide)
        {
            // Adalar tamamen ekran dışına kayar; fareyi konumundan takip ediyoruz, kenarda iz kalmaz.
            AutoHideShowMargin = 0;
            _autoHideTimer.Tick += AutoHideTimer_Tick;
            _autoHideTimer.Start();

            _clockChipFades = Screen.Primary
                ? [LauncherSection, LauncherDivider, TaskList, SystemDivider, TrayOverflowButton, QuickButton]
                : [LauncherSection, LauncherDivider, TaskList, SystemDivider, TrayOverflowButton];
            MainIsland.SizeChanged += (_, _) => ApplyClockChip();
            Clock.SizeChanged += (_, _) => ApplyClockChip();

            // Fareyle gelince sayacı beklemeden açılsın.
            MainIsland.MouseEnter += (_, _) => Dispatcher.BeginInvoke(UpdateClockChip);
        }
    }

    #region Island layout
    private void ApplyThickness()
    {
        double thickness = _settings.Thickness;

        // Pencere, adayla birlikte çevresindeki boşluğu da kapsar; ayrılan alan da bu kadardır.
        DesiredHeight = thickness + 2 * FloatGap;
        DesiredWidth = thickness + 2 * FloatGap;

        ButtonSize = thickness - 2 * IslandPadding;
        IconSize = Math.Max(16, Math.Round(ButtonSize * 0.62));
        IsCompact = thickness < CompactThickness;
        Chip.SetThickness(thickness, IslandCornerRadius, IsCompact);
        VolumeCard.SetThickness(thickness, IslandCornerRadius);
        double clockSize = Math.Clamp(Math.Round(ButtonSize * 0.58), 13, 26);
        ClockTime.SetValue(TextBlock.FontSizeProperty, clockSize);
        ClockDate.FontSize = Math.Clamp(Math.Round(ButtonSize * 0.32), 10, 13);
        CenterClockDigits(clockSize);
    }

    /// <summary>
    /// Rakamların kuyruğu yok, satır kutusunun altında dururlar: kutu ortalanınca saat adada aşağıda görünür.
    /// Tarih yerinde kalsın diye saatin kayması onun payından düşülür.
    /// </summary>
    private void CenterClockDigits(double fontSize)
    {
        var text = new FormattedText(
            "0",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface((FontFamily)FindResource("ClockFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Rect ink = text.BuildGeometry(new Point()).Bounds;
        double drop = (ink.Top + ink.Bottom) / 2 - text.Height / 2;

        ClockFace.Margin = new Thickness(0, 0, 0, 2 * drop);
        ClockDate.Margin = new Thickness(-14, 0, -14, -drop);
    }

    private ListCollectionView CreateTrayView(Predicate<NotifyIcon> belongs)
    {
        var view = new ListCollectionView(_notificationArea.TrayIcons)
        {
            Filter = item => item is NotifyIcon { IsHidden: false } icon && belongs(icon),
            IsLiveFiltering = true
        };
        view.LiveFilteringProperties.Add(nameof(NotifyIcon.IsHidden));
        return view;
    }

    private static bool IsBatteryIcon(NotifyIcon icon)
    {
        return icon.GUID == BatteryIconGuid;
    }

    private void ShadowHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Çipte kırpmayı damlaların şekli belirler.
        if (ClockChipProgress <= 0)
        {
            ClipShadowToIsland(e.NewSize);
        }
    }

    private void ClipShadowToIsland(Size size)
    {
        var outer = new RectangleGeometry(new Rect(size));
        var island = new RectangleGeometry(
            new Rect(ShadowBleed, ShadowBleed, Math.Max(0, size.Width - 2 * ShadowBleed), Math.Max(0, size.Height - 2 * ShadowBleed)),
            IslandCornerRadius, IslandCornerRadius);
        ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, island);
    }
    #endregion

    #region Glass
    private void BarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        IslandTint.Background = _tintBrush;
        DropTint.Fill = _tintBrush;
        if (ThemeManager.UsesGlass)
        {
            _glassTimer.Tick += GlassTimer_Tick;
            _glassTimer.Start();
            RefreshGlass();
        }
        else
        {
            GlassHost.Visibility = Visibility.Collapsed;

            // Saatin gölgesi parlak camda okunsun diye; koyu yazıda kirli hale olur.
            Clock.Effect = null;
        }
        _ = WarmPopupsAsync();
    }

    private void GlassHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ClockChipProgress <= 0)
        {
            GlassHost.Clip = new RectangleGeometry(new Rect(e.NewSize), IslandCornerRadius, IslandCornerRadius);
        }
        _islandWatch.Request();
    }

    /// <summary>Görüntü alırken cam yakalamadan bir an hariç tutuluyor; yalnızca çevresi değişince, değişmese de seyrek tazele.</summary>
    private async void GlassTimer_Tick(object? sender, EventArgs e)
    {
        // Oynayan animasyon şeritlere girer, kart açılıp kapanırken arkası değişmiş sanılmasın. Ada genişlerken de öyle.
        if (_glassSampling || _fullScreenOnThisBar || _geniesPlaying > 0 || _taskPanel?.IsMoving == true)
        {
            return;
        }

        // Çubuk gizliyken ada izlenmez, ama şarkı kartı adanın yerinde duruyor olabilir.
        System.Drawing.Rectangle[]? islandStrips = !IsIslandOffScreen() && TryGetIslandRestRect(out System.Drawing.Rectangle island) ? IslandStrips(island) : null;
        Popup? card = GroupPanel.IsOpen ? GroupPanel : TrayPopup.IsOpen ? TrayPopup : ToolsPopup.IsOpen ? ToolsPopup : QuickPopup.IsOpen ? QuickPopup : StartPopup.IsOpen ? StartPopup
            : Chip.IsShown && !Chip.IsHiding ? ChipPopup : null;
        System.Drawing.Rectangle[]? cardStrips = card != null ? CardStrips(card) : null;
        if (islandStrips == null && cardStrips == null)
        {
            return;
        }

        if (card != _watchedCard)
        {
            _watchedCard = card;
            _cardWatch.Reset();
        }

        _glassSampling = true;
        bool refreshIsland, refreshCard;
        try
        {
            // Ekranı okumak birkaç milisaniye sürer; arayüzü her 200 ms'de bir bekletmesin.
            (refreshIsland, refreshCard) = await Task.Run(() => (
                islandStrips != null && _islandWatch.ShouldRefresh(islandStrips),
                cardStrips != null && _cardWatch.ShouldRefresh(cardStrips)));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran okunamıyor.
            return;
        }
        finally
        {
            _glassSampling = false;
        }

        if (AllowClose)
        {
            return;
        }

        if (refreshIsland)
        {
            RefreshGlass();

            if (PreviewPopup.IsOpen)
            {
                _ = PreviewCard.RefreshGlassAsync(animateTint: true);
            }
        }

        if (refreshCard && card is { IsOpen: true })
        {
            _ = card == QuickPopup ? QuickPanel.RefreshGlassAsync(animateTint: true)
                : card == StartPopup ? StartPanel.RefreshGlassAsync(animateTint: true)
                : card == ChipPopup ? Chip.RefreshGlassAsync()
                : (card == GroupPanel ? PanelCard : card == ToolsPopup ? ToolsCard : TrayCard).RefreshGlassAsync(animateTint: true);
        }
    }

    private async void RefreshGlass()
    {
        if (!ThemeManager.UsesGlass || _glassBusy || AllowClose || !TryGetIslandRestRect(out System.Drawing.Rectangle area))
        {
            return;
        }

        // Ada ekran dışındaysa hariç tutmaya gerek yok.
        IntPtr exclude = IsIslandOffScreen() ? IntPtr.Zero : Handle;

        _glassBusy = true;
        try
        {
            GlassFrame? frame = await Task.Run(() => GlassCapture.Capture(area, exclude));
            if (frame != null && !AllowClose)
            {
                ShowGlassFrame(frame);
            }

            // Durağan ekranda Windows yeniden çizmez ve hariç tutma kalkmış sayılmaz; ada ekran görüntülerinde
            // çıkmaz olur. Pencereyi yeniden çizdirerek kaldır.
            if (exclude != IntPtr.Zero)
            {
                ForceRecompose();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran alınamıyor; sonraki denemede düzelir.
        }
        finally
        {
            _glassBusy = false;
        }
    }

    private void ForceRecompose()
    {
        _recomposeToggle = !_recomposeToggle;
        BarRoot.Opacity = _recomposeToggle ? 0.999 : 1;
    }

    private void ShowGlassFrame(GlassFrame frame)
    {
        GlassCard.Show(frame, ref _glassBitmap, GlassBrush);
        ApplyTint(frame.Luminance);
    }

    private void ApplyTint(double luminance)
    {
        // Koyu arka planda cam hafifçe aydınlatır, parlakta koyulaşır; beyaz yazı her zaman okunsun.
        Color target = GlassTint.For(luminance);

        if (GlassTint.IsNear(target, _tintTarget))
        {
            return;
        }

        _tintTarget = target;
        _tintBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(350)));
    }

    /// <summary>
    /// Gerçek konuma bakar. Animasyon olaylarından tutulan bir bayrak güvenilir değil: gizlenme yarıda
    /// kesilince tamamlandı olayı yine de gelebiliyor, ada ekrandayken gizli sanılıyor.
    /// </summary>
    private bool IsIslandOffScreen()
    {
        var slide = (TranslateTransform)BarRoot.RenderTransform;
        return Orientation == Orientation.Horizontal
            ? Math.Abs(slide.Y) >= DesiredHeight
            : Math.Abs(slide.X) >= DesiredWidth;
    }

    private bool TryGetIslandRestRect(out System.Drawing.Rectangle area)
    {
        area = System.Drawing.Rectangle.Empty;
        if (!IsLoaded || MainIsland.ActualWidth <= 0 || MainIsland.ActualHeight <= 0)
        {
            return false;
        }

        // Kayarken ya da gizliyken de adanın duracağı yeri ver.
        var slide = (TranslateTransform)BarRoot.RenderTransform;
        Point origin = MainIsland.PointToScreen(new Point(0, 0));
        area = new System.Drawing.Rectangle(
            (int)Math.Round(origin.X - slide.X * DpiScale),
            (int)Math.Round(origin.Y - slide.Y * DpiScale),
            (int)Math.Round(MainIsland.ActualWidth * DpiScale),
            (int)Math.Round(MainIsland.ActualHeight * DpiScale));
        area.Intersect(Screen.Bounds);
        return !area.IsEmpty;
    }

    /// <summary>
    /// Adanın iki yanı: pencerenin orası saydam, görünen adanın arkasıyla aynı sıradaki şeydir. Gölgeden uzak okunur.
    /// Otomatik gizlemede pencereler adanın altına da girer: ekranın içine bakan kenar boyunca ince bir şerit de okunur.
    /// Alan ayrılan modda o şerit adanın arkasını değil, üstündeki pencereleri gösterir; bakılmaz.
    /// </summary>
    private System.Drawing.Rectangle[] IslandStrips(System.Drawing.Rectangle island)
    {
        int reach = (int)Math.Round(SideSampleDistance * DpiScale);
        int length = (int)Math.Round(SideSampleLength * DpiScale);
        var strips = new List<System.Drawing.Rectangle>();
        if (Orientation == Orientation.Horizontal)
        {
            strips.Add(new System.Drawing.Rectangle(island.Left - reach - length, island.Top, length, island.Height));
            strips.Add(new System.Drawing.Rectangle(island.Right + reach, island.Top, length, island.Height));
        }
        else
        {
            strips.Add(new System.Drawing.Rectangle(island.Left, island.Top - reach - length, island.Width, length));
            strips.Add(new System.Drawing.Rectangle(island.Left, island.Bottom + reach, island.Width, length));
        }

        if (AppBarMode == AppBarMode.AutoHide)
        {
            strips.Add(StripBeside(island, AppBarEdge switch
            {
                AppBarEdge.Top => AppBarEdge.Bottom,
                AppBarEdge.Left => AppBarEdge.Right,
                AppBarEdge.Right => AppBarEdge.Left,
                _ => AppBarEdge.Top
            }));
        }
        return Visible(strips);
    }

    /// <summary>Açık kartın çubuğa bakmayan üç kenarı; çubuk tarafında adanın kendisi var. Kök, gölge payını zaten içeriyor.</summary>
    private System.Drawing.Rectangle[] CardStrips(Popup popup)
    {
        var root = (FrameworkElement)popup.Child;
        if (PresentationSource.FromVisual(root) == null)
        {
            return [];
        }

        Point origin = root.PointToScreen(new Point());
        var area = new System.Drawing.Rectangle(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Round(root.ActualWidth * DpiScale),
            (int)Math.Round(root.ActualHeight * DpiScale));

        var strips = new List<System.Drawing.Rectangle>();
        foreach (AppBarEdge side in new[] { AppBarEdge.Left, AppBarEdge.Top, AppBarEdge.Right, AppBarEdge.Bottom })
        {
            if (side != AppBarEdge)
            {
                strips.Add(StripBeside(area, side, distance: 0));
            }
        }
        return Visible(strips);
    }

    private System.Drawing.Rectangle StripBeside(System.Drawing.Rectangle area, AppBarEdge side, double distance = StripSampleDistance)
    {
        int gap = (int)Math.Round(distance * DpiScale);
        int width = (int)Math.Round(StripSampleWidth * DpiScale);
        return side switch
        {
            AppBarEdge.Top => new System.Drawing.Rectangle(area.Left, area.Top - gap - width, area.Width, width),
            AppBarEdge.Bottom => new System.Drawing.Rectangle(area.Left, area.Bottom + gap, area.Width, width),
            AppBarEdge.Left => new System.Drawing.Rectangle(area.Left - gap - width, area.Top, width, area.Height),
            _ => new System.Drawing.Rectangle(area.Right + gap, area.Top, width, area.Height)
        };
    }

    private System.Drawing.Rectangle[] Visible(List<System.Drawing.Rectangle> strips)
    {
        for (int i = 0; i < strips.Count; i++)
        {
            System.Drawing.Rectangle strip = strips[i];
            strip.Intersect(Screen.Bounds);
            strips[i] = strip;
        }
        // IsEmpty yalnızca dört değer de sıfırsa doğru: henüz ölçülmemiş kartın eni sıfır, boyu değil. Böyle şerit okunamaz, uygulamayı kapatırdı.
        return strips.Where(strip => strip.Width > 0 && strip.Height > 0).ToArray();
    }

    protected override void OnAutoHideAnimationBegin(bool isHiding)
    {
        base.OnAutoHideAnimationBegin(isHiding);

        // Açılırken arkası taze olsun; ada henüz ekran dışında, görüntüye karışmaz.
        if (!isHiding && IsIslandOffScreen())
        {
            _islandWatch.Reset();

            // Şarkı kartı ya da ses göstergesi adanın yerinde duruyorsa cama karışır; kart yerine kayınca alınır (Slid).
            if (VolumePopup.IsOpen)
            {
                _glassWaitsForVolume = true;
            }
            else if (!ChipPopup.IsOpen)
            {
                RefreshGlass();
            }
        }
    }

    protected override void OnFullScreenEnter(FullScreenApp app)
    {
        base.OnFullScreenEnter(app);

        // Temel sınıf bazı pencereleri tam ekran saymıyor; gerçekten geri çekildiyse görüntü almayı bırak.
        _fullScreenOnThisBar = !Topmost;
        if (_fullScreenOnThisBar)
        {
            Chip.Dismiss();
        }
    }

    protected override void OnFullScreenLeave()
    {
        base.OnFullScreenLeave();

        _fullScreenOnThisBar = false;
    }
    #endregion

    #region Auto-hide
    protected override bool ShouldAllowAutoHide()
    {
        // Altında pencere yoksa gizlenmez, saate dönüşür.
        return CanRetract() && !_desktopClear;
    }

    /// <summary>Çubuğa şu an ihtiyaç yok: pencere varsa gizlenir, yoksa saate dönüşür.</summary>
    private bool CanRetract()
    {
        // Başlat ana ekranda açılıyor; Windows'taki gibi açıkken çubuk da görünsün.
        return base.ShouldAllowAutoHide() && !_cursorInBarZone && !HoldsBar(GroupPanel) && !HoldsBar(TrayPopup) && !HoldsBar(ToolsPopup)
            && !HoldsBar(PreviewPopup) && !HoldsBar(QuickPopup) && !HoldsBar(StartPopup) && _geniesPlaying == 0 && !(_startMenuVisible && Screen.Primary)
            && _draggedGroup == null;
    }

    /// <summary>Isınmak için görünmeden açılan kart çubuğu ortaya çıkarmaz.</summary>
    private bool HoldsBar(Popup? popup)
    {
        return popup?.IsOpen == true && popup != _warmingPopup;
    }

    private async void Popup_OpenedOrClosed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AllowAutoHide));

        var popup = (Popup)sender!;
        GlassCard card = popup == GroupPanel ? PanelCard : popup == TrayPopup ? TrayCard : popup == ToolsPopup ? ToolsCard : PreviewCard;

        if (!popup.IsOpen)
        {
            // Bir sonraki açılışta eski görüntü bir an görünmesin.
            HideCard(card);
            WindowThumbnail.SetIsShown(card, false);
            return;
        }

        // Kart, arkasının görüntüsü hazır olunca belirir.
        await card.RefreshGlassAsync(animateTint: false, isHidden: true);

        if (!popup.IsOpen)
        {
            return;
        }

        if (popup == PreviewPopup)
        {
            // Küçük resimleri Windows çizer, WPF dönüşümü onlara işlemez; önizleme yalnızca solarak gelir.
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(PreviewFadeMs)));
            WindowThumbnail.SetIsShown(card, true);
        }
        else
        {
            RevealPopup(popup, card, popup == GroupPanel ? GroupsButton : popup == ToolsPopup ? ToolsButton : TrayOverflowButton);
        }
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        bool changed = UpdateCursorZone();

        if (ChipPopup.IsOpen && !Chip.IsHiding && IsIslandOffScreen() != _chipAtBarPlace)
        {
            MoveChip();
        }

        if (VolumePopup.IsOpen && !VolumeCard.IsHiding && IsBarOutOfSight() != _volumeAtBarPlace)
        {
            MoveVolumeChip();
        }

        // Başlat kontrolü süreç adı okuyor, pencereleri dolaşmak da sistem çağrısı; imleç kadar sık gerekmiyor.
        if (++_autoHideTicks % 4 == 0)
        {
            if (Screen.Primary)
            {
                bool visible = IsStartMenuOpen();
                changed |= visible != _startMenuVisible;
                _startMenuVisible = visible;
                if (!_settings.CustomStartMenu)
                {
                    StartGlyph.IsMenuOpen = visible;
                }
            }

            bool clear = IsDesktopClear();
            changed |= clear != _desktopClear;
            _desktopClear = clear;
        }

        // Önce çip: gizli çubuk yukarı kaymaya başlamadan doğru şekle geçsin.
        UpdateClockChip();

        if (changed)
        {
            OnPropertyChanged(nameof(AllowAutoHide));
        }
    }

    /// <summary>Çubuk bir uygulamanın üstünde durmuyor mu? Masaüstü bileşenleri görev çubuğu penceresi olmadığı için sayılmaz.</summary>
    private bool IsDesktopClear()
    {
        if (!_settings.ClockChipOnDesktop || !_settings.ShowClock || !TryGetIslandRestRect(out System.Drawing.Rectangle island))
        {
            return false;
        }

        return !_allWindows.OfType<ApplicationWindow>().Any(window => DesktopWindows.Covers(window.Handle, island));
    }

    private void UpdateClockChip()
    {
        // Gizliyken görünmeden doğru şekle geçer: masaüstü boşaldıysa çip yukarı kayar, yoksa açılınca tam ada gelir.
        if (IsIslandOffScreen())
        {
            SetClockChip(_desktopClear, animate: false);
            _clockChipDelay = 0;
            return;
        }

        if (!CanRetract())
        {
            _clockChipDelay = 0;
            SetClockChip(false, animate: true);
            return;
        }

        // Pencere geldi, çip aşağı kayıyor; bu arada açılmasın.
        if (_desktopClear && ++_clockChipDelay >= ClockChipDelayTicks)
        {
            SetClockChip(true, animate: true);
        }
    }

    private void SetClockChip(bool chip, bool animate)
    {
        if (chip == _clockChipTarget)
        {
            return;
        }

        _clockChipTarget = chip;
        double target = chip ? 1 : 0;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ClockChipProgressProperty, null);
            ClockChipProgress = target;
            return;
        }

        // Hedefe bugünkü değerden gider: yarıda kesilince sıçramaz. Daralma kendi eğrisini ApplyClockChip'te alır;
        // açılırken hızlı başlar ki damlalar fareye hemen tepki versin.
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(chip ? ClockChipCollapseMs : ClockChipExpandMs));
        if (!chip)
        {
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        }
        BeginAnimation(ClockChipProgressProperty, animation);
    }

    /// <summary>
    /// Yerleşim oynamaz: yalnızca zemin daralır, simgeler söner, saat ortaya kayar. Adayı daraltmak simgeleri sıkıştırır, saati dışarı iterdi.
    /// Sonra ada boyun verip iki damlaya ayrılır (<see cref="DropShape"/>); gölge, cam ve ton aynı şekli izler.
    /// Cam görüntüsü hep tam adanın arkasından alınır, görüntü sabit durur: ortası zaten çipin arkası, gerilmez.
    /// </summary>
    private void ApplyClockChip()
    {
        double progress = ClockChipProgress;
        bool horizontal = Orientation == Orientation.Horizontal;

        if (progress <= 0 || !IsLoaded)
        {
            IslandShell.ClearValue(WidthProperty);
            IslandShell.ClearValue(HeightProperty);
            GlassImage.ClearValue(WidthProperty);
            GlassImage.ClearValue(HeightProperty);
            GlassImage.ClearValue(HorizontalAlignmentProperty);
            GlassImage.ClearValue(VerticalAlignmentProperty);
            IslandContent.Clip = null;
            ClockSlide.X = 0;
            ClockSlide.Y = 0;
            Clock.Opacity = 1;
            ClockDrops.Update(0, 0, 0);
            ClockDrops.Visibility = Visibility.Collapsed;
            ShowDropLayers(false);
            GlassHost.Clip = new RectangleGeometry(new Rect(GlassHost.RenderSize), IslandCornerRadius, IslandCornerRadius);
            ClipShadowToIsland(ShadowHost.RenderSize);
            foreach (FrameworkElement section in _clockChipFades)
            {
                section.Opacity = 1;
            }
            return;
        }

        ClockDrops.Configure(_settings.Thickness, IslandCornerRadius, !horizontal);
        ClockDrops.Visibility = Visibility.Visible;
        ShowDropLayers(true);

        double natural = horizontal ? MainIsland.ActualWidth : MainIsland.ActualHeight;
        double cross = horizontal ? MainIsland.ActualHeight : MainIsland.ActualWidth;
        double shrinkAt = Math.Clamp(progress / ClockChipShrinkEnd, 0, 1);
        double shrink = 1 - Math.Pow(1 - shrinkAt, 3);
        double split = Math.Clamp((progress - ClockChipSplitStart) / (1 - ClockChipSplitStart), 0, 1);
        double gap = DropShape.GapAt(split, ClockDrops.RestGap);
        double solid = Math.Min(2 * ClockDrops.TileLength, natural);
        double length = Math.Min(natural + (solid - natural) * shrink + gap, natural);
        double glass = natural + 2 * ShadowBleed;

        Geometry shape = DropShape.Build(length, cross, IslandCornerRadius, split, gap, ClockDrops.RestGap, horizontal, 0);
        Geometry edge = DropShape.Build(length, cross, IslandCornerRadius, split, gap, ClockDrops.RestGap, horizontal, 0.5);
        GlassHost.Clip = shape;
        DropShadowShape.Data = shape;
        DropTint.Data = edge;
        DropSheen.Data = edge;

        // Gölge yalnızca damlaların dışında: içine düşerse camı karartır.
        Geometry hole = shape.CloneCurrentValue();
        hole.Transform = new TranslateTransform(ShadowBleed, ShadowBleed);
        var outer = new RectangleGeometry(new Rect(0, 0, (horizontal ? length : cross) + 2 * ShadowBleed, (horizontal ? cross : length) + 2 * ShadowBleed));
        ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, hole);

        IslandShell.Width = horizontal ? length : double.NaN;
        IslandShell.Height = horizontal ? double.NaN : length;
        GlassImage.Width = horizontal ? glass : double.NaN;
        GlassImage.Height = horizontal ? double.NaN : glass;

        // Yuvasından büyük öğeyi Stretch başa yaslar; o zaman çip adanın başının arkasını gösterirdi.
        GlassImage.HorizontalAlignment = horizontal ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        GlassImage.VerticalAlignment = horizontal ? VerticalAlignment.Stretch : VerticalAlignment.Center;

        // Kayma hesaba girmesin diye saatin yeri kaymadan önceki haliyle okunur.
        Point clock = Clock.TransformToAncestor(MainIsland).Transform(new Point()) - new Vector(ClockSlide.X, ClockSlide.Y);
        double toCenter = horizontal
            ? (natural - Clock.ActualWidth) / 2 - clock.X
            : (natural - Clock.ActualHeight) / 2 - clock.Y;
        ClockSlide.X = horizontal ? toCenter * progress : 0;
        ClockSlide.Y = horizontal ? 0 : toCenter * progress;

        // Sönmekte olan simgeler zeminin dışında kalmasın.
        double inner = Math.Max(0, length - 2 * IslandContentMargin);
        double radius = Math.Max(0, IslandCornerRadius - IslandContentMargin);
        _clockChipClip.RadiusX = radius;
        _clockChipClip.RadiusY = radius;
        _clockChipClip.Rect = horizontal
            ? new Rect((IslandContent.ActualWidth - inner) / 2, 0, inner, IslandContent.ActualHeight)
            : new Rect(0, (IslandContent.ActualHeight - inner) / 2, IslandContent.ActualWidth, inner);
        IslandContent.Clip = _clockChipClip;

        Clock.Opacity = 1 - Math.Clamp((progress - ClockTextFadeStart) / (ClockTextFadeEnd - ClockTextFadeStart), 0, 1);
        ClockDrops.Update(
            gap,
            Math.Clamp((progress - ClockDigitsStart) / (ClockDigitsEnd - ClockDigitsStart), 0, 1),
            Math.Clamp((progress - ClockLiquidStart) / (1 - ClockLiquidStart), 0, 1));

        double opacity = Math.Clamp(1 - progress / ClockChipFadeEnd, 0, 1);
        foreach (FrameworkElement section in _clockChipFades)
        {
            section.Opacity = opacity;
        }
    }

    /// <summary>Damla katmanları adanınkilerin yerine geçer; ayrılmadan önce şekil adayla aynı, geçiş görünmez.</summary>
    private void ShowDropLayers(bool show)
    {
        Visibility island = show ? Visibility.Hidden : Visibility.Visible;
        Visibility drops = show ? Visibility.Visible : Visibility.Collapsed;
        IslandShadow.Visibility = island;
        IslandTint.Visibility = island;
        IslandSheenLayer.Visibility = island;
        DropShadowShape.Visibility = drops;
        DropTint.Visibility = drops;
        DropSheen.Visibility = drops;
    }

    private bool UpdateCursorZone()
    {
        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        System.Drawing.Rectangle bounds = Screen.Bounds;

        // Gizliyken yalnızca ekranın en kenarı açar; açıkken adanın çevresindeki tüm şerit açık tutar.
        // Ada ortada olduğu için fare kenardan adaya giderken boşluktan geçer; bu sırada kapanmamalı.
        double thickness = Orientation == Orientation.Horizontal ? DesiredHeight : DesiredWidth;
        int depth = (int)Math.Round(_cursorInBarZone ? thickness * DpiScale : EdgeRevealDepth);

        bool inZone = bounds.Contains(cursor) && AppBarEdge switch
        {
            AppBarEdge.Top => cursor.Y < bounds.Top + depth,
            AppBarEdge.Left => cursor.X < bounds.Left + depth,
            AppBarEdge.Right => cursor.X >= bounds.Right - depth,
            _ => cursor.Y >= bounds.Bottom - depth
        };

        bool changed = inZone != _cursorInBarZone;
        _cursorInBarZone = inZone;
        return changed;
    }

    private void ShowAfterLayoutChange()
    {
        if (AppBarMode != AppBarMode.AutoHide)
        {
            return;
        }

        SetClockChip(false, animate: false);

        // Kenar değişince kayma ekseni de değişiyor; eski eksende kalan öteleme silinmeli.
        BarRoot.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null);
        BarRoot.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
        PeekDuringAutoHide();
    }
    #endregion

    #region Explorer restart
    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreatedMessage)
        {
            Dispatcher.BeginInvoke(HandleTaskbarCreated);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void HandleTaskbarCreated()
    {
        // Bu yayını tepsi servisi de yapıyor; yalnızca Explorer gerçekten yeniden başladıysa ilgilen.
        IntPtr windowsTray = WindowHelper.FindWindowsTray(_notificationArea.Handle);
        if (windowsTray == _windowsTray || windowsTray == IntPtr.Zero || AllowClose)
        {
            return;
        }
        _windowsTray = windowsTray;

        // Eski Explorer'la birlikte AppBar kaydı ve çalışma alanı da gitti. Otomatik gizlemede kayıt yok.
        if (AppBarMode == AppBarMode.Normal)
        {
            UnregisterAppBar();
            RegisterAppBar();
        }
        UpdatePosition();

        // Kapanan Dosya Gezgini pencereleri listede kalmasın. Görev servisi ortak; tek çubuk yenilesin.
        if (Screen.Primary)
        {
            _shellManager.Tasks.Dispose();
            _shellManager.Tasks.Initialize(true);
        }
    }
    #endregion

    #region Start button
    private void StartButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Kendi menümüz basınca açılır. Windows'unki bırakınca: basınca açılırsa bırakış onu kapatabilir.
        StartButton.ClickMode = _settings.CustomStartMenu ? ClickMode.Press : ClickMode.Release;

        // Tıklama Başlat menüsünü zaten kapatır; basmadan önce açık mıydı, burada bak.
        if (e.LeftButton == MouseButtonState.Released)
        {
            _startMenuWasOpen = IsStartMenuOpen();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.CustomStartMenu)
        {
            ToggleStartPanel();
            return;
        }

        ClosePanel();

        bool wasOpen = _startMenuWasOpen;
        _startMenuWasOpen = !wasOpen;

        if (!wasOpen)
        {
            ShellHelper.ShowStartMenu();
        }
    }

    private void StartButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShellHelper.ShowStartContextMenu();
        e.Handled = true;
    }

    public bool IsStartPanelOpen => HoldsBar(StartPopup);

    public Task WarmStartPanelAsync() => StartPanel.WarmAsync();

    /// <summary>IslandBar'ın Başlat menüsünü açar ya da kapatır; Başlat düğmesi ve Windows tuşu.</summary>
    public void ToggleStartPanel()
    {
        EndWarm();
        bool wasOpen = StartPopup.IsOpen;

        // Açık başka kartın görüntüsü alınırken beklemesin.
        if (!wasOpen)
        {
            StartOpening(StartPopup);
        }

        ClosePanel();
        if (wasOpen)
        {
            return;
        }

        // Küçük ekranda kart ekrana sığsın.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        StartPanel.CardHeight = Math.Min(StartPanelHeight, Screen.Bounds.Height / dpi.DpiScaleY - DesiredHeight - StartPanelScreenMargin);
        PlacePopup(StartPopup, StartPanel);
        ForceForeground();
        StartGlyph.IsMenuOpen = true;
        StartPopup.IsOpen = true;
    }

    private async void StartPopup_OpenedOrClosed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AllowAutoHide));

        if (!StartPopup.IsOpen)
        {
            StartGlyph.IsMenuOpen = false;
            StartPanel.Close();
            HideCard(StartPanel);
            return;
        }

        if (StartPopup != _warmingPopup)
        {
            StartPanel.FocusSearch();
        }
        await StartPanel.OpenAsync();

        if (StartPopup.IsOpen)
        {
            RevealPopup(StartPopup, StartPanel, StartButton);
        }
    }

    private static void OpenWindowsStartMenu()
    {
        WinKeyWatcher.AllowNativeStart();
        ShellHelper.ShowStartMenu();
    }

    /// <summary>
    /// Windows tuşu kancadan geliyor: Windows, o tuşu bu sürece vermiş saymıyor ve öne geçmeye izin vermeyebilir.
    /// Öndeki pencerenin girdisine bir an bağlanmak bu izni sağlar.
    /// </summary>
    private void ForceForeground()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
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
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    private static bool IsStartMenuOpen()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName is "StartMenuExperienceHost" or "SearchHost" or "SearchApp";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    #endregion

    #region Notification area
    private void Window_PositionChanged(object? sender, EventArgs e)
    {
        UpdateTrayHost();
    }

    private void UpdateTrayHost()
    {
        if (!Screen.Primary)
        {
            return;
        }

        // Uygulamalar görev çubuğunun yerini buradan öğreniyor (ABM_GETTASKBARPOS, açılır pencereler).
        _notificationArea.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = (NativeMethods.ABEdge)AppBarEdge,
            rc = new NativeMethods.Rect
            {
                Top = (int)(Top * DpiScale),
                Left = (int)(Left * DpiScale),
                Bottom = (int)((Top + ActualHeight) * DpiScale),
                Right = (int)((Left + ActualWidth) * DpiScale)
            }
        });
    }

    private void TrayIcon_MouseEnter(object sender, MouseEventArgs e)
    {
        var border = (FrameworkElement)sender;
        Point location = border.PointToScreen(new Point(0, 0));

        // Shell_NotifyIconGetRect için ikonun yeri.
        NotifyIcon icon = TrayIconOf(sender);
        icon.Placement = new NativeMethods.Rect
        {
            Top = (int)location.Y,
            Left = (int)location.X,
            Bottom = (int)(border.ActualHeight * DpiScale),
            Right = (int)(border.ActualWidth * DpiScale)
        };
        icon.IconMouseEnter(CursorParam());
        e.Handled = true;
    }

    private void TrayIcon_MouseLeave(object sender, MouseEventArgs e)
    {
        TrayIconOf(sender).IconMouseLeave(CursorParam());
        e.Handled = true;
    }

    private void TrayIcon_MouseMove(object sender, MouseEventArgs e)
    {
        TrayIconOf(sender).IconMouseMove(CursorParam());
        e.Handled = true;
    }

    private void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Pil, kartları açan öteki düğmeler gibi basınca açar; tepsi ikonu olmasa da düğmenin parçası.
        if (e.ChangedButton == MouseButton.Left && IsBatteryIcon(TrayIconOf(sender)))
        {
            ToggleQuickSettings();
            e.Handled = true;
            return;
        }

        // Tepsi kartı burada kapanmamalı: ikon kartın içindeyse bırakma olayı gelmez.
        ClosePopup(GroupPanel);
        UpdateTrayHost();

        // Sol tık Windows'taki gibi bırakınca gönderiliyor.
        if (e.ChangedButton != MouseButton.Left)
        {
            TrayIconOf(sender).IconMouseDown(e.ChangedButton, CursorParam(), System.Windows.Forms.SystemInformation.DoubleClickTime);
        }
        e.Handled = true;
    }

    private void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
    {
        NotifyIcon icon = TrayIconOf(sender);
        int doubleClickTime = System.Windows.Forms.SystemInformation.DoubleClickTime;

        // Windows'taki gibi pil, kendi penceresi yerine hızlı ayarları açar; basınca açıldı (TrayIcon_MouseDown).
        if (e.ChangedButton == MouseButton.Left && IsBatteryIcon(icon))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            icon.IconMouseDown(e.ChangedButton, CursorParam(), doubleClickTime);
        }
        icon.IconMouseUp(e.ChangedButton, CursorParam(), doubleClickTime);
        e.Handled = true;

        // Uygulama kendi menüsünü açarken kart yolda kalmasın.
        ClosePopup(TrayPopup);
    }

    private void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleQuickSettings();
    }

    private void ToggleQuickSettings()
    {
        EndWarm();
        bool wasOpen = QuickPopup.IsOpen;
        if (!wasOpen)
        {
            StartOpening(QuickPopup);
        }

        ClosePanel();

        if (!wasOpen)
        {
            PlacePopup(QuickPopup, QuickPanel);
            Activate();
            QuickPopup.IsOpen = true;
        }
    }

    private async void QuickPopup_OpenedOrClosed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AllowAutoHide));

        if (!QuickPopup.IsOpen)
        {
            QuickPanel.Close();
            HideCard(QuickPanel);
            UpdateQuickGlyphs();
            return;
        }

        await QuickPanel.OpenAsync();

        if (QuickPopup.IsOpen)
        {
            RevealPopup(QuickPopup, QuickPanel, QuickButton);
        }
    }

    private void UpdateQuickGlyphs()
    {
        if (!Screen.Primary)
        {
            return;
        }

        UpdateVolumeGlyph();
        QuickNetworkGlyph.Text = QuickSettingsPanel.NetworkGlyph();
    }

    private void UpdateVolumeGlyph()
    {
        QuickVolumeGlyph.Glyph = QuickSettingsPanel.VolumeGlyphFor(AudioEndpoint.TryOpen());
    }

    private void NetworkInformation_NetworkStatusChanged(object sender)
    {
        // Windows'un kendi iş parçacığından gelir; profil okumak arayüzü bekletmesin.
        string glyph = QuickSettingsPanel.NetworkGlyph();
        Dispatcher.BeginInvoke(() => QuickNetworkGlyph.Text = glyph);
    }

    private void TrayOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        EndWarm();
        bool wasOpen = TrayPopup.IsOpen;
        if (!wasOpen)
        {
            StartOpening(TrayPopup);
        }

        ClosePanel();

        if (!wasOpen)
        {
            PlacePopup(TrayPopup, TrayCard);
            Activate();
            TrayPopup.IsOpen = true;
        }
    }

    private void ToolsButton_Click(object sender, RoutedEventArgs e)
    {
        EndWarm();
        bool wasOpen = ToolsPopup.IsOpen;
        if (!wasOpen)
        {
            StartOpening(ToolsPopup);
        }

        ClosePanel();

        if (!wasOpen)
        {
            PlacePopup(ToolsPopup, ToolsCard);
            Activate();
            ToolsPopup.IsOpen = true;
        }
    }

    /// <summary>Kart bırakılacak yerin önünde durmasın: görünmez olunca fare de içinden geçer. Çubuk bu sırada gizlenmez.</summary>
    private void ToolsContent_DragStarted()
    {
        ToolsCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(ToolsDragFadeMs)));
    }

    private void ToolsContent_DragFinished(bool dropped)
    {
        ToolsCard.BeginAnimation(OpacityProperty, null);
        ToolsCard.Opacity = dropped ? 0 : 1;
        if (dropped)
        {
            ClosePopup(ToolsPopup);
        }
    }

    private static NotifyIcon TrayIconOf(object sender)
    {
        return (NotifyIcon)((FrameworkElement)sender).DataContext;
    }

    private static uint CursorParam()
    {
        System.Drawing.Point position = System.Windows.Forms.Cursor.Position;
        return unchecked((uint)((position.Y << 16) | (position.X & 0xFFFF)));
    }
    #endregion

    #region Clock
    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClock();
    }

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;

        // Her saniye bak: uykudan çıkınca veya sistem saati değişince de hemen düzelsin.
        long minute = now.Ticks / TimeSpan.TicksPerMinute;
        if (minute == _clockMinute)
        {
            return;
        }
        _clockMinute = minute;

        // Saat biçimi Windows bölge ayarından; ayraç yoksa hepsi saat kısmında kalır.
        string time = now.ToString("t");
        string separator = CultureInfo.CurrentCulture.DateTimeFormat.TimeSeparator;
        int at = separator.Length > 0 ? time.IndexOf(separator, StringComparison.Ordinal) : -1;
        ClockHours.Text = at < 0 ? time : time[..at];
        ClockSeparator.Text = at < 0 ? string.Empty : separator;
        ClockMinutes.Text = at < 0 ? string.Empty : time[(at + separator.Length)..];
        ClockDrops.SetTime(ClockHours.Text, ClockMinutes.Text);

        ClockDate.Text = now.ToString("ddd d MMM");
        Clock.ToolTip = now.ToString("D");

        UpdateQuickGlyphs();
    }

    private void Clock_MouseEnter(object sender, MouseEventArgs e)
    {
        // İnce çubukta iki satır sığmaz, dikey çubukta tarih ipucunda.
        if (_settings.ShowDate && !IsCompact && Orientation == Orientation.Horizontal)
        {
            AnimateClockFace(showDate: true);
        }
    }

    private void Clock_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateClockFace(showDate: false);
    }

    private void AnimateClockFace(bool showDate)
    {
        var duration = TimeSpan.FromMilliseconds(!SystemParameters.ClientAreaAnimation ? 0 : showDate ? ClockShowDateMs : ClockHideDateMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DoubleAnimation To(double value) => new(value, duration) { EasingFunction = ease };

        double scale = showDate ? ClockShrunkScale : 1;
        ClockTimeScale.BeginAnimation(ScaleTransform.ScaleXProperty, To(scale));
        ClockTimeScale.BeginAnimation(ScaleTransform.ScaleYProperty, To(scale));
        ClockTimeSlide.BeginAnimation(TranslateTransform.YProperty, To(showDate ? -ClockFace.ActualHeight * ClockLift : 0));
        ClockDate.BeginAnimation(OpacityProperty, To(showDate ? 1 : 0));
        ClockDateSlide.BeginAnimation(TranslateTransform.YProperty, To(showDate ? 0 : ClockDateRise));
    }
    #endregion

    #region Bar menu
    private void EdgeItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.Edge = (AppBarEdge)((MenuItem)sender).Tag;
    }

    private void ThicknessItem_Click(object sender, RoutedEventArgs e)
    {
        _settings.Thickness = ParseThickness((MenuItem)sender);
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Pencere durumu ayar değil; her pencere değişiminde çalışır.
        if (e.PropertyName == nameof(AppSettings.RunningGroupItems))
        {
            return;
        }

        // Menü ve ayarlar penceresi yalnızca ayarı değiştirir; her çubuk kendine uygular, kaydetmek App'te.
        if (e.PropertyName is nameof(AppSettings.Edge) or nameof(AppSettings.Thickness))
        {
            // Kartların konumu değişti; eski görüntüyle başlamasın.
            _prep.Clear();
            Chip.Dismiss();
        }
        else if (e.PropertyName == nameof(AppSettings.ShowNowPlayingChip) && !_settings.ShowNowPlayingChip)
        {
            Chip.Dismiss();
        }

        if (e.PropertyName == nameof(AppSettings.Edge))
        {
            AppBarEdge = _settings.Edge;
            UpdatePosition();
            ShowAfterLayoutChange();
        }
        else if (e.PropertyName == nameof(AppSettings.Thickness))
        {
            ApplyThickness();
            UpdatePosition();
            ShowAfterLayoutChange();
        }

        UpdateMenuChecks();
    }

    private void SettingsItem_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).OpenSettings();
    }

    private void BarWindow_Closed(object? sender, EventArgs e)
    {
        _settings.PropertyChanged -= Settings_PropertyChanged;
        _stage.Dispose();
        _nowPlaying?.Dispose();
        _clockTimer.Stop();
        _autoHideTimer.Stop();
        _glassTimer.Stop();

        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _dragOpenTimer.Stop();
        _gapCloseTimer.Stop();

        _volumeWatcher?.Dispose();
        _runTracker?.Dispose();
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;

        TaskList.ItemsSource = null;
        ((INotifyCollectionChanged)_taskWindows).CollectionChanged -= TaskWindows_CollectionChanged;
        ((INotifyCollectionChanged)_allWindows).CollectionChanged -= TaskWindows_CollectionChanged;
        _settings.Pinned.CollectionChanged -= TaskWindows_CollectionChanged;
        foreach (TaskGroup group in _taskGroups)
        {
            group.Detach();
        }
        (_taskWindows as ListCollectionView)?.DetachFromSourceCollection();
        (_allWindows as ListCollectionView)?.DetachFromSourceCollection();
        TrayList.ItemsSource = null;
        TrayOverflowList.ItemsSource = null;
        _trayBattery?.DetachFromSourceCollection();
        _trayOthers?.DetachFromSourceCollection();
    }

    protected override void SetScreenProperties(ScreenSetupReason reason)
    {
        // DPI değişimi yalnızca bu pencereyi ilgilendirir; ekran düzeni değiştiyse tüm çubuklar yeniden açılır.
        if (reason == ScreenSetupReason.DpiChange)
        {
            UpdatePosition();
        }
        else
        {
            ((App)Application.Current).NotifyDisplayChange();
        }
    }

    private void ExitItem_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ExitGracefully();
    }

    private void UpdateMenuChecks()
    {
        foreach (MenuItem item in EdgeMenu.Items)
        {
            item.IsChecked = (AppBarEdge)item.Tag == _settings.Edge;
        }

        foreach (MenuItem item in ThicknessMenu.Items)
        {
            item.IsChecked = ParseThickness(item) == _settings.Thickness;
        }
    }

    private static double ParseThickness(MenuItem item)
    {
        return double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
    }
    #endregion

    #region Groups
    private void NewGroupItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameDialog("") { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _settings.Groups.Add(new AppGroup { Name = dialog.GroupName });
        _settings.Save();
    }

    private void PanelNewGroup_Click(object sender, RoutedEventArgs e)
    {
        NewGroupItem_Click(sender, e);
        ShowGroupsPanel();
    }

    private void RenameGroupItem_Click(object sender, RoutedEventArgs e)
    {
        RenameGroup(GroupOf(sender), this);
        ShowGroupsPanel();
    }

    internal void RenameGroup(AppGroup group, Window owner)
    {
        var dialog = new NameDialog(group.Name) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        group.Name = dialog.GroupName;
        _settings.Save();
    }

    private void ChooseIconItem_Click(object sender, RoutedEventArgs e)
    {
        AppGroup group = GroupOf(sender);

        var dialog = new OpenFileDialog
        {
            DereferenceLinks = false,
            Filter = "İkonlar ve programlar|*.ico;*.exe;*.lnk;*.dll;*.png|Tüm dosyalar|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            group.IconPath = dialog.FileName;
            _settings.Save();
        }
        ShowGroupsPanel();
    }

    private void ResetIconItem_Click(object sender, RoutedEventArgs e)
    {
        GroupOf(sender).IconPath = null;
        _settings.Save();
    }

    private void DeleteGroupItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteGroup(GroupOf(sender), this);
        ShowGroupsPanel();
    }

    internal void DeleteGroup(AppGroup group, Window owner)
    {
        if (group.Items.Count > 0 && MessageBox.Show(owner,
                $"\"{group.Name}\" grubu ve içindeki {group.Items.Count} öğe silinsin mi? Programların kendisi silinmez.",
                "Grubu sil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Groups.Remove(group);
        group.Dispose();
        _settings.Save();
    }

    private static AppGroup GroupOf(object sender)
    {
        return (AppGroup)((FrameworkElement)sender).DataContext;
    }
    #endregion

    #region Pinned apps
    private void PinFilesItem_Click(object sender, RoutedEventArgs e)
    {
        AddItems(_settings.Pinned, PickFiles());
    }

    private void PinFolderItem_Click(object sender, RoutedEventArgs e)
    {
        AddItems(_settings.Pinned, PickFolders());
    }

    private void PinStoreAppItem_Click(object sender, RoutedEventArgs e)
    {
        AddItems(_settings.Pinned, PickStoreApps());
    }


    internal void Unpin(GroupItem item)
    {
        _settings.Pinned.Remove(item);
        _settings.Save();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool canDrop = SetDropEffect(e);

        // Açılır kartlardaki sürükleme de mantıksal ağaçtan buraya ulaşır.
        if (canDrop && e.OriginalSource is Visual source && PresentationSource.FromVisual(source) == PresentationSource.FromVisual(this))
        {
            ShowBarGap(e);
        }
        else
        {
            CloseBarGap();
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // Çubuğun içinde bir öğeden ötekine geçerken de gelir; hemen ardından DragOver gelmezse gerçekten çıkmıştır.
        if (_gapPaths != null)
        {
            _gapCloseTimer.Stop();
            _gapCloseTimer.Start();
        }
    }

    private void GapCloseTimer_Tick(object? sender, EventArgs e)
    {
        CloseBarGap();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        // Grup düğmeleri bırakmayı kendileri işliyor; buraya gelen çubuğun geri kalanına bırakılmıştır.
        if (!e.Data.GetDataPresent(ToolsPanel.InternalDragFormat) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            PinDropped(paths);
        }

        CloseBarGap();
        e.Handled = true;
    }

    private void TaskPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _taskPanel = (TaskPanel)sender;
    }

    private void ShowBarGap(DragEventArgs e)
    {
        _gapCloseTimer.Stop();
        if (_taskPanel == null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        // Başlatıcıların üzerinde yer açılmaz: açılan boşluk adayı kaydırır, imleç Gruplar'a girip çıkarak titrerdi.
        Point cursor = e.GetPosition(_taskPanel);
        if (_taskPanel.IsBefore(cursor))
        {
            CloseBarGap();
            return;
        }

        int slots = Math.Min(paths.Length, MaxGapSlots);
        bool opening = _gapPaths == null;
        _gapPaths ??= paths;
        _taskPanel.ShowGap(_taskPanel.SlotAt(cursor, slots), slots);
        if (opening)
        {
            LoadGapIcons(paths, slots);
        }
    }

    private async void LoadGapIcons(string[] paths, int slots)
    {
        var ink = (IValueConverter)FindResource("IconInk");
        for (int i = 0; i < slots; i++)
        {
            ImageSource? icon = await AppIcon.LoadAsync(paths[i]);
            if (_gapPaths != paths)
            {
                return;
            }

            if (icon != null)
            {
                _gapIcons[paths[i]] = icon;
                if (ink.Convert(icon, typeof(ImageSource), null, CultureInfo.CurrentCulture) is ImageSource shown)
                {
                    _taskPanel?.SetGhostIcon(i, shown);
                }
            }
        }
    }

    private void CloseBarGap()
    {
        _gapCloseTimer.Stop();
        if (_gapPaths != null)
        {
            _gapPaths = null;
            _gapIcons.Clear();
            _taskPanel?.CloseGap();
        }
    }

    /// <summary>Bırakılanları boşluğa yerleştirir. Zaten sabitliyse yeni kopya eklenmez, yeri değişir.</summary>
    private void PinDropped(string[] paths)
    {
        int at = _taskPanel is { GapIndex: >= 0 } panel ? panel.GapIndex : _taskGroups.Count;
        var items = new List<GroupItem>();
        foreach (string path in paths)
        {
            GroupItem item = GroupItem.FromPath(path);
            if (_gapIcons.TryGetValue(path, out ImageSource? icon))
            {
                item.UseIcon(icon);
            }
            if (items.All(existing => !SamePath(existing, item)))
            {
                items.Add(item);
            }
        }

        _taskPanel?.FillGap(items.Count(item => !_settings.Pinned.Any(pin => SamePath(pin, item))));
        _gapPaths = null;
        _gapIcons.Clear();

        foreach (GroupItem item in items)
        {
            at = PinAt(item, Math.Min(at, _taskGroups.Count));
        }

        _settings.Save();
        _runTracker?.Refresh();
    }

    /// <returns>Sıradaki öğenin gireceği yer.</returns>
    private int PinAt(GroupItem item, int at)
    {
        TaskGroup? existing = _taskGroups.FirstOrDefault(group => group.Pin != null && SamePath(group.Pin, item));
        if (existing != null)
        {
            int to = _taskGroups.IndexOf(existing) < at ? at - 1 : at;
            MoveTaskGroup(existing, to);
            return to + 1;
        }

        // Önce düğme: SyncTaskGroups onu bu yerde bulur, yoksa sabitlenenlerin sonuna koyar.
        _taskGroups.Insert(at, new TaskGroup(PinnedKeyPrefix + item.Path, item));
        _settings.Pinned.Insert(PinIndexBefore(at + 1), item);
        return at + 1;
    }

    /// <summary>Düğmeyi taşır; sabitliyse sırası kalıcı olsun diye listede de yerine koyar.</summary>
    private void MoveTaskGroup(TaskGroup group, int to)
    {
        _taskGroups.Move(_taskGroups.IndexOf(group), to);
        if (group.Pin is { } pin)
        {
            int from = _settings.Pinned.IndexOf(pin);
            int next = PinIndexBefore(to + 1);
            int target = from < next ? next - 1 : next;
            if (from != target)
            {
                _settings.Pinned.Move(from, target);
            }
        }
    }

    /// <summary>Çubukta bu sıradan sonraki ilk pinin listedeki yeri; yoksa listenin sonu.</summary>
    private int PinIndexBefore(int taskIndex)
    {
        GroupItem? next = _taskGroups.Skip(taskIndex).FirstOrDefault(group => group.Pin != null)?.Pin;
        return next != null ? _settings.Pinned.IndexOf(next) : _settings.Pinned.Count;
    }

    private static bool SamePath(GroupItem first, GroupItem second)
    {
        return string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase);
    }
    #endregion

    #region Running windows
    private void TaskWindows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncTaskGroups();
    }

    private void SyncTaskGroups()
    {
        var pinGroups = new List<TaskGroup>();
        var pinnedWindows = new HashSet<ApplicationWindow>();

        // Sabitlenenler kendi sırasıyla: pencereleri düğmeye toplanır, ayrı düğme açılmaz.
        foreach (GroupItem pin in _settings.Pinned.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            string key = PinnedKeyPrefix + pin.Path;
            TaskGroup group = _taskGroups.FirstOrDefault(existing => existing.Key == key) ?? new TaskGroup(key, pin);
            List<ApplicationWindow> windows = _allWindows.OfType<ApplicationWindow>()
                .Where(window => !pinnedWindows.Contains(window) && pin.Matches(window))
                .ToList();
            pinnedWindows.UnionWith(windows);
            group.SetWindows(windows);
            pinGroups.Add(group);
        }

        var windowsByKey = new Dictionary<string, List<ApplicationWindow>>();
        foreach (ApplicationWindow window in _taskWindows.OfType<ApplicationWindow>().Where(window => !pinnedWindows.Contains(window)))
        {
            string key = TaskGroup.KeyOf(window);
            if (!windowsByKey.TryGetValue(key, out List<ApplicationWindow>? windows))
            {
                windowsByKey[key] = windows = new List<ApplicationWindow>();
            }
            windows.Add(window);
        }

        // Düğmeler yer değiştirmesin: var olanlar yerinde kalır, yeniler sona eklenir. Kullanıcı sabitlenmemiş bir uygulamayı
        // pinlerin arasına sürüklemiş olabilir: pin yuvaları pinlerin güncel sırasıyla dolar, öbürleri arasında yerini korur.
        var wanted = new List<TaskGroup>();
        var nextPin = new Queue<TaskGroup>(pinGroups);
        int afterLastPin = 0;
        foreach (TaskGroup group in _taskGroups)
        {
            if (!group.IsPinned)
            {
                if (windowsByKey.TryGetValue(group.Key, out List<ApplicationWindow>? groupWindows))
                {
                    group.SetWindows(groupWindows);
                    wanted.Add(group);
                }
            }
            else if (pinGroups.Contains(group))
            {
                wanted.Add(nextPin.Dequeue());
                afterLastPin = wanted.Count;
            }
        }
        wanted.InsertRange(afterLastPin, nextPin);

        foreach ((string key, List<ApplicationWindow> windows) in windowsByKey)
        {
            if (wanted.All(group => group.Key != key))
            {
                var group = new TaskGroup(key);
                group.SetWindows(windows);
                wanted.Add(group);
            }
        }

        foreach (TaskGroup group in _taskGroups.Except(wanted).ToList())
        {
            group.Detach();
            _taskGroups.Remove(group);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            int at = _taskGroups.IndexOf(wanted[i]);
            if (at < 0)
            {
                _taskGroups.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                _taskGroups.Move(at, i);
            }
        }

        if (PreviewPopup.IsOpen)
        {
            if (PreviewPopup.DataContext is TaskGroup { Windows.Count: > 0 } shown)
            {
                SizePreviewCard(shown);
            }
            else
            {
                HidePreview();
            }
        }

        // Sürüklenen uygulama bu sırada kapandıysa bırakılacak bir şey kalmadı.
        if (_draggedGroup != null && !_taskGroups.Contains(_draggedGroup))
        {
            EndTaskDrag(commit: false);
        }
    }

    private void TaskButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Çubuğa tıklamak pencereyi etkin olmaktan çıkarabilir; basıldığı andaki durumu sakla.
        _pressedTaskGroup = GroupOfTask(sender);
        _pressedActiveWindow = _pressedTaskGroup.ActiveWindow;
        _taskDragStart = e.GetPosition(TaskList);
    }

    private void TaskIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (((Image)sender).Parent is not Button button || _taskPanel == null || _taskPanel.TakeIsNew(GroupOfTask(button)))
        {
            return;
        }

        // Şablonun giriş animasyonu düğmenin Loaded'ında başlar, koşula bağlanamaz. Loaded önce üst öğeye gelir ve kare
        // bundan hemen sonra çizilir: burada kaldırmak gerekir, sonraya bırakılınca simge bir kare boş görünüyordu.
        if (button.Template.FindName("Root", button) is UIElement root && button.Template.FindName("Enter", button) is ScaleTransform enter)
        {
            root.BeginAnimation(OpacityProperty, null);
            enter.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            enter.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private void TaskButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Düğme basılıyken fareyi kendisi yakalar; başka yerde başlayan basışla sürükleme başlamasın.
        var button = (Button)sender;
        if (e.LeftButton != MouseButtonState.Pressed || _taskDragStart == null || _taskPanel == null || !button.IsMouseCaptured)
        {
            return;
        }

        Vector moved = e.GetPosition(TaskList) - _taskDragStart.Value;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Yakalama listeye geçince düğme basılı olmaktan çıkar, bırakınca tıklama sayılmaz.
        TaskGroup group = GroupOfTask(button);
        _taskDragStart = null;
        _pressedTaskGroup = null;
        _pressedActiveWindow = null;
        ClosePanel();
        HidePreview();
        _draggedGroup = group;
        TaskList.CaptureMouse();
        _taskPanel.Lift(group, e.GetPosition(_taskPanel));
        OnPropertyChanged(nameof(AllowAutoHide));
    }

    private void TaskList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedGroup == null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTaskDrag(commit: true);
            return;
        }

        _taskPanel?.MoveLifted(e.GetPosition(_taskPanel));
        e.Handled = true;
    }

    private void TaskList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedGroup != null)
        {
            EndTaskDrag(commit: true);
            e.Handled = true;
        }
    }

    private void TaskList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Düğmenin kaybettiği yakalama da buradan geçer. Sistem aldıysa (başka pencere öne geldi) sürükleme iptal.
        if (_draggedGroup != null && !TaskList.IsMouseCaptured)
        {
            EndTaskDrag(commit: false);
        }
    }

    private void EndTaskDrag(bool commit)
    {
        TaskGroup? group = _draggedGroup;
        _draggedGroup = null;
        int to = _taskPanel?.Release() ?? -1;
        TaskList.ReleaseMouseCapture();
        OnPropertyChanged(nameof(AllowAutoHide));

        int from = group != null ? _taskGroups.IndexOf(group) : -1;
        if (commit && from >= 0 && to >= 0 && to < _taskGroups.Count && to != from)
        {
            MoveTaskGroup(group!, to);
            if (group!.IsPinned)
            {
                _settings.Save();
            }
        }
    }

    private void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        TaskGroup group = GroupOfTask(sender);
        ApplicationWindow? active = _pressedTaskGroup == group ? _pressedActiveWindow : group.ActiveWindow;
        _pressedTaskGroup = null;
        _pressedActiveWindow = null;
        _taskDragStart = null;

        ClosePanel();

        // Kapalı pin açılır; açıkken Ctrl yeni kopya açar.
        if (group.Pin is { } pin && (!group.HasWindows || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            ReportFailures(Launcher.Launch(pin) ? [] : [pin]);
            return;
        }

        if (group.HasMany)
        {
            group.NextWindow(active).BringToFront();
        }
        else if (active is { CanMinimize: true })
        {
            active.Minimize();
        }
        else
        {
            group.Windows[0].BringToFront();
        }
    }

    private void LaunchPinItem_Click(object sender, RoutedEventArgs e)
    {
        GroupItem pin = GroupOfTask(sender).Pin!;
        ReportFailures(Launcher.Launch(pin) ? [] : [pin]);
    }

    private void UnpinTaskItem_Click(object sender, RoutedEventArgs e)
    {
        Unpin(GroupOfTask(sender).Pin!);
    }

    private void PinTaskItem_Click(object sender, RoutedEventArgs e)
    {
        if (PinItemFor(GroupOfTask(sender).Windows[0]) is { } item)
        {
            AddItems(_settings.Pinned, [item]);
        }
    }

    /// <summary>Çalışan pencereden sabitlenecek öğe: Store uygulamasında kimlik, diğerlerinde exe.</summary>
    private static GroupItem? PinItemFor(ApplicationWindow window)
    {
        string? packagedId = window.IsUWP && !string.IsNullOrEmpty(window.AppUserModelID) ? window.AppUserModelID : PackagedAppId(window.ProcId);
        if (packagedId != null)
        {
            string path = GroupItem.StoreAppPrefix + packagedId;
            using var shell = new ManagedShell.ShellFolders.ShellItem(path);
            return new GroupItem { Name = string.IsNullOrWhiteSpace(shell.DisplayName) ? window.Title : shell.DisplayName, Path = path };
        }

        if (string.IsNullOrEmpty(window.WinFileName))
        {
            return null;
        }

        string? description = FileVersionInfo.GetVersionInfo(window.WinFileName).FileDescription;
        return new GroupItem
        {
            Name = string.IsNullOrWhiteSpace(description) ? System.IO.Path.GetFileNameWithoutExtension(window.WinFileName) : description,
            Path = window.WinFileName
        };
    }

    /// <summary>Paketli masaüstü uygulamasının kimliği; pencere yazmıyor, exe'si de doğrudan başlatılamıyor.</summary>
    private static string? PackagedAppId(uint? processId)
    {
        const uint queryLimited = 0x1000;
        IntPtr process = processId is { } id ? OpenProcess(queryLimited, false, id) : IntPtr.Zero;
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            uint length = 512;
            var text = new System.Text.StringBuilder((int)length);
            return GetApplicationUserModelId(process, ref length, text) == 0 ? text.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder id);

    private void CloseTaskItem_Click(object sender, RoutedEventArgs e)
    {
        foreach (ApplicationWindow window in GroupOfTask(sender).Windows.ToList())
        {
            window.Close();
        }
    }

    private static TaskGroup GroupOfTask(object sender)
    {
        return (TaskGroup)((FrameworkElement)sender).DataContext;
    }
    #endregion

    #region Window preview
    private void TaskButton_MouseEnter(object sender, MouseEventArgs e)
    {
        var button = (Button)sender;
        _previewHideTimer.Stop();

        // Çipin üzerine gelen fare o an arkasında kalan düğmeyi istemiyor, çubuğu açmak istiyor.
        if (_draggedGroup != null || ClockChipProgress > 0 || !GroupOfTask(button).HasWindows || GroupPanel.IsOpen || TrayPopup.IsOpen || ToolsPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen || button.ContextMenu?.IsOpen == true)
        {
            return;
        }

        _previewButton = button;

        // Önizleme açıksa düğmeler arasında gezinirken beklemeden geçsin.
        if (PreviewPopup.IsOpen)
        {
            ShowPreview(button);
        }
        else
        {
            _previewShowTimer.Start();
        }
    }

    private void TaskButton_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewShowTimer.Stop();

        if (PreviewPopup.IsOpen)
        {
            _previewHideTimer.Start();
        }
    }

    private void TaskButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        HidePreview();
    }

    private void PreviewCard_MouseEnter(object sender, MouseEventArgs e)
    {
        _previewHideTimer.Stop();
    }

    private void PreviewCard_MouseLeave(object sender, MouseEventArgs e)
    {
        _previewHideTimer.Start();
    }

    private void PreviewShowTimer_Tick(object? sender, EventArgs e)
    {
        _previewShowTimer.Stop();

        if (_previewButton is { IsMouseOver: true } button)
        {
            ShowPreview(button);
        }
    }

    private void PreviewHideTimer_Tick(object? sender, EventArgs e)
    {
        HidePreview();
    }

    private void ShowPreview(Button button)
    {
        TaskGroup group = GroupOfTask(button);

        PreviewPopup.DataContext = group;
        PreviewPopup.PlacementTarget = button;
        SizePreviewCard(group);
        PlacePopup(PreviewPopup, PreviewCard);

        if (PreviewPopup.IsOpen)
        {
            _ = PreviewCard.RefreshGlassAsync(animateTint: true);
        }
        else
        {
            Chip.Dismiss();
            PreviewPopup.IsOpen = true;
        }
    }

    private void HidePreview()
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _previewButton = null;
        PreviewPopup.IsOpen = false;
    }

    private void SizePreviewCard(TaskGroup group)
    {
        // Yatay çubukta yan yana, yan çubukta alt alta; ekrana sığmayan alt satıra (sütuna) geçer.
        double margin = 2 * (PanelShadowMargin + PreviewCard.Padding.Left);
        int count = group.Windows.Count;
        int columns;
        if (Orientation == Orientation.Horizontal)
        {
            double room = Screen.Bounds.Width / DpiScale - margin;
            columns = Math.Clamp(count, 1, Math.Max(1, (int)(room / PreviewTileWidth)));
        }
        else
        {
            double room = Screen.Bounds.Height / DpiScale - margin;
            int rows = Math.Max(1, (int)(room / PreviewTileHeight));
            columns = (int)Math.Ceiling(count / (double)rows);
        }

        PreviewCard.Width = columns * PreviewTileWidth + 2 * PreviewCard.Padding.Left;
    }

    private void PreviewTile_Click(object sender, RoutedEventArgs e)
    {
        var window = (ApplicationWindow)((FrameworkElement)sender).DataContext;
        ClosePanel();
        window.BringToFront();
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        ((ApplicationWindow)((FrameworkElement)sender).DataContext).Close();
        e.Handled = true;
    }
    #endregion

    #region Panel
    private void GroupsButton_Click(object sender, RoutedEventArgs e)
    {
        EndWarm();
        bool wasOpen = GroupPanel.IsOpen;
        if (!wasOpen)
        {
            StartOpening(GroupPanel);
        }

        ClosePanel();

        if (!wasOpen)
        {
            ShowGroupsPanel();
        }
    }

    private void ShowGroupsPanel()
    {
        GroupPanel.PlacementTarget = GroupsButton;
        StartOpening(GroupPanel);
        _runTracker?.Refresh();
        PlacePopup(GroupPanel, PanelCard);
        OpenPanel();
    }

    private void PlacePopup(Popup popup, FrameworkElement card)
    {
        // Kart gölge payı kadar içeride; düğmeye ortalı ve adanın yanında süzülür. Pencereye bağlı kart zaten adadan FloatGap uzakta.
        double gap = popup.PlacementTarget == this ? -PanelShadowMargin : IslandPadding + FloatGap - PanelShadowMargin;
        double popupWidth = card.Width + 2 * PanelShadowMargin;
        double popupHeight = card.ActualHeight + 2 * PanelShadowMargin;

        if (popup.PlacementTarget is not FrameworkElement target)
        {
            return;
        }

        (popup.Placement, popup.HorizontalOffset, popup.VerticalOffset) = AppBarEdge switch
        {
            AppBarEdge.Top => (PlacementMode.Bottom, (target.ActualWidth - popupWidth) / 2, gap),
            AppBarEdge.Left => (PlacementMode.Right, gap, (target.ActualHeight - popupHeight) / 2),
            AppBarEdge.Right => (PlacementMode.Left, -gap, (target.ActualHeight - popupHeight) / 2),
            _ => (PlacementMode.Top, (target.ActualWidth - popupWidth) / 2, -gap)
        };
    }

    private void PopupCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Yan kenarlarda dikey ortalama kartın yüksekliğine bağlı; öğeler sonradan da değişebilir.
        if (sender == PanelCard)
        {
            PlacePopup(GroupPanel, PanelCard);
        }
        else if (sender == TrayCard)
        {
            PlacePopup(TrayPopup, TrayCard);
        }
        else if (sender == ToolsCard)
        {
            PlacePopup(ToolsPopup, ToolsCard);
        }
        else if (sender == QuickPanel)
        {
            PlacePopup(QuickPopup, QuickPanel);
        }
        else if (sender == StartPanel)
        {
            PlacePopup(StartPopup, StartPanel);
        }
        else
        {
            PlacePopup(PreviewPopup, PreviewCard);
        }
    }

    /// <summary>Kartın önceden alınmış görüntüsü ve yeri: basışta hiçbir şey beklemeden animasyon başlasın diye.</summary>
    private sealed record PopupPrep(BitmapSource Image, double Width, double Height, Point Origin, Vector FromTarget, long TakenAt);

    private (FrameworkElement Card, FrameworkElement Button) CardAndButton(Popup popup)
    {
        return popup == GroupPanel ? (PanelCard, GroupsButton)
            : popup == TrayPopup ? (TrayCard, TrayOverflowButton)
            : popup == ToolsPopup ? (ToolsCard, ToolsButton)
            : popup == QuickPopup ? (QuickPanel, QuickButton)
            : (StartPanel, StartButton);
    }

    private GenieEffect PlayOpening(Popup popup, FrameworkElement card, BitmapSource image, GenieLayout layout)
    {
        GenieEffect? genie = null;
        genie = new GenieEffect(_stage, image, layout, AppBarEdge, opening: true, visible: true, shown: null, finished: () =>
        {
            if (_genieOpening.TryGetValue(popup, out GenieEffect? current) && current == genie)
            {
                _genieOpening.Remove(popup);
                card.Opacity = 1;
            }
            GenieFinished();
        });
        _genieOpening[popup] = genie;
        _geniesPlaying++;
        _stage.Play(genie);
        return genie;
    }

    /// <summary>Basışta çağrılır: animasyon hazır görüntüyle hemen başlar, gerçek kart arkada hazırlanır.</summary>
    private void StartOpening(Popup popup)
    {
        // Hemen: açılan kartın camına karışmasın.
        Chip.Dismiss(immediately: true);

        if (!SystemParameters.ClientAreaAnimation || _genieOpening.ContainsKey(popup) || !_prep.TryGetValue(popup, out PopupPrep? prep)
            || popup.PlacementTarget is not FrameworkElement target)
        {
            return;
        }

        (FrameworkElement card, FrameworkElement button) = CardAndButton(popup);
        Point origin = target.PointToScreen(new Point()) + prep.FromTarget;
        PlayOpening(popup, card, prep.Image, GenieEffect.Measure(origin, prep.Width, prep.Height, button, AppBarEdge, VisualTreeHelper.GetDpi(this)));
    }

    /// <summary>Gerçek kart hazır: bir sonraki açılış için görüntüsü saklanır, oynayan animasyon buna geçer ve bitince kart görünür.</summary>
    private void RevealPopup(Popup popup, FrameworkElement card, FrameworkElement button)
    {
        bool warming = popup == _warmingPopup;
        if (!SystemParameters.ClientAreaAnimation && !warming)
        {
            card.Opacity = 1;
            return;
        }

        var root = (FrameworkElement)popup.Child;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        card.Opacity = 1;
        BitmapSource image = GenieEffect.Snapshot(root, dpi);
        card.Opacity = 0;

        Point origin = root.PointToScreen(new Point());
        GenieLayout layout = GenieEffect.Measure(origin, root.ActualWidth, root.ActualHeight, button, AppBarEdge, dpi);
        if (popup.PlacementTarget is FrameworkElement target)
        {
            _prep[popup] = new PopupPrep(image, root.ActualWidth, root.ActualHeight, origin, origin - target.PointToScreen(new Point()), Environment.TickCount64);
        }

        if (warming)
        {
            // Animasyonun kodu da ısınsın.
            _stage.Play(new GenieEffect(_stage, image, layout, AppBarEdge, opening: true, visible: false, shown: null, finished: () => { }));
            _warmDone?.TrySetResult();
            return;
        }

        if (!_genieOpening.TryGetValue(popup, out GenieEffect? genie))
        {
            genie = PlayOpening(popup, card, image, layout);
        }
        genie.CardReady(image, layout);
    }

    /// <summary>Kartı düğmesine akıtarak kapatır. Kopya ekrana çıkınca gerçek kart kapanır, boşluk görünmez.</summary>
    private void ClosePopup(Popup popup)
    {
        if (!popup.IsOpen || _genieClosing.Contains(popup))
        {
            return;
        }

        // Kapanış kartın gitmesini beklemeden görünsün.
        if (popup == StartPopup)
        {
            StartGlyph.IsMenuOpen = false;
        }

        (FrameworkElement card, FrameworkElement button) = CardAndButton(popup);

        // Hâlâ açılıyorsa kopya zaten üstte: bulunduğu yerden geri döner.
        if (_genieOpening.Remove(popup, out GenieEffect? opening))
        {
            opening.Reverse();
            popup.IsOpen = false;
            return;
        }

        // Görüntüsü henüz hazır olmayan kart hiç görünmedi.
        if (card.Opacity == 0 || !SystemParameters.ClientAreaAnimation)
        {
            popup.IsOpen = false;
            return;
        }

        var root = (FrameworkElement)popup.Child;

        // Çubuk kapanırken (ekran düzeni değişti) Windows önce kartın penceresini yok eder, sonra çubuğa etkinliğini
        // kaybettiğini bildirir: IsOpen hâlâ doğru ama konum sorulamaz, animasyon da oynayacak yer kalmadı.
        if (PresentationSource.FromVisual(root) == null || PresentationSource.FromVisual(button) == null)
        {
            popup.IsOpen = false;
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        // Başlat'ın görüntüsünü almak en pahalısı; arama ve kaydırma değişmediyse açılıştaki görüntü aynısı.
        BitmapSource image = popup == StartPopup && StartPanel.IsAtRest && _prep.TryGetValue(popup, out PopupPrep? prep)
            ? prep.Image
            : GenieEffect.Snapshot(root, dpi);
        GenieLayout layout = GenieEffect.Measure(root.PointToScreen(new Point()), root.ActualWidth, root.ActualHeight, button, AppBarEdge, dpi);

        _genieClosing.Add(popup);
        _geniesPlaying++;
        _stage.Play(new GenieEffect(_stage, image, layout, AppBarEdge, opening: false, visible: true, shown: () =>
        {
            _genieClosing.Remove(popup);
            popup.IsOpen = false;
        }, finished: GenieFinished));
    }

    #region Now playing chip
    private void NowPlaying_TrackStarted(NowPlayingTrack track)
    {
        // Açık kart varsa kalkmasın; Hızlı ayarlar şarkıyı zaten gösteriyor.
        if (!_settings.ShowNowPlayingChip || _fullScreenOnThisBar || IsAnyPanelActive() || VolumePopup.IsOpen || NowPlayingChip.IsUserBusy())
        {
            return;
        }

        Chip.SetTrack(track);
        if (ChipPopup.IsOpen)
        {
            PlaceChip();
            Chip.Renew();
            return;
        }

        _nowPlaying!.WantsUpdates = true;
        ChipPopup.IsOpen = true;
    }

    private void NowPlaying_TrackUpdated(NowPlayingTrack track)
    {
        if (ChipPopup.IsOpen && !Chip.IsHiding)
        {
            Chip.SetCover(track);
        }
    }

    private async void ChipPopup_Opened(object? sender, EventArgs e)
    {
        // Kapalı Popup'ın içeriği ölçülemiyor (WPF yerleşimi askıya alıyor); kart henüz görünmezken burada yerleşir.
        PlaceChip();
        await Chip.RevealAsync(TowardBar());
    }

    private void ChipPopup_Closed(object? sender, EventArgs e)
    {
        _nowPlaying!.WantsUpdates = false;
    }

    private void Chip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Yeni şarkının adı başka uzunlukta: arkası yeniden alınsın.
        if (Chip.IsShown && !Chip.IsHiding)
        {
            _ = Chip.RefreshGlassAsync();
        }
    }

    /// <summary>Çubuğun açılıp kapanmasına göre kartın yeri değişti.</summary>
    private void MoveChip()
    {
        Point old = _chipPlace;
        PlaceChip();
        Chip.SlideFrom(old - _chipPlace, TowardBar());
    }

    private Vector TowardBar()
    {
        return AppBarEdge switch
        {
            AppBarEdge.Top => new Vector(0, -1),
            AppBarEdge.Left => new Vector(-1, 0),
            AppBarEdge.Right => new Vector(1, 0),
            _ => new Vector(0, 1)
        };
    }

    /// <summary>
    /// Çubuk görünürken hemen yanında, gizliyken adanın durduğu yerde; ikisinde de ekranın ortasında.
    /// Popup ekran dışına taşamaz: gizliyken kenar tarafındaki gölge payı daraltılmazsa kart yukarı itilirdi.
    /// </summary>
    private void PlaceChip()
    {
        _chipAtBarPlace = IsIslandOffScreen();
        double shadow = PanelShadowMargin;
        double near = _chipAtBarPlace ? FloatGap : shadow;

        ChipRoot.Padding = AppBarEdge switch
        {
            AppBarEdge.Top => new Thickness(shadow, near, shadow, shadow),
            AppBarEdge.Left => new Thickness(near, shadow, shadow, shadow),
            AppBarEdge.Right => new Thickness(shadow, shadow, near, shadow),
            _ => new Thickness(shadow, shadow, shadow, near)
        };

        // Şarkı adının bekleyen ölçüsü önce üst öğelere ulaşsın; yoksa aynı kısıtla sorulan Measure önceki şarkının boyunu döner.
        ChipRoot.UpdateLayout();
        ChipRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Küçük pay: tam sığan ad yuvarlama yüzünden son harflerinden kesiliyordu.
        double width = Math.Ceiling(ChipRoot.DesiredSize.Width) + ChipTextSlack;
        double height = ChipRoot.DesiredSize.Height;

        // Boyut elle verilir: yeniden açılan Popup önceki boyunu koruyor, uzun addan sonra kart geniş kalıyordu.
        ChipPopup.Width = width;
        ChipPopup.Height = height;
        double centerX = (ActualWidth - width) / 2;
        double centerY = (ActualHeight - height) / 2;

        (ChipPopup.HorizontalOffset, ChipPopup.VerticalOffset) = (AppBarEdge, _chipAtBarPlace) switch
        {
            (AppBarEdge.Top, true) => (centerX, 0),
            (AppBarEdge.Top, false) => (centerX, ActualHeight - shadow),
            (AppBarEdge.Left, true) => (0, centerY),
            (AppBarEdge.Left, false) => (ActualWidth - shadow, centerY),
            (AppBarEdge.Right, true) => (ActualWidth - width, centerY),
            (AppBarEdge.Right, false) => (shadow - width, centerY),
            (_, true) => (centerX, ActualHeight - height),
            _ => (centerX, shadow - height)
        };

        _chipPlace = new Point(ChipPopup.HorizontalOffset + ChipRoot.Padding.Left, ChipPopup.VerticalOffset + ChipRoot.Padding.Top);
    }
    #endregion

    #region Volume chip
    /// <summary>Ses tuşuna basıldı. Hızlı ayarlar açıksa orada kendi kaydırıcısı oynuyor, kart üstüne çıkmaz.</summary>
    internal void ShowVolume(AudioEndpoint audio)
    {
        if (HoldsBar(QuickPopup))
        {
            return;
        }

        // Şarkı kartıyla aynı yerde duruyor.
        Chip.Dismiss();
        VolumeCard.Present(audio);

        if (!VolumePopup.IsOpen)
        {
            PlaceVolumeChip();
            VolumePopup.IsOpen = true;
        }
    }

    private async void VolumePopup_Opened(object? sender, EventArgs e)
    {
        await VolumeCard.RevealAsync(TowardBar());
    }

    /// <summary>Çubuk ekranda değil: gizli ya da tam ekran pencerenin arkasında.</summary>
    private bool IsBarOutOfSight()
    {
        return IsIslandOffScreen() || _fullScreenOnThisBar;
    }

    private void MoveVolumeChip()
    {
        Point old = _volumePlace;
        PlaceVolumeChip();
        VolumeCard.SlideFrom(old - _volumePlace);
    }

    private void RefreshGlassAfterVolume()
    {
        if (_glassWaitsForVolume)
        {
            _glassWaitsForVolume = false;
            if (!IsIslandOffScreen())
            {
                RefreshGlass();
            }
        }
    }

    /// <summary>
    /// Çubuk görünürken adanın hemen yanında, görünmezken adanın durduğu yerde; ikisinde de ekranın ortasında.
    /// Popup iki yeri de kapsar ve yerinden oynamaz: kart arada kayarken pencerenin kenarında kesilmesin.
    /// </summary>
    private void PlaceVolumeChip()
    {
        _volumeAtBarPlace = IsBarOutOfSight();

        var size = new Size(VolumeChip.CardWidth, _settings.Thickness);
        double centerX = (ActualWidth - size.Width) / 2;
        double centerY = (ActualHeight - size.Height) / 2;
        (Point beside, Point atBar) = AppBarEdge switch
        {
            AppBarEdge.Top => (new Point(centerX, ActualHeight), new Point(centerX, FloatGap)),
            AppBarEdge.Left => (new Point(ActualWidth, centerY), new Point(FloatGap, centerY)),
            AppBarEdge.Right => (new Point(-size.Width, centerY), new Point(ActualWidth - FloatGap - size.Width, centerY)),
            _ => (new Point(centerX, -size.Height), new Point(centerX, ActualHeight - FloatGap - size.Height))
        };

        // Popup ekran dışına taşamaz: kenar tarafında gölge payı olursa kart itilirdi.
        var area = Rect.Union(new Rect(beside, size), new Rect(atBar, size));
        area.Inflate(PanelShadowMargin, PanelShadowMargin);
        area.Intersect(AppBarEdge switch
        {
            AppBarEdge.Top => new Rect(area.X, 0, area.Width, area.Height),
            AppBarEdge.Left => new Rect(0, area.Y, area.Width, area.Height),
            AppBarEdge.Right => new Rect(area.X, area.Y, ActualWidth - area.X, area.Height),
            _ => new Rect(area.X, area.Y, area.Width, ActualHeight - area.Y)
        });

        VolumePopup.Width = area.Width;
        VolumePopup.Height = area.Height;
        VolumePopup.HorizontalOffset = area.X;
        VolumePopup.VerticalOffset = area.Y;

        _volumePlace = _volumeAtBarPlace ? atBar : beside;
        VolumeCard.Margin = new Thickness(_volumePlace.X - area.X, _volumePlace.Y - area.Y, 0, 0);
    }
    #endregion

    private bool IsAnyPanelActive()
    {
        return GroupPanel.IsOpen || TrayPopup.IsOpen || ToolsPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen || PreviewPopup.IsOpen || _geniesPlaying > 0;
    }

    /// <summary>
    /// Fare düğmeye gelince kartın camını ve görüntüsünü tazeler: basış geldiğinde animasyon güncel görüntüyle başlar,
    /// gerçek kart da camı yeniden almak zorunda kalmaz.
    /// </summary>
    private async void PanelButton_MouseEnter(object sender, MouseEventArgs e)
    {
        Popup popup = sender == StartButton ? StartPopup : sender == GroupsButton ? GroupPanel : sender == ToolsButton ? ToolsPopup : sender == TrayOverflowButton ? TrayPopup : QuickPopup;
        // Şarkı kartı açıkken alınan cam onu da içerir; kart açılırken yeniden alınır.
        if (_preparing || ChipPopup.IsOpen || !SystemParameters.ClientAreaAnimation || !_prep.TryGetValue(popup, out PopupPrep? prep)
            || Environment.TickCount64 - prep.TakenAt < PrepareIntervalMs || (popup == StartPopup && !_settings.CustomStartMenu))
        {
            return;
        }

        _preparing = true;
        try
        {
            await Task.Delay(HoverPrepareDelayMs);
            if (!((UIElement)sender).IsMouseOver || ChipPopup.IsOpen || IsAnyPanelActive() || popup.PlacementTarget is not FrameworkElement target)
            {
                return;
            }

            var root = (FrameworkElement)popup.Child;
            Point origin = target.PointToScreen(new Point()) + prep.FromTarget;
            await Task.WhenAll(GlassCardsIn(root).Select(card => card.PrepareAsync(origin - prep.Origin)));

            // Beklerken basılmış olabilir: kartın durumu artık açılışa ait.
            if (IsAnyPanelActive())
            {
                return;
            }

            if (popup == StartPopup)
            {
                StartPanel.ResetView();
            }
            else if (popup == QuickPopup)
            {
                QuickPanel.ResetView();
            }

            FrameworkElement panel = CardAndButton(popup).Card;
            panel.Opacity = 1;
            BitmapSource image = GenieEffect.Snapshot(root, VisualTreeHelper.GetDpi(this));
            panel.Opacity = 0;
            _prep[popup] = prep with { Image = image, Width = root.ActualWidth, Height = root.ActualHeight, Origin = origin, TakenAt = Environment.TickCount64 };
        }
        finally
        {
            _preparing = false;
        }
    }

    /// <summary>Kartın içindeki camlar; bir camın içine inilmez, tüm kutuları dolaşmak boşuna.</summary>
    private static IEnumerable<GlassCard> GlassCardsIn(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is GlassCard card)
            {
                yield return card;
            }
            else
            {
                foreach (GlassCard inner in GlassCardsIn(child))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// Açılıştan sonra boşta her kart bir kez görünmeden açılır: ilk kullanımın yerleşimi, Windows servisleri
    /// ve JIT burada biter, görüntüsü de hazır olur. Yoksa ilk basış 350-500 ms bekliyordu.
    /// </summary>
    private async Task WarmPopupsAsync()
    {
        await Task.Delay(WarmDelayMs);

        var popups = new List<Popup> { GroupPanel, ToolsPopup };
        if (Screen.Primary)
        {
            popups.Add(TrayPopup);
            popups.Add(QuickPopup);
        }
        if (_settings.CustomStartMenu)
        {
            popups.Add(StartPopup);
        }

        foreach (Popup popup in popups)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            if (popup == GroupPanel)
            {
                popup.PlacementTarget = GroupsButton;
            }

            if (IsAnyPanelActive() || !IsLoaded || popup.PlacementTarget is not FrameworkElement { IsVisible: true })
            {
                continue;
            }

            _warmingPopup = popup;
            _warmDone = new TaskCompletionSource();
            PlacePopup(popup, CardAndButton(popup).Card);
            popup.IsOpen = true;
            await Task.WhenAny(_warmDone.Task, Task.Delay(WarmTimeoutMs));
            EndWarm();
        }
    }

    /// <summary>Isınma sürerken kullanıcı bastıysa onun açılışı öncelikli.</summary>
    private void EndWarm()
    {
        if (_warmingPopup is { } popup)
        {
            _warmingPopup = null;
            popup.IsOpen = false;
        }
    }

    private void GenieFinished()
    {
        _geniesPlaying--;
        OnPropertyChanged(nameof(AllowAutoHide));
    }

    private static void HideCard(FrameworkElement card)
    {
        card.BeginAnimation(OpacityProperty, null);
        card.Opacity = 0;
    }

    private void OpenPanel()
    {
        // Panel, pencere etkin olmayınca kapanıyor; bu yüzden etkin olduğundan emin ol.
        Activate();
        GroupPanel.IsOpen = true;
    }

    private void ClosePanel()
    {
        ClosePopup(GroupPanel);
        ClosePopup(TrayPopup);
        ClosePopup(ToolsPopup);
        ClosePopup(QuickPopup);
        ClosePopup(StartPopup);
        HidePreview();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        ClosePanel();
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Açılır kartlardaki tıklama da mantıksal ağaçtan buraya ulaşır: kutuya sağ tık paneli kapatıyordu.
        if (e.OriginalSource is Visual source && PresentationSource.FromVisual(source) != PresentationSource.FromVisual(this))
        {
            return;
        }

        ClosePanel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _draggedGroup != null)
        {
            EndTaskDrag(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (GroupPanel.IsOpen || TrayPopup.IsOpen || ToolsPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen))
        {
            ClosePanel();
            e.Handled = true;
        }
    }

    private void ItemButton_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);
        bool newInstance = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        ClosePanel();

        // Dock'lardaki gibi: açıksa ona geç, Ctrl ile yeni kopya.
        if (!newInstance && item.NextWindow() is { } window)
        {
            window.BringToFront();
            return;
        }

        ReportFailures(Launcher.Launch(item) ? [] : [item]);
    }

    private void OpenAllButton_Click(object sender, RoutedEventArgs e)
    {
        List<GroupItem> items = GroupOf(sender).Items.ToList();

        ClosePanel();

        // Açık olanı ikinci kez açma; önce onları getir ki yeni açılanlar üstte kalsın.
        foreach (GroupItem item in items.Where(item => item.IsRunning))
        {
            item.NextWindow()?.BringToFront();
        }
        ReportFailures(items.Where(item => !item.IsRunning && !Launcher.Launch(item)).ToList());
    }

    private void NewInstanceItem_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);

        ClosePanel();
        ReportFailures(Launcher.Launch(item) ? [] : [item]);
    }

    private void OpenLocationItem_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);

        ClosePanel();
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.LocationPath}\"") { UseShellExecute = true });
    }

    private static GroupItem ItemOf(object sender)
    {
        return (GroupItem)((FrameworkElement)sender).DataContext;
    }

    private void ItemButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tileDragStart = e.GetPosition(null);
    }

    private void ItemButton_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tileDragStart == null)
        {
            return;
        }

        Vector moved = e.GetPosition(null) - _tileDragStart.Value;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _tileDragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(GroupItem), ItemOf(sender)), DragDropEffects.Move);
        _settings.Save();
    }

    private void ItemButton_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(GroupItem)) is not GroupItem dragged)
        {
            return;
        }

        // Sürüklerken yer değiştirir; bırakınca yerleşmiş olur. Başka grubun kutusuna gelince o gruba geçer.
        GroupItem over = ItemOf(sender);
        AppGroup? source = OwnerOf(dragged);
        AppGroup? target = OwnerOf(over);
        if (source != null && target != null && dragged != over)
        {
            int to = target.Items.IndexOf(over);
            if (source == target)
            {
                source.Items.Move(source.Items.IndexOf(dragged), to);
            }
            else if (!HasPath(target, dragged))
            {
                source.Items.Remove(dragged);
                target.Items.Insert(to, dragged);
            }
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private AppGroup? OwnerOf(GroupItem item)
    {
        return _settings.Groups.FirstOrDefault(group => group.Items.Contains(item));
    }

    private static bool HasPath(AppGroup group, GroupItem item)
    {
        return group.Items.Any(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase));
    }

    private void ItemButton_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(GroupItem)))
        {
            e.Handled = true;
        }
    }

    private void GroupSection_DragOver(object sender, DragEventArgs e)
    {
        AppGroup group = GroupOf(sender);

        if (e.Data.GetData(typeof(GroupItem)) is GroupItem dragged)
        {
            // Kutuların dışına, başka grubun boş yerine gelince o grubun sonuna geçer.
            if (OwnerOf(dragged) is { } source && source != group && !HasPath(group, dragged))
            {
                source.Items.Remove(dragged);
                group.Items.Add(dragged);
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (SetDropEffect(e))
        {
            ((Border)sender).Background = (Brush)FindResource("DropHighlight");
        }
    }

    private void GroupSection_DragLeave(object sender, DragEventArgs e)
    {
        ((Border)sender).Background = Brushes.Transparent;
    }

    private void GroupSection_Drop(object sender, DragEventArgs e)
    {
        var section = (Border)sender;
        section.Background = Brushes.Transparent;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddItems(GroupOf(section).Items, paths.Select(GroupItem.FromPath));
        }
        e.Handled = true;
    }

    private void ReportFailures(IReadOnlyCollection<GroupItem> failed)
    {
        if (failed.Count > 0)
        {
            MessageBox.Show(this, "Şunlar açılamadı:\n" + string.Join("\n", failed.Select(item => item.Path)),
                "IslandBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);

        OwnerOf(item)?.Items.Remove(item);
        _settings.Save();
    }
    #endregion

    #region Adding items
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;

        _addTargetGroup = GroupOf(button);
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void AddFilesItem_Click(object sender, RoutedEventArgs e)
    {
        AddToPanelGroup(PickFiles());
    }

    private void AddFolderItem_Click(object sender, RoutedEventArgs e)
    {
        AddToPanelGroup(PickFolders());
    }

    private void AddStoreAppItem_Click(object sender, RoutedEventArgs e)
    {
        AddToPanelGroup(PickStoreApps());
    }

    private List<GroupItem> PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            DereferenceLinks = false,
            Filter = "Programlar ve kısayollar|*.exe;*.lnk|Tüm dosyalar|*.*"
        };
        return dialog.ShowDialog(this) == true ? dialog.FileNames.Select(GroupItem.FromPath).ToList() : [];
    }

    private List<GroupItem> PickFolders()
    {
        var dialog = new OpenFolderDialog { Multiselect = true };
        return dialog.ShowDialog(this) == true ? dialog.FolderNames.Select(GroupItem.FromPath).ToList() : [];
    }

    private List<GroupItem> PickStoreApps()
    {
        var dialog = new StoreAppPicker { Owner = this };

        // Seçicideki nesneler kapanınca serbest kalıyor; yenilerini döndür.
        return dialog.ShowDialog() == true
            ? dialog.SelectedApps.Select(app => new GroupItem { Name = app.Name, Path = app.Path }).ToList()
            : [];
    }

    private void AddToPanelGroup(IEnumerable<GroupItem> items)
    {
        if (_addTargetGroup != null)
        {
            AddItems(_addTargetGroup.Items, items);
        }

        // Diyalog açılınca pencere etkinliğini kaybedip paneli kapatmıştı.
        ShowGroupsPanel();
    }

    private void AddItems(ICollection<GroupItem> target, IEnumerable<GroupItem> items)
    {
        foreach (GroupItem item in items)
        {
            if (target.Any(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            target.Add(item);
        }

        _settings.Save();
        _runTracker?.Refresh();
    }

    private void GroupsButton_DragEnter(object sender, DragEventArgs e)
    {
        // Sürüklenen dosya hangi gruba gideceğini görebilsin. Sürükleme bitene dek pencere etkinleşemez;
        // bırakılınca DragOpenTimer_Tick toparlar.
        if (SetDropEffect(e) && !GroupPanel.IsOpen)
        {
            ClosePanel();
            _runTracker?.Refresh();
            GroupPanel.PlacementTarget = GroupsButton;
            PlacePopup(GroupPanel, PanelCard);
            GroupPanel.IsOpen = true;
            _dragOpenTimer.Start();
        }
    }

    private void GroupsButton_DragOver(object sender, DragEventArgs e)
    {
        SetDropEffect(e);
    }

    private void GroupsButton_Drop(object sender, DragEventArgs e)
    {
        // Tek grup varsa nereye gideceği belli; yoksa panel açık, kullanıcı bölüme bırakır.
        if (_settings.Groups.Count == 1 && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddItems(_settings.Groups[0].Items, paths.Select(GroupItem.FromPath));
        }
        e.Handled = true;
    }

    private void DragOpenTimer_Tick(object? sender, EventArgs e)
    {
        const int leftButton = 0x01;
        if ((GetAsyncKeyState(leftButton) & 0x8000) != 0)
        {
            return;
        }

        _dragOpenTimer.Stop();

        // Bırakıldı. İmleç paneldeyse normal açık panele dön, değilse kapat: etkin olmayan panel kendiliğinden kapanmaz.
        if (GroupPanel.IsOpen && IsCursorOver(PanelCard))
        {
            Activate();
        }
        if (!IsActive || !IsCursorOver(PanelCard))
        {
            ClosePanel();
        }
    }

    private static bool IsCursorOver(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) == null)
        {
            return false;
        }

        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        Point origin = element.PointToScreen(new Point(0, 0));
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        return cursor.X >= origin.X && cursor.X <= origin.X + element.ActualWidth * dpi.DpiScaleX
            && cursor.Y >= origin.Y && cursor.Y <= origin.Y + element.ActualHeight * dpi.DpiScaleY;
    }

    [System.Runtime.InteropServices.DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    private static bool SetDropEffect(DragEventArgs e)
    {
        // Araçlar kartından sürüklenen ekran görüntüsü başka yere gidiyor; çubuğa düşerse pin olmasın.
        bool canDrop = e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(ToolsPanel.InternalDragFormat);

        // Dosyanın kendisine dokunmuyoruz, yolunu tutuyoruz; bağlantıya izin vermeyen kaynak için kopya da olur.
        DragDropEffects allowed = e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;
        e.Effects = canDrop ? allowed : DragDropEffects.None;
        e.Handled = true;
        return canDrop;
    }
    #endregion
}
