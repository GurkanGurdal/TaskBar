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

namespace GrupBar;

public partial class BarWindow : AppBarWindow
{
    private static readonly Brush DropHighlight = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

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
    private const double GlassChangeThreshold = 0.004;
    private const int GlassMaxIdleTicks = 75;
    private const double TintSampleDistance = 24;
    private const double TintSampleLength = 40;
    private const double PanelShadowMargin = 20;
    private const double StartPanelHeight = 620;
    private const double StartPanelScreenMargin = 40;
    private const int PreviewShowDelayMs = 350;
    private const int PreviewHideDelayMs = 250;
    private const int PanelStaggerMaxItems = 16;
    private const int PanelStaggerStepMs = 22;

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

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private readonly AppSettings _settings;
    private readonly ShellManager _shellManager;
    private readonly NotificationArea _notificationArea;
    private readonly ICollectionView _taskWindows;
    private readonly ListCollectionView? _trayBattery;
    private readonly ListCollectionView? _trayOthers;
    private IntPtr _windowsTray;
    private AppGroup? _addTargetGroup;
    private readonly DispatcherTimer _dragOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _startMenuWasOpen;
    private readonly ObservableCollection<TaskGroup> _taskGroups = new();
    private TaskGroup? _pressedTaskGroup;
    private ApplicationWindow? _pressedActiveWindow;
    private readonly DispatcherTimer _previewShowTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewShowDelayMs) };
    private readonly DispatcherTimer _previewHideTimer = new() { Interval = TimeSpan.FromMilliseconds(PreviewHideDelayMs) };
    private Button? _previewButton;
    private readonly ScaleTransform _quickScale = new();
    private readonly ScaleTransform _startScale = new();
    private readonly IDisposable? _volumeWatcher;
    private readonly GroupRunTracker? _runTracker;
    private Point? _tileDragStart;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _clockMinute;
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private int _autoHideTicks;
    private readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private WriteableBitmap? _glassBitmap;
    private double _glassSignature = double.NaN;
    private int _glassIdleTicks;
    private bool _glassBusy;
    private bool _recomposeToggle;
    private readonly SolidColorBrush _tintBrush = new(GlassTint.OnDark);
    private Color _tintTarget = GlassTint.OnDark;
    private bool _fullScreenOnThisBar;
    private bool _cursorInBarZone;
    private bool _startMenuVisible;

    public BarWindow(ShellManager shellManager, AppSettings settings, AppBarScreen screen)
        : base(shellManager.AppBarManager, shellManager.ExplorerHelper, shellManager.FullScreenHelper,
            screen, settings.Edge, settings.AutoHideBar ? AppBarMode.AutoHide : AppBarMode.Normal, settings.Thickness)
    {
        _settings = settings;

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
        TaskList.ItemsSource = _taskGroups;
        SyncTaskGroups();
        _previewShowTimer.Tick += PreviewShowTimer_Tick;
        _previewHideTimer.Tick += PreviewHideTimer_Tick;
        _dragOpenTimer.Tick += DragOpenTimer_Tick;

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
        }
        QuickPanel.RenderTransform = _quickScale;
        QuickPanel.AllWindows = () => shellManager.Tasks.GroupedWindows.OfType<ApplicationWindow>();
        QuickPanel.CloseRequested += ClosePanel;

        StartPanel.RenderTransform = _startScale;
        StartPanel.Attach(settings);
        StartPanel.CloseRequested += ClosePanel;
        StartPanel.LaunchFailed += item => ReportFailures([item]);
        StartPanel.PinToBarRequested += item => AddItems(_settings.Pinned, [item]);
        StartPanel.WindowsStartRequested += OpenWindowsStartMenu;
        // Çubuk gizliyken de doğru yerde açılsın: düğme ekran dışında olabilir, pencere değil.
        StartPopup.PlacementTarget = this;

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
        var outer = new RectangleGeometry(new Rect(e.NewSize));
        var island = new RectangleGeometry(
            new Rect(ShadowBleed, ShadowBleed, Math.Max(0, e.NewSize.Width - 2 * ShadowBleed), Math.Max(0, e.NewSize.Height - 2 * ShadowBleed)),
            IslandCornerRadius, IslandCornerRadius);
        ShadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, island);
    }
    #endregion

    #region Glass
    private void BarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        IslandTint.Background = _tintBrush;
        _glassTimer.Tick += GlassTimer_Tick;
        _glassTimer.Start();
        RefreshGlass();
    }

    private void GlassHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        GlassHost.Clip = new RectangleGeometry(new Rect(e.NewSize), IslandCornerRadius, IslandCornerRadius);
        _glassSignature = double.NaN;
    }

    private void GlassTimer_Tick(object? sender, EventArgs e)
    {
        if (IsIslandOffScreen() || _fullScreenOnThisBar)
        {
            return;
        }

        // Görüntü alırken ada yakalamadan bir an hariç tutuluyor; gereksiz yere yapma.
        // Arkadaki değişimi adanın iki yanından anla, değişmese de seyrek tazele.
        double signature;
        try
        {
            signature = SampleBesideIsland();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran okunamıyor.
            return;
        }

        if (double.IsNaN(_glassSignature) || Math.Abs(signature - _glassSignature) > GlassChangeThreshold
            || ++_glassIdleTicks >= GlassMaxIdleTicks)
        {
            _glassSignature = signature;
            _glassIdleTicks = 0;
            RefreshGlass();

            foreach ((Popup popup, GlassCard card) in new[] { (GroupPanel, PanelCard), (TrayPopup, TrayCard), (PreviewPopup, PreviewCard) })
            {
                if (popup.IsOpen)
                {
                    _ = card.RefreshGlassAsync(animateTint: true);
                }
            }

            if (QuickPopup.IsOpen)
            {
                _ = QuickPanel.RefreshGlassAsync(animateTint: true);
            }

            if (StartPopup.IsOpen)
            {
                _ = StartPanel.RefreshGlassAsync(animateTint: true);
            }
        }
    }

    private async void RefreshGlass()
    {
        if (_glassBusy || AllowClose || !TryGetIslandRestRect(out System.Drawing.Rectangle area))
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

    private double SampleBesideIsland()
    {
        if (!TryGetIslandRestRect(out System.Drawing.Rectangle island))
        {
            return _glassSignature;
        }

        // Pencerenin adanın iki yanındaki kısmı saydam; görünen, adanın arkasıyla aynı sıradaki şeydir. Gölgeden uzak oku.
        int reach = (int)Math.Round(TintSampleDistance * DpiScale);
        int length = (int)Math.Round(TintSampleLength * DpiScale);
        System.Drawing.Rectangle first, second;
        if (Orientation == Orientation.Horizontal)
        {
            first = new System.Drawing.Rectangle(island.Left - reach - length, island.Top, length, island.Height);
            second = new System.Drawing.Rectangle(island.Right + reach, island.Top, length, island.Height);
        }
        else
        {
            first = new System.Drawing.Rectangle(island.Left, island.Top - reach - length, island.Width, length);
            second = new System.Drawing.Rectangle(island.Left, island.Bottom + reach, island.Width, length);
        }

        first.Intersect(Screen.Bounds);
        second.Intersect(Screen.Bounds);

        double total = 0;
        foreach (System.Drawing.Rectangle area in new[] { first, second })
        {
            if (!area.IsEmpty)
            {
                total += GlassCapture.AverageLuminance(area);
            }
        }
        return total;
    }

    protected override void OnAutoHideAnimationBegin(bool isHiding)
    {
        base.OnAutoHideAnimationBegin(isHiding);

        // Açılırken arkası taze olsun; ada henüz ekran dışında, görüntüye karışmaz.
        if (!isHiding && IsIslandOffScreen())
        {
            RefreshGlass();
        }
    }

    protected override void OnFullScreenEnter(FullScreenApp app)
    {
        base.OnFullScreenEnter(app);

        // Temel sınıf bazı pencereleri tam ekran saymıyor; gerçekten geri çekildiyse görüntü almayı bırak.
        _fullScreenOnThisBar = !Topmost;
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
        // Başlat ana ekranda açılıyor; Windows'taki gibi açıkken çubuk da görünsün.
        return base.ShouldAllowAutoHide() && !_cursorInBarZone && GroupPanel?.IsOpen != true && TrayPopup?.IsOpen != true
            && PreviewPopup?.IsOpen != true && QuickPopup?.IsOpen != true && StartPopup?.IsOpen != true && !(_startMenuVisible && Screen.Primary);
    }

    private async void Popup_OpenedOrClosed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AllowAutoHide));

        var popup = (Popup)sender!;
        GlassCard card = popup == GroupPanel ? PanelCard : popup == TrayPopup ? TrayCard : PreviewCard;

        if (!popup.IsOpen)
        {
            // Bir sonraki açılışta eski görüntü bir an görünmesin.
            card.BeginAnimation(OpacityProperty, null);
            WindowThumbnail.SetIsShown(card, false);
            return;
        }

        // Kart, arkasının görüntüsü hazır olunca belirir.
        await card.RefreshGlassAsync(animateTint: false);

        if (popup.IsOpen)
        {
            // Küçük resimleri Windows çizer, WPF ölçeklemesi onlara işlemez; önizleme yalnızca solarak gelir.
            AnimatePopupIn(card, popup == GroupPanel ? PanelScale : popup == TrayPopup ? TrayScale : null);
            WindowThumbnail.SetIsShown(card, true);

            if (popup == GroupPanel)
            {
                StaggerPanelSections();
            }
        }
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        bool changed = UpdateCursorZone();

        // Başlat kontrolü süreç adı okuyor; imleç kadar sık gerekmiyor.
        if (Screen.Primary && ++_autoHideTicks % 4 == 0)
        {
            bool visible = IsStartMenuOpen();
            changed |= visible != _startMenuVisible;
            _startMenuVisible = visible;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(AllowAutoHide));
        }
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

    public bool IsStartPanelOpen => StartPopup.IsOpen;

    public Task WarmStartPanelAsync() => StartPanel.WarmAsync();

    /// <summary>GrupBar'ın Başlat menüsünü açar ya da kapatır; Başlat düğmesi ve Windows tuşu.</summary>
    public void ToggleStartPanel()
    {
        bool wasOpen = StartPopup.IsOpen;
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
        StartPopup.IsOpen = true;
    }

    private async void StartPopup_OpenedOrClosed(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AllowAutoHide));

        if (!StartPopup.IsOpen)
        {
            StartPanel.Close();
            StartPanel.BeginAnimation(OpacityProperty, null);
            return;
        }

        StartPanel.FocusSearch();
        await StartPanel.OpenAsync();

        if (StartPopup.IsOpen)
        {
            AnimatePopupIn(StartPanel, _startScale);
            StartPanel.AnimateEntrance();
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
        // Tepsi kartı burada kapanmamalı: ikon kartın içindeyse bırakma olayı gelmez.
        GroupPanel.IsOpen = false;
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

        // Windows'taki gibi pil, kendi penceresi yerine hızlı ayarları açar.
        if (e.ChangedButton == MouseButton.Left && IsBatteryIcon(icon))
        {
            ToggleQuickSettings();
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
        TrayPopup.IsOpen = false;
    }

    private void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleQuickSettings();
    }

    private void ToggleQuickSettings()
    {
        bool wasOpen = QuickPopup.IsOpen;
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
            QuickPanel.BeginAnimation(OpacityProperty, null);
            UpdateQuickGlyphs();
            return;
        }

        await QuickPanel.OpenAsync();

        if (QuickPopup.IsOpen)
        {
            AnimatePopupIn(QuickPanel, _quickScale);
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
        bool wasOpen = TrayPopup.IsOpen;
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
        _clockTimer.Stop();
        _autoHideTimer.Stop();
        _glassTimer.Stop();

        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _dragOpenTimer.Stop();

        _volumeWatcher?.Dispose();
        _runTracker?.Dispose();
        Windows.Networking.Connectivity.NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;

        TaskList.ItemsSource = null;
        ((INotifyCollectionChanged)_taskWindows).CollectionChanged -= TaskWindows_CollectionChanged;
        foreach (TaskGroup group in _taskGroups)
        {
            group.Detach();
        }
        (_taskWindows as ListCollectionView)?.DetachFromSourceCollection();
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

    private void UnpinItem_Click(object sender, RoutedEventArgs e)
    {
        Unpin((GroupItem)((FrameworkElement)sender).DataContext);
    }

    internal void Unpin(GroupItem item)
    {
        _settings.Pinned.Remove(item);
        _settings.Save();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        SetDropEffect(e);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        // Grup düğmeleri bırakmayı kendileri işliyor; buraya gelen çubuğun geri kalanına bırakılmıştır.
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddItems(_settings.Pinned, paths.Select(GroupItem.FromPath));
        }

        e.Handled = true;
    }
    #endregion

    #region Running windows
    private void TaskWindows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncTaskGroups();
    }

    private void SyncTaskGroups()
    {
        var windowsByKey = new Dictionary<string, List<ApplicationWindow>>();
        foreach (ApplicationWindow window in _taskWindows.OfType<ApplicationWindow>())
        {
            string key = TaskGroup.KeyOf(window);
            if (!windowsByKey.TryGetValue(key, out List<ApplicationWindow>? windows))
            {
                windowsByKey[key] = windows = new List<ApplicationWindow>();
            }
            windows.Add(window);
        }

        // Düğmeler yer değiştirmesin: var olan gruplar yerinde kalır, yeniler sona eklenir.
        foreach (TaskGroup group in _taskGroups.Where(group => !windowsByKey.ContainsKey(group.Key)).ToList())
        {
            group.Detach();
            _taskGroups.Remove(group);
        }

        foreach ((string key, List<ApplicationWindow> windows) in windowsByKey)
        {
            TaskGroup? group = _taskGroups.FirstOrDefault(existing => existing.Key == key);
            if (group == null)
            {
                group = new TaskGroup(key);
                _taskGroups.Add(group);
            }
            group.SetWindows(windows);
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
    }

    private void TaskButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Çubuğa tıklamak pencereyi etkin olmaktan çıkarabilir; basıldığı andaki durumu sakla.
        _pressedTaskGroup = GroupOfTask(sender);
        _pressedActiveWindow = _pressedTaskGroup.ActiveWindow;
    }

    private void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        TaskGroup group = GroupOfTask(sender);
        ApplicationWindow? active = _pressedTaskGroup == group ? _pressedActiveWindow : group.ActiveWindow;
        _pressedTaskGroup = null;
        _pressedActiveWindow = null;

        ClosePanel();

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

        if (GroupPanel.IsOpen || TrayPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen || button.ContextMenu?.IsOpen == true)
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
        bool wasOpen = GroupPanel.IsOpen;
        ClosePanel();

        if (!wasOpen)
        {
            ShowGroupsPanel();
        }
    }

    private void ShowGroupsPanel()
    {
        _runTracker?.Refresh();
        GroupPanel.PlacementTarget = GroupsButton;
        PlacePopup(GroupPanel, PanelCard);
        OpenPanel();
    }

    /// <summary>Bölümler sırayla, hafifçe yukarı kayarak belirir.</summary>
    private void StaggerPanelSections()
    {
        for (int i = 0; i < Math.Min(GroupSections.Items.Count, PanelStaggerMaxItems); i++)
        {
            if (GroupSections.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement section)
            {
                continue;
            }

            TimeSpan delay = TimeSpan.FromMilliseconds(i * PanelStaggerStepMs);
            var slide = new TranslateTransform();
            section.RenderTransform = slide;
            section.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = delay });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240))
            {
                BeginTime = delay,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
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

    private void AnimatePopupIn(FrameworkElement card, ScaleTransform? scale)
    {
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));

        if (scale == null)
        {
            return;
        }

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var duration = TimeSpan.FromMilliseconds(200);

        card.RenderTransformOrigin = AppBarEdge switch
        {
            AppBarEdge.Top => new Point(0.5, 0),
            AppBarEdge.Left => new Point(0, 0.5),
            AppBarEdge.Right => new Point(1, 0.5),
            _ => new Point(0.5, 1)
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
    }

    private void OpenPanel()
    {
        // Panel, pencere etkin olmayınca kapanıyor; bu yüzden etkin olduğundan emin ol.
        Activate();
        GroupPanel.IsOpen = true;
    }

    private void ClosePanel()
    {
        GroupPanel.IsOpen = false;
        TrayPopup.IsOpen = false;
        QuickPopup.IsOpen = false;
        StartPopup.IsOpen = false;
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
        if (e.Key == Key.Escape && (GroupPanel.IsOpen || TrayPopup.IsOpen || QuickPopup.IsOpen || StartPopup.IsOpen))
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
            ((Border)sender).Background = DropHighlight;
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
                "GrupBar", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        bool canDrop = e.Data.GetDataPresent(DataFormats.FileDrop);

        // Dosyanın kendisine dokunmuyoruz, yolunu tutuyoruz; bağlantıya izin vermeyen kaynak için kopya da olur.
        DragDropEffects allowed = e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : DragDropEffects.Copy;
        e.Effects = canDrop ? allowed : DragDropEffects.None;
        e.Handled = true;
        return canDrop;
    }
    #endregion
}
