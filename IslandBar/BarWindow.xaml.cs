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
    private const string PinnedKeyPrefix = "pin:";
    private const int MaxGapSlots = 6;
    private const int GapCloseDelayMs = 120;
    private const double StartPanelHeight = 620;
    private const double StartPanelScreenMargin = 40;
    private const int PreviewShowDelayMs = 350;
    private const int PreviewHideDelayMs = 250;
    private const int PreviewFadeMs = 130;

    /// <summary>Kısayolla açılan uygulamanın penceresi bu süre içinde gelirse öne alınır.</summary>
    private const int LaunchFocusMs = 15000;

    /// <summary>Çalışma düzeninde açılan uygulamanın penceresi en çok bu kadar beklenir.</summary>
    private const int LayoutWaitMs = 20000;
    private const int LayoutPollMs = 150;
    private const int LayoutSettleMs = 1000;

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

    /// <summary>İmleç kenarda bu kadar kaldığı hâlde ada görünmüyorsa durum kayda yazılır.</summary>
    private const int StuckRevealMs = 600;

    /// <summary>70 ms'lik dürtü bundan geç gelirse arayüz donmuştur, kenar o sırada izlenmez.</summary>
    private const int SlowTickMs = 400;

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
    private bool _awaitingBatteryIcon;

    /// <summary>Açılıştan (ya da Explorer yeniden başlayınca) sonra pil simgesine bakılan aralar, saniye.</summary>
    private static readonly int[] BatteryIconChecks = [8, 15, 30, 60];
    private bool _startMenuWasOpen;
    private readonly ObservableCollection<TaskGroup> _taskGroups = new();
    private int _wheelRemainder;
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
    private ImageSource? _claudeIcon;

    /// <summary>Bildirim kartına tıklanınca: Claude'da Claude öne gelir, süre dolduğunda yalnızca kapanır.</summary>
    private Action? _noticeClicked;

    /// <summary>Hatırlatıcı kartında Tamam (true) ya da Ertele (false).</summary>
    private Action<bool>? _noticeChosen;
    private bool _volumeAtBarPlace;
    private bool _glassWaitsForVolume;
    private Point _volumePlace;
    private Point _chipPlace;

    private readonly IDisposable? _volumeWatcher;
    private readonly GroupRunTracker? _runTracker;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _clockMinute;
    private const int AutoHideIntervalMs = 70;
    private Pulse? _autoHidePulse;
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
    private long _edgeSince;
    private bool _stuckLogged;
    private long _lastAutoHideTick;
    private bool _cursorInBarZone;
    private bool _desktopClear;
    private bool _clockChipTarget;
    private int _clockChipDelay;
    private readonly RectangleGeometry _clockChipClip = new();
    private FrameworkElement[] _clockChipFades = [];
    private bool _startMenuVisible;
    private bool _attentionWanted;
    private uint _trayMenuProcess;
    private long _trayMenuSince;
    private const int TrayMenuGraceMs = 1500;

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
            PrivacyWatch.Shared.Changed += PrivacyWatch_Changed;
            PrivacyWatch_Changed(PrivacyWatch.Shared.Camera, PrivacyWatch.Shared.Microphone);
            TranslateMode.Shared.Changed += TranslateMode_Changed;
            TranslateMode_Changed(TranslateMode.Shared.IsOn);

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
        Chip.CloseChosen += () => TranslateMode.Shared.SetOn(false);
        _residentTimer.Tick += (_, _) => ShowResidentChip();
        Chip.ActionChosen += done =>
        {
            Action<bool>? chosen = _noticeChosen;
            _noticeChosen = null;
            chosen?.Invoke(done);
        };
        Chip.Clicked += () =>
        {
            if (Chip.IsClaude)
            {
                _noticeClicked?.Invoke();
            }
            else
            {
                QuickPanel.OpenMediaApp(Chip.AppId);
            }
        };
        CalendarContent.Opened += ClosePanel;

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
        FocusTimer.Shared.Changed += FocusTimer_Changed;
        FocusTimer_Changed();

        if (AppBarMode == AppBarMode.AutoHide)
        {
            // Adalar tamamen ekran dışına kayar; fareyi konumundan takip ediyoruz, kenarda iz kalmaz.
            AutoHideShowMargin = 0;
            _autoHidePulse = new Pulse(Dispatcher, AutoHideIntervalMs, AutoHideTick);

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
        _ = AwaitBatteryIconAsync();
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
        Popup? card = GroupPanel.IsOpen ? GroupPanel : TrayPopup.IsOpen ? TrayPopup : QuickPopup.IsOpen ? QuickPopup : StartPopup.IsOpen ? StartPopup
            : CalendarPopup.IsOpen ? CalendarPopup : Chip.IsShown && !Chip.IsHiding ? ChipPopup : null;
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
                : CardOf(card).RefreshGlassAsync(animateTint: true);
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

    /// <summary>Ada otomatik gizlemeyle ekran dışında.</summary>
    internal bool IsIslandHidden => IsIslandOffScreen();

    /// <summary>Ada masaüstünde saat damlalarına daralmış (ya da daralıyor).</summary>
    internal bool IsClockChipShown => ClockChipProgress > 0;

    internal bool TryGetIslandRestRect(out System.Drawing.Rectangle area)
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
        AutoHideLog.Write($"{Screen.DeviceName} tam ekran başladı: '{app.title}' {app.rect.Left},{app.rect.Top}-{app.rect.Right},{app.rect.Bottom} " +
            $"kaynak={(app.fromTasksService ? "Windows" : "boyut")} çubuk geri çekildi={_fullScreenOnThisBar}");
        if (_fullScreenOnThisBar)
        {
            Chip.Dismiss();
        }
    }

    protected override void OnFullScreenLeave()
    {
        base.OnFullScreenLeave();

        _fullScreenOnThisBar = false;
        AutoHideLog.Write($"{Screen.DeviceName} tam ekran bitti");
    }
    #endregion

    #region Auto-hide
    protected override bool ShouldAllowAutoHide()
    {
        // Altında pencere yoksa gizlenmez, saate dönüşür.
        return CanRetract() && !_desktopClear;
    }

    /// <summary>
    /// Çubuğa şu an ihtiyaç yok: pencere varsa gizlenir, yoksa saate dönüşür. Windows'un çubuğu gibi bu ekranda dikkat
    /// isteyen pencere varken (uygulamaya geçilene kadar) ve tepsi ikonunun açtığı menü açıkken de görünür kalır.
    /// </summary>
    private bool CanRetract()
    {
        // Başlat ana ekranda açılıyor; Windows'taki gibi açıkken çubuk da görünsün.
        return base.ShouldAllowAutoHide() && !_cursorInBarZone && !HoldsBar(GroupPanel) && !HoldsBar(TrayPopup)
            && !HoldsBar(CalendarPopup) && !HoldsBar(PreviewPopup) && !HoldsBar(QuickPopup) && !HoldsBar(StartPopup) && _geniesPlaying == 0 && !(_startMenuVisible && Screen.Primary)
            && _draggedGroup == null && !_attentionWanted && _trayMenuProcess == 0;
    }

    private bool IsAttentionWanted()
    {
        // Sabitlenmiş uygulamanın pencereleri her ekrandan toplanıyor: yalnızca bu ekrandakiler sayılır.
        return _taskGroups.Any(group => group.Windows.Any(window => window.HMonitor == Screen.HMonitor && group.WantsAttention(window)));
    }

    /// <summary>Tepsi ikonuna tıklandı: uygulamanın açacağı menü ya da açılır pencere kapanana kadar çubuk gizlenmesin.</summary>
    private void WatchTrayMenu(NotifyIcon icon)
    {
        if (AppBarMode != AppBarMode.AutoHide)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(icon.HWnd, out uint process);
        _trayMenuProcess = process;
        _trayMenuSince = Environment.TickCount64;
        OnPropertyChanged(nameof(AllowAutoHide));
    }

    /// <summary>
    /// Menü biraz geç açılabilir: tıklamadan sonra kısa bir süre her durumda beklenir. Sonra uygulamanın menüsü
    /// (<c>#32768</c>) ya da öndeki, görev çubuğunda olmayan penceresi (OneDrive gibi açılır pencereler, Electron
    /// menüleri) durdukça tutulur. Menüsü kapanınca öne gizli pencere kalabildiği için yalnızca görünen pencere sayılır.
    /// </summary>
    private bool UpdateTrayMenu()
    {
        if (_trayMenuProcess == 0 || Environment.TickCount64 - _trayMenuSince < TrayMenuGraceMs)
        {
            return false;
        }

        if (DesktopWindows.HasMenu(_trayMenuProcess))
        {
            return false;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint process);
        if (process == _trayMenuProcess && DesktopWindows.IsShown(foreground) && !DesktopWindows.IsDesktop(foreground)
            && !_allWindows.OfType<ApplicationWindow>().Any(window => window.Handle == foreground))
        {
            return false;
        }

        _trayMenuProcess = 0;
        return true;
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
        GlassCard card = CardOf(popup);

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
            RevealPopup(popup, card, CardAndButton(popup).Button);
        }
    }

    /// <summary>Kendi cam kartı olan açılır kartlar; hızlı ayarlar ve Başlat kartın kendisi.</summary>
    private GlassCard CardOf(Popup popup)
    {
        return popup == GroupPanel ? PanelCard : popup == TrayPopup ? TrayCard : popup == CalendarPopup ? CalendarCard : PreviewCard;
    }

    private void AutoHideTick()
    {
        bool changed = UpdateCursorZone();
        WatchStuckReveal();

        if (ChipPopup.IsOpen && !Chip.IsHiding && IsIslandOffScreen() != _chipAtBarPlace)
        {
            MoveChip();
        }

        if (VolumePopup.IsOpen && !VolumeCard.IsHiding && IsBarOutOfSight() != _volumeAtBarPlace)
        {
            MoveVolumeChip();
        }

        if (Screen.Primary)
        {
            ConvertCard.Current?.FollowBar(IsIslandOffScreen());
        }
        LyricsMode.Shared.FollowBar(Screen.DeviceName, Screen.Primary, IsIslandOffScreen());

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

            bool attention = IsAttentionWanted();
            if (attention != _attentionWanted)
            {
                AutoHideLog.Write($"{Screen.DeviceName} dikkat isteyen pencere {(attention ? "var" : "kalmadı")}");
                changed = true;
            }
            _attentionWanted = attention;

            changed |= UpdateTrayMenu();
        }

        // Önce çip: gizli çubuk yukarı kaymaya başlamadan doğru şekle geçsin.
        UpdateClockChip();

        if (changed)
        {
            OnPropertyChanged(nameof(AllowAutoHide));
        }
    }

    /// <summary>
    /// Tanı: kullanıcı imleci kenara götürünce çubuğun bazen açılmadığını bildirdi (Windows'un çubuğunda da). İlk kayıt
    /// tam ekranı da takılmayı da göstermedi; bu yüzden imleç ham konumdan kenarda sayılır ve adanın yerinde gerçekten ne
    /// göründüğüne bakılır. 600 ms sonra ada görünmüyorsa o anki durum bir kez yazılır. Arayüz donup zamanlayıcı
    /// geciktiyse o da yazılır.
    /// </summary>
    private void WatchStuckReveal()
    {
        long now = Environment.TickCount64;
        long gap = _lastAutoHideTick == 0 ? 0 : now - _lastAutoHideTick;
        _lastAutoHideTick = now;
        if (gap >= SlowTickMs)
        {
            AutoHideLog.Write($"{Screen.DeviceName} dürtü {gap} ms gecikti");
        }

        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        System.Drawing.Rectangle bounds = Screen.Bounds;
        int edge = (int)EdgeRevealDepth;
        bool atEdge = bounds.Contains(cursor) && AppBarEdge switch
        {
            AppBarEdge.Top => cursor.Y < bounds.Top + edge,
            AppBarEdge.Left => cursor.X < bounds.Left + edge,
            AppBarEdge.Right => cursor.X >= bounds.Right - edge,
            _ => cursor.Y >= bounds.Bottom - edge
        };
        if (!atEdge)
        {
            _edgeSince = 0;
            _stuckLogged = false;
            return;
        }

        if (_edgeSince == 0)
        {
            _edgeSince = now;
            return;
        }
        if (_stuckLogged || now - _edgeSince < StuckRevealMs)
        {
            return;
        }

        var slide = (TranslateTransform)BarRoot.RenderTransform;
        string atIsland = "?";
        bool ours = false;
        if (TryGetIslandRestRect(out System.Drawing.Rectangle island))
        {
            IntPtr hit = GetAncestor(WindowFromPoint(new NativePoint { X = island.X + island.Width / 2, Y = island.Y + island.Height / 2 }), RootAncestor);
            NativeMethods.GetWindowThreadProcessId(hit, out uint hitProcess);
            ours = hitProcess == Environment.ProcessId;
            atIsland = $"'{WindowTitle(hit)}' ({ProcessName(hitProcess)})";
        }
        if (ours && Math.Abs(slide.X) < 1 && Math.Abs(slide.Y) < 1)
        {
            return;
        }

        _stuckLogged = true;
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        AutoHideLog.Write($"{Screen.DeviceName} açılmadı: imleç {cursor} ekran {bounds} bölgede={_cursorInBarZone} kayma={slide.X:0},{slide.Y:0} " +
            $"en üstte={Topmost} tam ekran={_fullScreenOnThisBar} gizlenebilir={AllowAutoHide} kart açık={IsAnyPanelActive()} saat çipi={ClockChipProgress:0.##} " +
            $"fare basılı={Mouse.LeftButton == MouseButtonState.Pressed} adanın yerinde={atIsland} öndeki='{WindowTitle(foreground)}' ({ProcessName(foregroundProcess)})");
    }

    private static string ProcessName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "?";
        }
    }

    private static string WindowTitle(IntPtr window)
    {
        var text = new System.Text.StringBuilder(256);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private const uint RootAncestor = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);

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
        if (Screen.Primary)
        {
            ConvertCard.Current?.SetBarIsChip(progress > 0);
        }

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
        _ = AwaitBatteryIconAsync();
    }

    /// <summary>
    /// Pil simgesi Explorer'ın içinden (stobject) eklenir. Oturum açılışında Explorer'la yarışınca simge Explorer'ın
    /// tepsisine kaydolur, bize hiç gelmez; TaskbarCreated yayını onu yeniden ekletir. Bilgisayarda pil varken simge
    /// birkaç kontrolde hâlâ yoksa yayın yinelenir (uygulamalar ikonlarını yeniden ekler, ManagedShell çiftleri birleştirir).
    /// </summary>
    private async Task AwaitBatteryIconAsync()
    {
        if (!Screen.Primary || _awaitingBatteryIcon)
        {
            return;
        }

        _awaitingBatteryIcon = true;
        try
        {
            foreach (int seconds in BatteryIconChecks)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (AllowClose || !IsLoaded || HasBatteryIcon() || !HasSystemBattery())
                {
                    return;
                }

                ManagedShell.Common.Logging.ShellLogger.Info("BarWindow: pil simgesi yok, TaskbarCreated yeniden yayınlanıyor");
                NativeMethods.SendNotifyMessage(NativeMethods.HWND_BROADCAST, (uint)TaskbarCreatedMessage, UIntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            _awaitingBatteryIcon = false;
        }
    }

    private bool HasBatteryIcon()
    {
        return _notificationArea.TrayIcons.OfType<NotifyIcon>().Any(IsBatteryIcon);
    }

    private static bool HasSystemBattery()
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        return (power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0
            && power.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
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
        TakeForeground();
        Activate();
    }

    /// <summary>
    /// Ön plan izni: kancadan ya da tekerlekten gelen istekte Windows başka pencereyi öne almaya izin vermez, açılan
    /// uygulama da arkada kalır. Etkin pencerenin girdisine bir an bağlanıp çubuğu öne alınca ikisi de olur.
    /// </summary>
    private void TakeForeground()
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

        // Uygulama kendi menüsünü açarken kart yolda kalmasın; çubuk menü kapanana kadar kalır.
        WatchTrayMenu(icon);
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

    /// <summary>Kamera ya da mikrofon kullanımdayken hızlı ayarlar düğmesinde renkli simge, ipucunda kullananlar.</summary>
    private void PrivacyWatch_Changed(IReadOnlyList<string> camera, IReadOnlyList<string> microphone)
    {
        CameraSlot.Visibility = camera.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraSlot.ToolTip = camera.Count > 0 ? $"Kamerayı kullanıyor: {string.Join(", ", camera)}" : null;
        MicrophoneSlot.Visibility = microphone.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneSlot.ToolTip = microphone.Count > 0 ? $"Mikrofonu kullanıyor: {string.Join(", ", microphone)}" : null;
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

    /// <summary>Zamanlayıcı çalışırken saat kalan süreyi gösterir; durunca hemen saate döner.</summary>
    private void FocusTimer_Changed()
    {
        FocusTimer timer = FocusTimer.Shared;
        if (!timer.IsRunning)
        {
            ClockProgress.Visibility = Visibility.Collapsed;
            _clockMinute = 0;
            UpdateClock();
            return;
        }

        (string left, string right) = timer.Display();
        if (ClockHours.Text != left)
        {
            ClockHours.Text = left;
        }
        if (ClockMinutes.Text != right)
        {
            ClockMinutes.Text = right;
        }
        ClockSeparator.Text = ":";
        ClockDrops.SetTime(left, right);
        ClockProgress.Visibility = Orientation == Orientation.Horizontal ? Visibility.Visible : Visibility.Collapsed;
        ClockProgressScale.ScaleX = 1 - timer.Progress;
        Clock.ToolTip = $"Zamanlayıcı: {timer.EndsAt:t}'de biter";
    }

    private void UpdateClock()
    {
        if (FocusTimer.Shared.IsRunning)
        {
            return;
        }

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

    /// <summary>Saate tıklamak takvimi açar; öteki kart düğmeleri gibi basınca.</summary>
    private void Clock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Pencereye ulaşırsa açılan kartı hemen kapatır.
        e.Handled = true;

        EndWarm();
        bool wasOpen = CalendarPopup.IsOpen;
        if (!wasOpen)
        {
            StartOpening(CalendarPopup);
        }

        ClosePanel();

        if (!wasOpen)
        {
            CalendarContent.ShowToday();
            PlacePopup(CalendarPopup, CalendarCard);
            Activate();
            CalendarPopup.IsOpen = true;
        }
    }

    private void Clock_MouseEnter(object sender, MouseEventArgs e)
    {
        PanelButton_MouseEnter(sender, e);

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
        // Menü ve ayarlar penceresi yalnızca ayarı değiştirir; her çubuk kendine uygular, kaydetmek App'te.
        if (e.PropertyName is nameof(AppSettings.Edge) or nameof(AppSettings.Thickness))
        {
            // Kartların konumu değişti; eski görüntüyle başlamasın.
            _prep.Clear();
            Chip.Dismiss();
        }
        else if ((e.PropertyName == nameof(AppSettings.ShowNowPlayingChip) && !_settings.ShowNowPlayingChip && !Chip.IsClaude)
            || (e.PropertyName == nameof(AppSettings.ShowClaudeChip) && !_settings.ShowClaudeChip && Chip.IsClaude))
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
        LayoutEditor.StopFor(this);
        _settings.PropertyChanged -= Settings_PropertyChanged;
        _stage.Dispose();
        _nowPlaying?.Dispose();
        _clockTimer.Stop();
        FocusTimer.Shared.Changed -= FocusTimer_Changed;
        _autoHidePulse?.Dispose();
        _glassTimer.Stop();

        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _gapCloseTimer.Stop();

        _volumeWatcher?.Dispose();
        _runTracker?.Dispose();
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
        PrivacyWatch.Shared.Changed -= PrivacyWatch_Changed;
        TranslateMode.Shared.Changed -= TranslateMode_Changed;
        _residentTimer.Stop();

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

    private void DeleteGroupItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteGroup(GroupOf(sender), this);
        ShowGroupsPanel();
    }

    internal void DeleteGroup(AppGroup group, Window owner)
    {
        if (group.Items.Count > 0 && MessageBox.Show(owner,
                $"\"{group.Name}\" düzeni silinsin mi? Uygulamalara ve dosyalara dokunulmaz.",
                "Düzeni sil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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

        SwitchTo(group, active);
    }

    /// <summary>
    /// Win+1…9, 0: çubuktaki o sıradaki düğmeye basmak gibi (Windows'taki gibi soldan sayılır, 0 onuncu).
    /// Shift ile yeni kopya açar; sabitlenmemiş uygulamada çalışan pencerenin exe'si ya da kimliği başlatılır.
    /// </summary>
    internal void PressTaskAt(int index, bool newInstance)
    {
        if (index >= _taskGroups.Count || _draggedGroup != null)
        {
            return;
        }

        TaskGroup group = _taskGroups[index];
        ClosePanel();
        HidePreview();

        GroupItem? launch = group.Pin is { } pin && (!group.HasWindows || newInstance) ? pin
            : newInstance ? PinItemFor(group.Windows[0])
            : null;
        ApplicationWindow? active = group.ActiveWindow;
        if (launch != null)
        {
            if (Launcher.Launch(launch))
            {
                BringForwardWhenOpened(group);
            }
            else
            {
                ReportFailures([launch]);
            }
        }
        else if (!newInstance)
        {
            // Küçültmek izin istemez; Windows sıradaki pencereyi kendisi etkinleştirir.
            if (group.HasMany || active is not { CanMinimize: true })
            {
                TakeForeground();
            }
            SwitchTo(group, active);
        }
    }

    /// <summary>
    /// Kısayolla açılan uygulama arkada açılıyor: izin Windows'ta açılış anında verilir, kancadan gelen istekte
    /// verilmez, çubuğu öne almak da yetmedi. Yeni penceresi gelince öne alınır.
    /// </summary>
    private void BringForwardWhenOpened(TaskGroup group)
    {
        List<ApplicationWindow> known = group.Windows.ToList();
        long until = Environment.TickCount64 + LaunchFocusMs;

        void Windows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            ApplicationWindow? opened = group.Windows.Except(known).FirstOrDefault();
            if (opened == null && Environment.TickCount64 < until)
            {
                return;
            }

            group.Windows.CollectionChanged -= Windows_CollectionChanged;
            if (opened != null && Environment.TickCount64 < until && opened.State != ApplicationWindow.WindowState.Active)
            {
                TakeForeground();
                opened.BringToFront();
            }
        }

        group.Windows.CollectionChanged += Windows_CollectionChanged;
    }

    /// <summary>Tek pencerede öne getirir ya da küçültür; çok pencerede sıradakine geçer.</summary>
    private static void SwitchTo(TaskGroup group, ApplicationWindow? active)
    {
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

    /// <summary>Orta tık pencereyi kapatır; birden çok pencerede etkin olanı ya da en son etkin olanı.</summary>
    private void TaskButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        TaskGroup group = GroupOfTask(sender);
        if (e.ChangedButton != MouseButton.Middle || !group.HasWindows)
        {
            return;
        }

        e.Handled = true;
        HidePreview();
        (group.ActiveWindow ?? group.NextWindow(null)).Close();
    }

    /// <summary>Tekerlek uygulamanın pencereleri arasında gezer: aşağı sıradaki, yukarı önceki.</summary>
    private void TaskButton_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        TaskGroup group = GroupOfTask(sender);
        e.Handled = true;
        int notches = WheelNotches(e);
        if (!group.HasWindows || notches == 0)
        {
            return;
        }

        ApplicationWindow? active = group.ActiveWindow;
        ApplicationWindow next = active == null
            ? group.NextWindow(null)
            : group.Windows[((group.Windows.IndexOf(active) - notches) % group.Windows.Count + group.Windows.Count) % group.Windows.Count];
        if (next != active)
        {
            TakeForeground();
            next.BringToFront();
        }
    }

    /// <summary>
    /// Tam çentik sayısı, yukarı artı. Dokunmatik yüzey çentiği parça parça gönderir: artan kısım biriktirilir,
    /// yön değişince sıfırlanır.
    /// </summary>
    private int WheelNotches(MouseWheelEventArgs e)
    {
        if (Math.Sign(e.Delta) != Math.Sign(_wheelRemainder))
        {
            _wheelRemainder = 0;
        }

        _wheelRemainder += e.Delta;
        int notches = _wheelRemainder / Mouse.MouseWheelDeltaForOneLine;
        _wheelRemainder -= notches * Mouse.MouseWheelDeltaForOneLine;
        return notches;
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
        if (_draggedGroup != null || ClockChipProgress > 0 || !GroupOfTask(button).HasWindows || GroupPanel.IsOpen || TrayPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen || CalendarPopup.IsOpen || button.ContextMenu?.IsOpen == true)
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
        else if (sender == CalendarCard)
        {
            PlacePopup(CalendarPopup, CalendarCard);
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
            : popup == CalendarPopup ? (CalendarCard, Clock)
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
        if (!_settings.ShowNowPlayingChip || IsReminderShown || _fullScreenOnThisBar || IsAnyPanelActive() || IsChipSpotTaken || VolumePopup.IsOpen || NowPlayingChip.IsUserBusy())
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
        // Claude kartı ana ekran dışında da çıkabiliyor; orada şarkı izleyicisi yok.
        if (_nowPlaying != null)
        {
            _nowPlaying.WantsUpdates = false;
        }
    }

    /// <summary>Claude'un penceresi bu ekranda: kart onun yanında çıksın.</summary>
    internal bool ShowsClaudeWindow => _taskWindows.OfType<ApplicationWindow>().Any(IsClaudeWindow);

    /// <summary>
    /// Claude Code bitirdi ya da onay bekliyor: şarkı kartıyla aynı kart, aynı yerde (kullanıcının seçimi). Claude zaten
    /// öndeyse kullanıcı görüyor, çıkmaz. Terminaldeki Claude Code'un kendi penceresi yok; o zaman ikon yerine işaret.
    /// </summary>
    internal async void ShowClaude(ClaudeNote note)
    {
        ApplicationWindow? claude = ClaudeWindow();
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        string? skip = !_settings.ShowClaudeChip ? "ayar kapalı"
            : IsClaudeProcess(foregroundProcess) ? "Claude önde"
            : IsClaudeInSight() ? "Claude ekranında en üstte"
            : IsReminderShown ? "hatırlatıcı açık"
            : _fullScreenOnThisBar ? "tam ekran"
            : IsAnyPanelActive() || IsChipSpotTaken || VolumePopup.IsOpen ? "kart açık"
            : NowPlayingChip.IsUserBusy() ? "rahatsız etme"
            : null;
        AutoHideLog.Write($"{Screen.DeviceName} Claude olayı '{note.Project}: {note.Status}' {(skip == null ? "gösteriliyor" : "gösterilmedi: " + skip)}, " +
            $"öndeki '{WindowTitle(foreground)}' ({ProcessName(foregroundProcess)})");
        if (skip != null)
        {
            return;
        }

        // Pencerenin ikonu ManagedShell'de geç yükleniyor, ilk kartta boş geliyordu; exe'den bir kez büyük boy alınır.
        if (_claudeIcon == null && claude != null && ProcessPath(claude.Handle) is { } path)
        {
            _claudeIcon = await AppIcon.LoadAsync(path);
        }

        Chip.SetClaude(_claudeIcon ?? claude?.Icon, note.Project, note.Status, note.IsWaiting);
        _noticeClicked = FocusClaude;
        if (ChipPopup.IsOpen)
        {
            PlaceChip();
            Chip.Renew();
            return;
        }

        ChipPopup.IsOpen = true;
    }

    /// <summary>
    /// Süre doldu, hatırlatıcı: kullanıcı neye bakıyorsa baksın görmeli, bu yüzden Claude kartının koşulları yok.
    /// Açık kart varsa kapanır ki üst üste binmesin.
    /// </summary>
    internal void ShowNotice(string glyph, string title, string status, int seconds)
    {
        if (IsReminderShown)
        {
            return;
        }

        ClosePanel();
        Chip.SetNotice(null, glyph, title, status, waiting: true, seconds);
        _noticeClicked = null;
        if (ChipPopup.IsOpen)
        {
            PlaceChip();
            Chip.Renew();
            return;
        }

        ChipPopup.IsOpen = true;
    }

    private readonly DispatcherTimer _residentTimer = new() { Interval = TimeSpan.FromMilliseconds(ResidentCheckMs) };
    private const int ResidentCheckMs = 600;

    /// <summary>
    /// Çeviri kipi açıkken kartı kalıcı (kullanıcının isteği): şarkı, Claude ya da bir kart açılınca yerini verir, o
    /// gidince geri gelir. Yalnızca ana ekranda.
    /// </summary>
    private void TranslateMode_Changed(bool on)
    {
        if (on)
        {
            _residentTimer.Start();
            ShowResidentChip();
        }
        else
        {
            _residentTimer.Stop();
            if (ChipPopup.IsOpen && Chip.IsResident)
            {
                Chip.Dismiss();
            }
        }
    }

    private void ShowResidentChip()
    {
        if (!TranslateMode.Shared.IsOn || !IsLoaded || ChipPopup.IsOpen || _fullScreenOnThisBar || IsAnyPanelActive() || IsChipSpotTaken
            || VolumePopup.IsOpen || PreviewPopup.IsOpen)
        {
            return;
        }

        Chip.SetResident("", "Çeviri modu", "Seçtiğin yazı çevrilir");
        _noticeClicked = null;
        ChipPopup.IsOpen = true;
    }

    /// <summary>Hatırlatıcı kartı ekranda; başka kart onu ezmez.</summary>
    internal bool IsReminderShown => ChipPopup.IsOpen && Chip.IsSticky;

    /// <summary>Hatırlatıcı: kart kullanıcı Tamam ya da Ertele diyene kadar kalır (kullanıcının seçimi).</summary>
    internal void ShowReminder(Reminder reminder, Action<bool> chosen)
    {
        ClosePanel();
        string when = reminder.DueAt.Date == DateTime.Today ? reminder.TimeLabel : reminder.DueAt.ToString("d MMM t");
        Chip.SetNotice(null, "", reminder.Text, $"Hatırlatıcı · {when}", waiting: true, seconds: 0);
        _noticeClicked = null;
        _noticeChosen = chosen;
        if (ChipPopup.IsOpen)
        {
            PlaceChip();
            Chip.Renew();
            return;
        }

        ChipPopup.IsOpen = true;
    }

    private void FocusClaude()
    {
        if (ClaudeWindow() is { } claude)
        {
            TakeForeground();
            claude.BringToFront();
        }
    }

    private static string? ProcessPath(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Öndeki pencere Claude'un mu? Claude'un birden çok üst düzey penceresi var; öndekini tek bir pencereyle
    /// karşılaştırmak tutmadı (Claude önündeyken kart çıkıyordu), bu yüzden sürece bakılır.
    /// </summary>
    private bool IsClaudeProcess(uint processId)
    {
        return processId != 0 && _allWindows.OfType<ApplicationWindow>().Where(IsClaudeWindow).Any(window =>
        {
            NativeMethods.GetWindowThreadProcessId(window.Handle, out uint claudeProcess);
            return claudeProcess == processId;
        });
    }

    /// <summary>
    /// Claude kendi ekranında en üstteki uygulama penceresi mi, yani kullanıcı onu görüyor mu? Öndeki pencereye bakmak
    /// yetmedi: çubuk yeniden başlayınca odak IslandBar'da kalıyordu, Claude ekrandayken kart yine çıkıyordu.
    /// </summary>
    private bool IsClaudeInSight()
    {
        var windows = new Dictionary<IntPtr, ApplicationWindow>();
        foreach (ApplicationWindow window in _allWindows.OfType<ApplicationWindow>())
        {
            windows.TryAdd(window.Handle, window);
        }
        if (!windows.Values.Any(IsClaudeWindow))
        {
            return false;
        }

        var screensSeen = new HashSet<IntPtr>();
        foreach (IntPtr handle in DesktopWindows.ZOrder())
        {
            if (!windows.TryGetValue(handle, out ApplicationWindow? window) || !DesktopWindows.IsShown(handle) || !screensSeen.Add(DesktopWindows.MonitorOf(handle)))
            {
                continue;
            }

            // Bu ekrandaki en üst uygulama penceresi.
            if (IsClaudeWindow(window))
            {
                return true;
            }
        }
        return false;
    }

    private ApplicationWindow? ClaudeWindow()
    {
        return _allWindows.OfType<ApplicationWindow>().FirstOrDefault(IsClaudeWindow);
    }

    /// <summary>
    /// Claude masaüstü uygulaması: paket kimliği (Claude_…) ya da exe adı Claude.exe. Paketli uygulamada exe yolu
    /// boş gelebiliyor, yalnızca ada bakınca bulunamadı. Terminaldeki claude.exe'nin penceresi yok.
    /// </summary>
    private static bool IsClaudeWindow(ApplicationWindow window)
    {
        return (window.AppUserModelID?.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) ?? false)
            || string.Equals(System.IO.Path.GetFileName(window.WinFileName), "claude.exe", StringComparison.OrdinalIgnoreCase);
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
    /// <summary>Ses simgelerinin üzerinde tekerlek: ses tuşlarıyla aynı adım, gösterge de aynı.</summary>
    private void QuickButton_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int notches = WheelNotches(e);
        if (notches == 0 || AudioEndpoint.TryOpen() is not { } audio)
        {
            return;
        }

        try
        {
            bool changed = false;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                changed |= audio.Press(notches > 0 ? VolumeKey.Up : VolumeKey.Down);
            }

            if (_settings.ShowVolumeChip)
            {
                ShowVolume(audio, changed);
            }
        }
        catch (COMException)
        {
            // Aygıt tam bu sırada çıkarılmış olabilir.
        }
    }

    internal void ShowVolume(AudioEndpoint audio, bool changed)
    {
        if (HoldsBar(QuickPopup))
        {
            return;
        }

        // Şarkı kartıyla aynı yerde duruyor.
        Chip.Dismiss();
        VolumeCard.Present(audio, changed);

        if (!VolumePopup.IsOpen)
        {
            PlaceVolumeChip();
            VolumePopup.IsOpen = true;
        }
    }

    private async void VolumePopup_Opened(object? sender, EventArgs e)
    {
        await VolumeCard.RevealAsync();
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

        // Saat çipinin kutucuğuyla aynı boy: dikey çubukta alt alta, kısa; yatayda rakamlara göre geniş.
        bool isTall = Orientation == Orientation.Vertical;
        ClockDrops.Configure(_settings.Thickness, IslandCornerRadius, isTall);
        Size size = isTall ? new Size(_settings.Thickness, ClockDrops.TileLength) : new Size(ClockDrops.TileLength, _settings.Thickness);
        VolumeCard.SetSize(size, _settings.Thickness, IslandCornerRadius);
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
        return GroupPanel.IsOpen || TrayPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen || CalendarPopup.IsOpen || PreviewPopup.IsOpen || _geniesPlaying > 0;
    }

    /// <summary>Dönüştür kartı şarkı kartının yerinde duruyor (ana ekran): şarkı, Claude ve çeviri kartı çıkmaz.</summary>
    private bool IsChipSpotTaken => Screen.Primary && (ConvertCard.Current != null || LyricsMode.Shared.HoldsChipSpot);

    /// <summary>Bu ekranda tam ekran bir pencere var (çubuk geri çekildi).</summary>
    internal bool IsFullScreenHere => _fullScreenOnThisBar;

    /// <summary>Dönüştür kartı açılıyor: şarkı ya da çeviri kartı yerini verir; hatırlatıcı kalır.</summary>
    internal void YieldChipSpot()
    {
        if (ChipPopup.IsOpen && !IsReminderShown)
        {
            Chip.Dismiss(immediately: true);
        }
    }

    /// <summary>
    /// Fare düğmeye gelince kartın camını ve görüntüsünü tazeler: basış geldiğinde animasyon güncel görüntüyle başlar,
    /// gerçek kart da camı yeniden almak zorunda kalmaz.
    /// </summary>
    private async void PanelButton_MouseEnter(object sender, MouseEventArgs e)
    {
        Popup popup = sender == StartButton ? StartPopup : sender == GroupsButton ? GroupPanel : sender == TrayOverflowButton ? TrayPopup
            : sender == Clock ? CalendarPopup : QuickPopup;
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

        var popups = new List<Popup> { GroupPanel };
        if (_settings.ShowClock)
        {
            popups.Add(CalendarPopup);
        }
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
        ClosePopup(CalendarPopup);
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
        else if (e.Key == Key.Escape && (GroupPanel.IsOpen || TrayPopup.IsOpen || CalendarPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen))
        {
            ClosePanel();
            e.Handled = true;
        }
    }

    /// <summary>Düzen klasörüne tıklamak onu açar: kayıtlı yerler varsa pencereler oraya konur.</summary>
    private void LayoutFolder_Click(object sender, RoutedEventArgs e)
    {
        AppGroup group = GroupOf(sender);
        List<GroupItem> items = group.Items.ToList();

        ClosePanel();

        if (group.HasLayout)
        {
            _ = OpenLayoutAsync(items);
            return;
        }

        // Açık olanı ikinci kez açma; önce onları getir ki yeni açılanlar üstte kalsın.
        foreach (GroupItem item in items.Where(item => item.IsRunning))
        {
            item.NextWindow()?.BringToFront();
        }
        ReportFailures(items.Where(item => !item.IsRunning && !Launcher.Launch(item)).ToList());
    }

    /// <summary>Çalışma düzeni: açık olanlar yerine konup öne gelir, kapalılar açılır ve penceresi gelince yerine konur.</summary>
    private async Task OpenLayoutAsync(List<GroupItem> items)
    {
        var failures = new List<GroupItem>();
        var placing = new List<Task>();
        foreach (GroupItem item in items)
        {
            if (ShownWindow(item) is { } window)
            {
                item.Spot?.Apply(window);
                window.BringToFront();
            }
            else if (!Launcher.Launch(item))
            {
                failures.Add(item);
            }
            else if (item.Spot is { } spot)
            {
                placing.Add(PlaceWhenOpenedAsync(item, spot));
            }
        }

        ReportFailures(failures);
        await Task.WhenAll(placing);
    }

    private async Task PlaceWhenOpenedAsync(GroupItem item, WindowSpot spot)
    {
        long until = Environment.TickCount64 + LayoutWaitMs;
        ApplicationWindow? window;
        while ((window = ShownWindow(item)) == null && Environment.TickCount64 < until)
        {
            await Task.Delay(LayoutPollMs);
        }

        if (window == null)
        {
            return;
        }

        // Uygulamalar açılırken kendi son yerlerine de geçiyor; yerleşince bir kez daha.
        spot.Apply(window);
        await Task.Delay(LayoutSettleMs);
        if (ShownWindow(item) == window)
        {
            spot.Apply(window);
        }
    }

    /// <summary>
    /// Öğenin ekrandaki penceresi. Dosya ve klasör hiçbir uygulamayla eşleşmez: onu gösteren pencere, başlığındaki
    /// addan ya da Gezgin'in klasöründen bulunur.
    /// </summary>
    private ApplicationWindow? ShownWindow(GroupItem item)
    {
        return item.IsDocument
            ? _allWindows.OfType<ApplicationWindow>().FirstOrDefault(window => WindowDocument.Shows(window, item.Path))
            : item.LayoutWindow;
    }

    private void NewLayout_Click(object sender, RoutedEventArgs e)
    {
        ClosePanel();
        StartLayoutEditor(target: null);
    }

    /// <summary>Kayıtlı düzen yerinde açılır, kaydedilince grubun öğeleri yenisiyle değişir.</summary>
    private void EditLayoutItem_Click(object sender, RoutedEventArgs e)
    {
        AppGroup group = GroupOf(sender);
        ClosePanel();
        _ = OpenLayoutAsync(group.Items.ToList());
        StartLayoutEditor(group);
    }

    private void StartLayoutEditor(AppGroup? target)
    {
        LayoutEditor.Start(this, bannerAtBottom: AppBarEdge == AppBarEdge.Top, LayoutCount, () => SaveLayout(target));
    }

    /// <summary>Ekranda görünen pencereler (küçültülmüş ve başka masaüstündekiler değil), en üstteki önce.</summary>
    private List<ApplicationWindow> LayoutWindows()
    {
        List<IntPtr> order = DesktopWindows.ZOrder();
        int Depth(ApplicationWindow window) => order.IndexOf(window.Handle) is var index and >= 0 ? index : int.MaxValue;

        return _allWindows.OfType<ApplicationWindow>()
            .Where(window => DesktopWindows.IsShown(window.Handle))
            .OrderBy(Depth)
            .ToList();
    }

    /// <summary>Düzenleme kartındaki sayı, diske bakmadan: belge ve klasör pencereleri ayrı, öteki uygulamalar birer kez.</summary>
    private int LayoutCount()
    {
        return LayoutWindows().Select(window => TaskGroup.KeyOf(window) + "|" + WindowDocument.Key(window)).Distinct().Count();
    }

    /// <summary>
    /// Ekrandaki uygulamaları ve yerlerini gruba yazar; yeni düzende yeni grup açılır. Grubun adını döner. Dosya ya da
    /// klasör gösteren pencerede o kaydedilir, uygulamada en üstteki penceresi. Alttan üste sıralı: "Hepsini aç" sırayla
    /// öne getirdiği için üst üste binenler yine aynı sırada durur.
    /// </summary>
    private string? SaveLayout(AppGroup? target)
    {
        var items = new List<GroupItem>();
        foreach (ApplicationWindow window in LayoutWindows())
        {
            GroupItem? item = WindowDocument.Find(window) is { } document ? GroupItem.FromPath(document) : PinItemFor(window);
            if (item != null && WindowSpot.Capture(window) is { } spot && !items.Any(other => SamePath(other, item)))
            {
                item.Spot = spot;
                items.Add(item);
            }
        }
        items.Reverse();

        if (items.Count == 0)
        {
            return null;
        }

        AppGroup group = target != null && _settings.Groups.Contains(target) ? target : new AppGroup { Name = NextLayoutName() };
        group.Items = new ObservableCollection<GroupItem>(items);
        if (!_settings.Groups.Contains(group))
        {
            _settings.Groups.Add(group);
        }
        _settings.Save();
        _runTracker?.Refresh();
        return group.Name;
    }

    private string NextLayoutName()
    {
        for (int number = 1; ; number++)
        {
            string name = $"Düzen {number}";
            if (_settings.Groups.All(group => group.Name != name))
            {
                return name;
            }
        }
    }

    private void ReportFailures(IReadOnlyCollection<GroupItem> failed)
    {
        if (failed.Count > 0)
        {
            MessageBox.Show(this, "Şunlar açılamadı:\n" + string.Join("\n", failed.Select(item => item.Path)),
                "IslandBar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region Adding items
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

    internal static bool SetDropEffect(DragEventArgs e)
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
