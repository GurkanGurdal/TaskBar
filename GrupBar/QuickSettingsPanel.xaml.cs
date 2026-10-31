using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Media.Control;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using Windows.System.Power;
using ManagedShell.WindowsTasks;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace GrupBar;

/// <summary>
/// Windows'un hızlı ayarlar paneli: çalan medya, kutular, parlaklık, ses, pil. Yalnızca açıkken çalışır;
/// kapanınca olay aboneliklerini ve zamanlayıcısını bırakır.
/// </summary>
public partial class QuickSettingsPanel : UserControl
{
    private const string NoNetwork = "";
    private const double PausedArtScale = 0.9;

    private const int PageSlideMs = 300;
    private const double TilePageHeight = 152;
    private const double PageDotIdleOpacity = 0.4;

    /// <summary>Kapak arka planının opaklığı; camı örtmeyecek kadar hafif.</summary>
    private const double MediaBackdropOpacity = 0.35;
    private const int EqualizerFrameRate = 20;
    private static readonly int[] EqualizerPeriodsMs = { 430, 560, 370 };

    /// <summary>Bir kapağın "renkli" sayılması için piksel başına en az doygunluk ağırlığı.</summary>
    private const double MinAccentWeight = 8;
    private const double MinAccentLuma = 165;
    private const byte KeyWin = 0x5B;
    private const byte KeyControl = 0x11;
    private const uint KeyRelease = 2;
    private const string AirplaneKey = @"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState";
    private const string WifiSettingsUri = "ms-settings:network-wifi";
    private const string BluetoothSettingsUri = "ms-settings:bluetooth";
    private const string ContainerIdProperty = "System.Devices.Aep.ContainerId";
    private const int BluetoothConnectWaitMs = 12000;

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly QuickTile _wifiTile;
    private readonly QuickTile _bluetoothTile;
    private readonly QuickTile _airplaneTile;
    private readonly QuickTile _energyTile;
    private readonly QuickTile[][] _pages;
    private int _page;
    private int _pageTransition;
    private int _subTransition;
    private uint[] _projectTopologies = [];
    private DeviceWatcher? _castWatcher;
    private string? _subSettingsUri;
    private Action<string>? _promptAction;
    private WiFiAdapter? _wifiAdapter;
    private int _wifiPage;
    private int _wifiListVersion;
    private readonly List<BluetoothEntry> _bluetoothEntries = new();
    private int _bluetoothPage;
    private int _bluetoothAttempt;
    private Border[] _pageDots = [];
    private bool _open;

    private Radio? _wifiRadio;
    private Radio? _bluetoothRadio;

    private AudioEndpoint? _audio;
    private bool _updatingSliders;
    private int _brightnessTarget = -1;
    private bool _brightnessBusy;

    private MediaManager? _mediaManager;
    private MediaSession? _mediaSession;
    private string? _mediaTrack;
    private bool _mediaPlaying;
    private TimeSpan _mediaStart;
    private TimeSpan _mediaDuration;
    private TimeSpan _mediaPosition;
    private DateTimeOffset _mediaPositionAt;

    public QuickSettingsPanel()
    {
        InitializeComponent();

        _wifiTile = new QuickTile("", "Wi-Fi", () => ToggleRadio(_wifiRadio, WifiSettingsUri), OpenWifiPage);
        _bluetoothTile = new QuickTile("", "Bluetooth", () => ToggleRadio(_bluetoothRadio, BluetoothSettingsUri), OpenBluetoothPage);
        _airplaneTile = new QuickTile("", "Uçak modu", () => OpenUri("ms-settings:network-airplanemode"));
        _energyTile = new QuickTile("", "Enerji tasarrufu", () => OpenUri("ms-settings:batterysaver"));

        _pages = new[]
        {
            new[]
            {
                _wifiTile,
                _bluetoothTile,
                _airplaneTile,
                _energyTile,
                new QuickTile("\uE776", "Erişilebilirlik", OpenAccessibilityPage, opensMenu: true),
                new QuickTile("\uEBC6", "Yansıt", OpenProjectPage, opensMenu: true)
            },
            new[]
            {
                new QuickTile("", "Gece ışığı", () => OpenUri("ms-settings:nightlight")),
                new QuickTile("", "Canlı altyazı", () => SendShortcut(0x4C, control: true)), // Win+Ctrl+L
                new QuickTile("", "Mobil etkin nokta", () => OpenUri("ms-settings:network-mobilehotspot")),
                new QuickTile("", "Yakın paylaşım", () => OpenUri("ms-settings:crossdevice")),
                new QuickTile("\uE703", "Kablosuz ekran", OpenCastPage, opensMenu: true)
            }
        };

        Pages.ItemsSource = _pages;
        CreatePageDots();
        ShowPage(0, animate: false);
        _pollTimer.Tick += (_, _) =>
        {
            PollAudio();
            TickMediaProgress();
        };

        // Boş yere tıklamak çubuğun fare olayına gidip paneli kapatmasın.
        MouseDown += (_, e) => e.Handled = true;
    }

    /// <summary>Bir eylem başka bir pencere açacaksa panel önce kapanır.</summary>
    public event Action? CloseRequested;

    /// <summary>Medya uygulamasının ikonunu ve penceresini bulmak için; tüm ekranlardaki pencereler.</summary>
    public Func<IEnumerable<ApplicationWindow>> AllWindows { get; set; } = Enumerable.Empty<ApplicationWindow>;

    /// <summary>Çubuktaki ikon için: ses düzeyine uygun simge.</summary>
    internal static string VolumeGlyphFor(AudioEndpoint? audio)
    {
        if (audio == null || audio.Muted)
        {
            return VolumeIcon.MutedGlyph;
        }

        float level = audio.Level;
        return level <= 0 ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
    }

    /// <summary>Çubuktaki ikon için: bağlantı türü ve internete çıkıp çıkamadığı.</summary>
    internal static string NetworkGlyph()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null)
            {
                return NoNetwork;
            }

            bool hasInternet = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            return profile.IsWlanConnectionProfile
                ? hasInternet ? "" : ""
                : hasInternet ? "" : "";
        }
        catch (COMException)
        {
            return NoNetwork;
        }
    }

    /// <summary>Durumu okur, görüntüyü alır. Bittiğinde panel gösterilmeye hazırdır.</summary>
    public async Task OpenAsync()
    {
        _open = true;
        ShowPage(0, animate: false);

        _audio = AudioEndpoint.TryOpen();
        VolumeRow.Visibility = _audio != null ? Visibility.Visible : Visibility.Collapsed;
        PollAudio();
        _pollTimer.Start();

        UpdateBattery();
        UpdateStaticTiles();

        // Yavaş kaynaklar (WMI, medya) paneli bekletmesin; geç gelen sonra yerine oturur.
        await Task.WhenAny(Task.WhenAll(LoadBrightnessAsync(), LoadRadiosAsync(), LoadMediaAsync()), Task.Delay(250));

        if (_open)
        {
            await RefreshGlassAsync(animateTint: false);
        }
    }

    public void Close()
    {
        _open = false;
        CloseSubPage(animate: false);
        _pollTimer.Stop();
        _audio = null;

        if (_wifiRadio != null)
        {
            _wifiRadio.StateChanged -= Radio_StateChanged;
        }
        if (_bluetoothRadio != null)
        {
            _bluetoothRadio.StateChanged -= Radio_StateChanged;
        }
        _wifiRadio = null;
        _bluetoothRadio = null;

        BindMediaSession(null);
        if (_mediaManager != null)
        {
            _mediaManager.CurrentSessionChanged -= MediaManager_CurrentSessionChanged;
        }
        _mediaManager = null;
    }

    /// <summary>
    /// Sırayla: iki kart aynı pencerede ve görüntü alırken pencere bir an hariç tutuluyor; aynı anda alırlarsa
    /// biri bitirip hariç tutmayı kaldırınca öbürü kendini görür.
    /// </summary>
    public async Task RefreshGlassAsync(bool animateTint)
    {
        if (MediaCard.Visibility == Visibility.Visible)
        {
            await MediaCard.RefreshGlassAsync(animateTint);
        }
        await SettingsCard.RefreshGlassAsync(animateTint);
    }

    #region Tiles
    /// <summary>Windows'taki gibi: sayfalar alt alta durur, şerit tek parça kayar.</summary>
    private void ShowPage(int page, bool animate = true)
    {
        int from = _page;
        _page = page;
        _pageTransition++;

        UpdatePageDots(animate);

        double target = -page * TilePageHeight;
        if (!animate || from == page || !SystemParameters.ClientAreaAnimation)
        {
            PagesSlide.BeginAnimation(TranslateTransform.YProperty, null);
            PagesSlide.Y = target;
            Pages.CacheMode = null;
            return;
        }

        // Kayarken şerit önbellekten çizilsin: kutular her karede yeniden çizilmez, kare düşmez.
        int transition = _pageTransition;
        Pages.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);

        var slide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(PageSlideMs))
        {
            // Basar basmaz hareket etsin; yavaş başlayan eğri gecikme gibi hissediliyor.
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        slide.Completed += (_, _) =>
        {
            // Durunca önbellek kalksın, yazılar keskin kalsın.
            if (transition == _pageTransition)
            {
                Pages.CacheMode = null;
            }
        };
        PagesSlide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Noktalar hep aynı boyda; etkin olan yalnızca parlaklığıyla ayrılır.</summary>
    private void UpdatePageDots(bool animate)
    {
        for (int i = 0; i < _pageDots.Length; i++)
        {
            Border dot = _pageDots[i];
            double opacity = i == _page ? 1 : PageDotIdleOpacity;

            if (animate && SystemParameters.ClientAreaAnimation)
            {
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(PageSlideMs)));
            }
            else
            {
                dot.BeginAnimation(OpacityProperty, null);
                dot.Opacity = opacity;
            }
        }
    }

    private void CreatePageDots()
    {
        _pageDots = new Border[_pages.Length];
        for (int i = 0; i < _pages.Length; i++)
        {
            int target = i;
            var dot = new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = Brushes.White
            };

            // Nokta küçük; tıklama alanı çevresini de kapsasın.
            var hitArea = new Border { Background = Brushes.Transparent, Padding = new Thickness(4, 5, 4, 5), Child = dot };
            hitArea.MouseDown += (_, e) =>
            {
                ShowPage(target);
                e.Handled = true;
            };
            _pageDots[i] = dot;
            PageDots.Children.Add(hitArea);
        }
    }

    private void Tiles_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        int next = Math.Clamp(_page + (e.Delta < 0 ? 1 : -1), 0, _pages.Length - 1);
        if (next != _page)
        {
            ShowPage(next);
        }
        e.Handled = true;
    }

    private void TilePrimary_Click(object sender, RoutedEventArgs e)
    {
        ((QuickTile)((FrameworkElement)sender).DataContext).Primary();
    }

    private void TileMore_Click(object sender, RoutedEventArgs e)
    {
        ((QuickTile)((FrameworkElement)sender).DataContext).More?.Invoke();
    }

    private void UpdateStaticTiles()
    {
        // Uçak modunu değiştirecek belgeli bir API yok: kutu durumu gösterir, tıklanınca ayarı açar.
        using RegistryKey? airplane = Registry.LocalMachine.OpenSubKey(AirplaneKey);
        _airplaneTile.IsOn = airplane?.GetValue(string.Empty) is 1;

        try
        {
            _energyTile.IsOn = PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (COMException)
        {
            _energyTile.IsOn = false;
        }
    }

    private void OpenUri(string uri)
    {
        CloseRequested?.Invoke();
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>Windows'un kendi açılır penceresini kısayoluyla açar (Yansıt, Kablosuz ekran, Canlı altyazı).</summary>
    private void SendShortcut(byte key, bool control = false)
    {
        CloseRequested?.Invoke();

        keybd_event(KeyWin, 0, 0, IntPtr.Zero);
        if (control)
        {
            keybd_event(KeyControl, 0, 0, IntPtr.Zero);
        }
        keybd_event(key, 0, 0, IntPtr.Zero);
        keybd_event(key, 0, KeyRelease, IntPtr.Zero);
        if (control)
        {
            keybd_event(KeyControl, 0, KeyRelease, IntPtr.Zero);
        }
        keybd_event(KeyWin, 0, KeyRelease, IntPtr.Zero);
    }

    [DllImport("user32")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
    #endregion

    #region Radios
    private async Task LoadRadiosAsync()
    {
        try
        {
            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            if (!_open)
            {
                return;
            }

            _wifiRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.WiFi);
            _bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            if (_wifiRadio != null)
            {
                _wifiRadio.StateChanged += Radio_StateChanged;
            }
            if (_bluetoothRadio != null)
            {
                _bluetoothRadio.StateChanged += Radio_StateChanged;
            }

            await UpdateRadioTilesAsync();
        }
        catch (COMException)
        {
            // Radyo yönetimi hizmeti kapalıysa kutular kapalı görünür, tıklayınca ayar açılır.
        }
    }

    private void Radio_StateChanged(Radio sender, object args)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_open)
            {
                await UpdateRadioTilesAsync();
            }
        });
    }

    private async Task UpdateRadioTilesAsync()
    {
        bool wifiOn = _wifiRadio?.State == RadioState.On;
        _wifiTile.IsOn = wifiOn;
        _wifiTile.Label = !wifiOn ? "Wi-Fi" : ConnectedWifiName() ?? "Kullanılabilir";

        bool bluetoothOn = _bluetoothRadio?.State == RadioState.On;
        _bluetoothTile.IsOn = bluetoothOn;
        string? device = bluetoothOn ? await ConnectedBluetoothNameAsync() : null;
        _bluetoothTile.Label = device ?? "Bluetooth";
    }

    private static string? ConnectedWifiName()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            return profile is { IsWlanConnectionProfile: true } ? profile.ProfileName : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static async Task<string?> ConnectedBluetoothNameAsync()
    {
        try
        {
            foreach (string selector in new[]
            {
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
            })
            {
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
                if (devices.FirstOrDefault(device => !string.IsNullOrEmpty(device.Name)) is { } connected)
                {
                    return connected.Name;
                }
            }
        }
        catch (COMException)
        {
            // Ad bulunamazsa kutuda "Bluetooth" yazar.
        }
        return null;
    }

    private async void ToggleRadio(Radio? radio, string settingsUri)
    {
        if (radio == null)
        {
            OpenUri(settingsUri);
            return;
        }

        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
                return;
            }
        }
        catch (COMException)
        {
            // Aşağıda ayarlara yönlendirilir.
        }
        OpenUri(settingsUri);
    }
    #endregion

    #region Sub pages
    /// <summary>Alt sayfayı açar; yandan kayarak gelir, ana görünüm öbür yana çekilir.</summary>
    private void OpenSubPage(string title, IEnumerable<QuickOption> options, string? note = null, string? settingsUri = null, string? settingsTip = null)
    {
        SubTitle.Text = title;
        SubItems.ItemsSource = options;
        SetSubNote(note);
        _subSettingsUri = settingsUri;
        SubSettings.ToolTip = settingsTip;
        AutomationProperties.SetName(SubSettings, settingsTip ?? string.Empty);
        SubSettings.Visibility = settingsUri != null ? Visibility.Visible : Visibility.Collapsed;

        _subTransition++;
        MainView.IsHitTestVisible = false;
        SubView.IsHitTestVisible = true;
        SubView.Visibility = Visibility.Visible;
        SlideSubPage(open: true);
    }

    private void CloseSubPage(bool animate = true)
    {
        StopCastWatcher();
        StopWifiPage();
        StopBluetoothPage();
        HidePrompt();
        _subTransition++;
        MainView.IsHitTestVisible = true;
        SubView.IsHitTestVisible = false;
        MainView.Visibility = Visibility.Visible;

        if (animate && SubView.Visibility == Visibility.Visible && SystemParameters.ClientAreaAnimation)
        {
            SlideSubPage(open: false);
            return;
        }

        MainSlide.BeginAnimation(TranslateTransform.XProperty, null);
        SubSlide.BeginAnimation(TranslateTransform.XProperty, null);
        MainSlide.X = 0;
        SubSlide.X = 0;
        MainView.CacheMode = null;
        SubView.CacheMode = null;
        SubView.Visibility = Visibility.Collapsed;
    }

    private void SlideSubPage(bool open)
    {
        double width = SettingsHost.ActualWidth;
        int transition = _subTransition;

        if (!SystemParameters.ClientAreaAnimation)
        {
            MainSlide.X = open ? -width : 0;
            SubSlide.X = open ? 0 : width;
            FinishSubSlide(open);
            return;
        }

        var cache = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        MainView.CacheMode = cache;
        SubView.CacheMode = cache;

        var duration = TimeSpan.FromMilliseconds(PageSlideMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(open ? 0 : width, duration) { EasingFunction = ease };
        if (open)
        {
            slide.From = width;
        }
        slide.Completed += (_, _) =>
        {
            if (transition == _subTransition)
            {
                FinishSubSlide(open);
            }
        };
        MainSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(open ? -width : 0, duration) { EasingFunction = ease });
        SubSlide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void FinishSubSlide(bool open)
    {
        MainView.CacheMode = null;
        SubView.CacheMode = null;

        // Gizlenen görünüm yerini korur, kartın boyu değişmez.
        if (open)
        {
            MainView.Visibility = Visibility.Hidden;
        }
        else
        {
            SubView.Visibility = Visibility.Collapsed;
        }
    }

    private void SetSubNote(string? note)
    {
        SubNote.Text = note ?? string.Empty;
        SubNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SubBack_Click(object sender, RoutedEventArgs e)
    {
        CloseSubPage();
    }

    private void SubSettings_Click(object sender, RoutedEventArgs e)

    {

        OpenUri(_subSettingsUri!);

    }

    private void SubOption_Click(object sender, RoutedEventArgs e)
    {
        ((QuickOption)((FrameworkElement)sender).DataContext).Action();
    }

    /// <summary>
    /// Alt sayfanın altında onay alanı. Bağlantıyı değiştiren her eylem buradan geçer:
    /// listede yanlış satıra tıklamak bağlantıyı koparmasın (Windows'taki Bağlan düğmesi gibi).
    /// </summary>
    private void ShowPrompt(string text, string confirm, Action<string> action, bool password = false)
    {
        _promptAction = action;
        SubPromptText.Text = text;
        SubPromptConfirm.Content = confirm;
        SubPassword.Clear();
        SubPassword.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        SubPromptConfirm.IsEnabled = !password;
        SubPrompt.Visibility = Visibility.Visible;

        if (password)
        {
            Dispatcher.BeginInvoke(() => SubPassword.Focus(), DispatcherPriority.Input);
        }
    }

    private void HidePrompt()
    {
        _promptAction = null;
        SubPassword.Clear();
        SubPrompt.Visibility = Visibility.Collapsed;
    }

    private void ConfirmPrompt()
    {
        Action<string>? action = _promptAction;
        string password = SubPassword.Password;
        HidePrompt();
        action?.Invoke(password);
    }

    private void SubPromptConfirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmPrompt();
    }

    private void SubPromptCancel_Click(object sender, RoutedEventArgs e)
    {
        HidePrompt();
    }

    private void SubPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        SubPromptConfirm.IsEnabled = SubPassword.Password.Length > 0;
    }

    private void SubPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SubPromptConfirm.IsEnabled)
        {
            ConfirmPrompt();
            e.Handled = true;
        }
    }

    private async void OpenProjectPage()
    {
        (uint Topology, string Title, string Detail, string Picture)[] modes =
        {
            (DisplayTopology.Internal, "Yalnızca bilgisayar ekranı", "İkinci ekran kapanır", "F0 M0,0H9V7H0Z M11,0H20V7H11Z M12,1V6H19V1H12Z"),
            (DisplayTopology.Clone, "Yinele", "İki ekranda aynı görüntü", "F1 M0,0H9V7H0Z M11,0H20V7H11Z M9.4,3H10.6V4H9.4Z"),
            (DisplayTopology.Extend, "Genişlet", "Masaüstü iki ekrana yayılır", "F1 M0,0H9.6V7H0Z M10.4,0H20V7H10.4Z"),
            (DisplayTopology.External, "Yalnızca ikinci ekran", "Bilgisayar ekranı kapanır", "F0 M0,0H9V7H0Z M1,1V6H8V1H1Z M11,0H20V7H11Z")
        };

        uint current = DisplayTopology.Current();
        var options = new List<QuickOption>();
        foreach ((uint topology, string title, string detail, string picture) in modes)
        {
            options.Add(new QuickOption(title, detail, () => ApplyTopology(topology, options), picture: picture)
            {
                IsSelected = topology == current
            });
        }

        OpenSubPage("Yansıt", options);
        _projectTopologies = modes.Select(mode => mode.Topology).ToArray();

        // Windows'a ilk sorgu bir saniyeyi bulabiliyor ve bu sırada ekranı da bekletiyor:
        // sayfa açılmayı beklemesin, kayma bitince arka planda sorulur.
        await Task.Delay(PageSlideMs);
        bool[] available = await Task.Run(() => modes.Select(mode => DisplayTopology.CanApply(mode.Topology)).ToArray());
        for (int i = 0; i < options.Count; i++)
        {
            options[i].IsAvailable = available[i];
        }
    }

    private async void ApplyTopology(uint topology, IReadOnlyList<QuickOption> options)
    {
        if (topology == DisplayTopology.Current())
        {
            return;
        }

        SetSubNote("Ekranlar ayarlanıyor...");
        int result = await Task.Run(() => DisplayTopology.Set(topology));
        if (result != 0)
        {
            SetSubNote($"Windows bu modu kuramadı (hata {result}).");
            return;
        }

        SetSubNote(null);
        for (int i = 0; i < options.Count; i++)
        {
            options[i].IsSelected = _projectTopologies[i] == topology;
        }
    }

    /// <summary>
    /// Yakındaki kablosuz ekranları listeler. Windows bir ekrana bağlanmayı uygulamalara açmıyor;
    /// bağlantı adımı kendi penceresinde yapılır.
    /// </summary>
    private void OpenCastPage()
    {
        var options = new ObservableCollection<QuickOption>
        {
            new("Bağlanma penceresini aç", "Windows'un kablosuz ekran penceresi", () => SendShortcut(0x4B), glyph: "")
        };

        OpenSubPage("Kablosuz ekran", options, "Ekranlar aranıyor...");

        try
        {
            _castWatcher = DeviceInformation.CreateWatcher(Windows.UI.ViewManagement.ProjectionManager.GetDeviceSelector());
            _castWatcher.Added += (_, device) => Dispatcher.BeginInvoke(() =>
            {
                // Seçici kablolu ekranları da döndürüyor; kablosuz olanlar Wi-Fi Direct uç noktası olarak gelir.
                if (device.Kind == DeviceInformationKind.AssociationEndpoint
                    && !string.IsNullOrEmpty(device.Name)
                    && options.All(option => option.Title != device.Name))
                {
                    options.Insert(options.Count - 1, new QuickOption(device.Name, "Bağlanmak için tıklayın", () => SendShortcut(0x4B), glyph: ""));
                    SetSubNote(null);
                }
            });
            _castWatcher.Updated += (_, _) => { };
            _castWatcher.Removed += (_, _) => { };
            _castWatcher.EnumerationCompleted += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (options.Count == 1)
                {
                    SetSubNote("Yakında kablosuz ekran bulunamadı.");
                }
            });
            _castWatcher.Start();
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetSubNote("Ekranlar listelenemedi; Windows penceresinden bağlanabilirsiniz.");
        }
    }

    private void StopCastWatcher()
    {
        if (_castWatcher == null)
        {
            return;
        }

        if (_castWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            _castWatcher.Stop();
        }
        _castWatcher = null;
    }

    private void OpenAccessibilityPage()
    {
        var options = new List<QuickOption>
        {
            new("Büyüteç", "Ekranın bir bölümünü büyütür", ToggleMagnifier, glyph: "") { IsSelected = IsProcessRunning("Magnify") },
            new("Ekran okuyucu", "Ekrandakileri sesli okur", () => SendShortcut(0x0D, control: true), glyph: "") { IsSelected = IsProcessRunning("Narrator") }, // Win+Ctrl+Enter
        };

        OpenSubPage("Erişilebilirlik", options, settingsUri: "ms-settings:easeofaccess", settingsTip: "Tüm erişilebilirlik ayarları");
    }

    private void ToggleMagnifier()
    {
        if (IsProcessRunning("Magnify"))
        {
            SendShortcut(0x1B); // Win+Esc kapatır
            return;
        }

        CloseRequested?.Invoke();
        Process.Start(new ProcessStartInfo("magnify.exe") { UseShellExecute = true });
    }

    private static bool IsProcessRunning(string name)
    {
        Process[] processes = Process.GetProcessesByName(name);
        foreach (Process process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }
    #endregion

    #region Wi-Fi page
    /// <summary>
    /// Yakındaki ağlar. Windows 11 24H2'den beri bu API'ler konum izni ister:
    /// izin yoksa Windows bir kez sorar, reddedilirse sayfa konum ayarını gösterir.
    /// </summary>
    private async void OpenWifiPage()
    {
        int page = ++_wifiPage;
        OpenSubPage("Wi-Fi", Array.Empty<QuickOption>(), "Ağlar aranıyor...", WifiSettingsUri, "Diğer Wi-Fi ayarları");

        if (_wifiRadio is { State: not RadioState.On })
        {
            SetSubNote("Wi-Fi kapalı. Açmak için geri dönüp kutuya tıklayın.");
            return;
        }

        try
        {
            WiFiAccessStatus access = await WiFiAdapter.RequestAccessAsync();
            if (page != _wifiPage)
            {
                return;
            }

            if (access != WiFiAccessStatus.Allowed)
            {
                SubItems.ItemsSource = new[]
                {
                    new QuickOption("Konum ayarlarını aç", "Masaüstü uygulamalarına konum izni verin", () => OpenUri("ms-settings:privacy-location"), glyph: ""),
                };
                SetSubNote("Windows, yakındaki ağları listelemek için konum izni istiyor.");
                return;
            }

            IReadOnlyList<WiFiAdapter> adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (page != _wifiPage)
            {
                return;
            }

            if (adapters.Count == 0)
            {
                SetSubNote("Wi-Fi bağdaştırıcısı bulunamadı.");
                return;
            }

            WiFiAdapter adapter = adapters[0];
            _wifiAdapter = adapter;
            adapter.AvailableNetworksChanged += WifiAdapter_AvailableNetworksChanged;

            // Önce son taramanın sonucu; yeni tarama birkaç saniye sürer.
            await ShowWifiNetworksAsync(adapter);
            await adapter.ScanAsync();
            if (adapter != _wifiAdapter)
            {
                return;
            }

            await ShowWifiNetworksAsync(adapter);
            SetSubNote(adapter.NetworkReport.AvailableNetworks.Count > 0 ? null : "Yakında ağ bulunamadı.");
        }
        catch (Exception e) when (IsDeviceError(e))
        {
            if (page == _wifiPage)
            {
                SetSubNote("Ağlar listelenemedi.");
            }
        }
    }

    private void StopWifiPage()
    {
        _wifiPage++;
        if (_wifiAdapter != null)
        {
            _wifiAdapter.AvailableNetworksChanged -= WifiAdapter_AvailableNetworksChanged;
            _wifiAdapter = null;
        }
    }

    private void WifiAdapter_AvailableNetworksChanged(WiFiAdapter sender, object args)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (sender != _wifiAdapter)
            {
                return;
            }

            try
            {
                await ShowWifiNetworksAsync(sender);
            }
            catch (Exception e) when (IsDeviceError(e))
            {
                // Liste bir sonraki taramaya kadar olduğu gibi kalır.
            }
        });
    }

    private async Task ShowWifiNetworksAsync(WiFiAdapter adapter)
    {
        int version = ++_wifiListVersion;
        ConnectionProfile? profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
        if (adapter != _wifiAdapter || version != _wifiListVersion)
        {
            return;
        }

        string? connected = profile?.WlanConnectionProfileDetails?.GetConnectedSsid();
        HashSet<string> saved = NetworkInformation.GetConnectionProfiles()
            .Where(known => known.IsWlanConnectionProfile)
            .Select(known => known.ProfileName)
            .ToHashSet();

        // Aynı ağın birden çok vericisi tek satır olur, en güçlüsü gösterilir.
        List<QuickOption> rows = adapter.NetworkReport.AvailableNetworks
            .Where(network => network.Ssid.Length > 0)
            .GroupBy(network => network.Ssid)
            .Select(group => group.MaxBy(network => network.NetworkRssiInDecibelMilliwatts)!)
            .OrderByDescending(network => network.Ssid == connected)
            .ThenByDescending(network => saved.Contains(network.Ssid))
            .ThenByDescending(network => network.SignalBars)
            .ThenBy(network => network.Ssid)
            .Select(network => WifiRow(adapter, network, network.Ssid == connected, saved.Contains(network.Ssid)))
            .ToList();
        SubItems.ItemsSource = rows;
    }

    private QuickOption WifiRow(WiFiAdapter adapter, WiFiAvailableNetwork network, bool connected, bool saved)
    {
        string detail = connected ? "Bağlı" : saved ? "Kayıtlı" : IsOpenNetwork(network) ? "Açık ağ" : "Güvenli";
        return new QuickOption(network.Ssid, detail, () => PromptWifi(adapter, network, connected, saved), glyph: SignalGlyph(network.SignalBars))
        {
            IsSelected = connected
        };
    }

    private void PromptWifi(WiFiAdapter adapter, WiFiAvailableNetwork network, bool connected, bool saved)
    {
        if (connected)
        {
            ShowPrompt(network.Ssid, "Bağlantıyı kes", _ => DisconnectWifi(adapter));
        }
        else if (saved || IsOpenNetwork(network))
        {
            ShowPrompt(network.Ssid, "Bağlan", _ => ConnectWifi(adapter, network, null));
        }
        else if (IsEnterpriseNetwork(network))
        {
            ShowPrompt($"{network.Ssid}: kurumsal oturum açma gerekiyor.", "Ayarları aç", _ => OpenUri(WifiSettingsUri));
        }
        else
        {
            PromptWifiPassword(adapter, network, $"{network.Ssid} parolası");
        }
    }

    private void PromptWifiPassword(WiFiAdapter adapter, WiFiAvailableNetwork network, string text)
    {
        ShowPrompt(text, "Bağlan", password => ConnectWifi(adapter, network, password), password: true);
    }

    private async void ConnectWifi(WiFiAdapter adapter, WiFiAvailableNetwork network, string? password)
    {
        SetSubNote($"{network.Ssid} ağına bağlanılıyor...");
        try
        {
            // Parola yoksa Windows kayıtlı profili kullanır.
            WiFiConnectionResult result = password == null
                ? await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic)
                : await adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            if (adapter != _wifiAdapter)
            {
                return;
            }

            switch (result.ConnectionStatus)
            {
                case WiFiConnectionStatus.Success:
                    SetSubNote(null);
                    break;
                case WiFiConnectionStatus.InvalidCredential:
                    SetSubNote(null);
                    PromptWifiPassword(adapter, network, $"{network.Ssid} parolası yanlış");
                    break;
                case WiFiConnectionStatus.UnspecifiedFailure when !IsOpenNetwork(network):
                    // Kayıtlı parola eskimiş olabilir; Windows bunu çoğu zaman yanlış parola diye bildirmiyor.
                    SetSubNote(null);
                    PromptWifiPassword(adapter, network, $"{network.Ssid} ağına bağlanılamadı, parolayı girin");
                    break;
                case WiFiConnectionStatus.NetworkNotAvailable:
                    SetSubNote("Ağ artık görünmüyor.");
                    break;
                case WiFiConnectionStatus.Timeout:
                    SetSubNote("Ağ yanıt vermedi.");
                    break;
                default:
                    SetSubNote("Bağlanılamadı.");
                    break;
            }

            await ShowWifiNetworksAsync(adapter);
            await UpdateRadioTilesAsync();
        }
        catch (Exception e) when (IsDeviceError(e))
        {
            if (adapter == _wifiAdapter)
            {
                SetSubNote("Bağlanılamadı.");
            }
        }
    }

    private async void DisconnectWifi(WiFiAdapter adapter)
    {
        try
        {
            adapter.Disconnect();
            await ShowWifiNetworksAsync(adapter);
            await UpdateRadioTilesAsync();
        }
        catch (Exception e) when (IsDeviceError(e))
        {
            SetSubNote("Bağlantı kesilemedi.");
        }
    }

    private static bool IsOpenNetwork(WiFiAvailableNetwork network)
    {
        return network.SecuritySettings.NetworkAuthenticationType is NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None;
    }

    private static bool IsEnterpriseNetwork(WiFiAvailableNetwork network)
    {
        return network.SecuritySettings.NetworkAuthenticationType is NetworkAuthenticationType.Wpa or NetworkAuthenticationType.Rsna or NetworkAuthenticationType.Ihv;
    }

    private static string SignalGlyph(byte bars)
    {
        return bars switch
        {
            >= 4 => "",
            3 => "",
            2 => "",
            _ => ""
        };
    }

    private static bool IsDeviceError(Exception e)
    {
        return e is COMException or UnauthorizedAccessException or InvalidOperationException;
    }
    #endregion

    #region Bluetooth page
    /// <summary>Eşleşmiş bir aygıt. Bağlantısı yalnızca <see cref="CanSwitch"/> ise (ses aygıtları) değiştirilebilir.</summary>
    private sealed record BluetoothEntry(string Name, string Glyph, Guid Container, bool CanSwitch, Func<bool> IsConnected, IDisposable Device);

    private async void OpenBluetoothPage()
    {
        int page = ++_bluetoothPage;
        OpenSubPage("Bluetooth", Array.Empty<QuickOption>(), "Aygıtlar okunuyor...", BluetoothSettingsUri, "Aygıt ekle ve diğer ayarlar");

        if (_bluetoothRadio is { State: not RadioState.On })
        {
            SetSubNote("Bluetooth kapalı. Açmak için geri dönüp kutuya tıklayın.");
            return;
        }

        var entries = new List<BluetoothEntry>();
        try
        {
            await AddClassicDevicesAsync(entries, page);
            await AddLowEnergyDevicesAsync(entries, page);
            entries = await Task.Run(() => MarkSwitchable(entries));
        }
        catch (Exception e) when (IsDeviceError(e))
        {
            entries.ForEach(entry => entry.Device.Dispose());
            if (page == _bluetoothPage)
            {
                SetSubNote("Aygıtlar listelenemedi.");
            }
            return;
        }

        // Yükleme sürerken sayfadan çıkıldıysa bu aygıtlar artık kimsenin değil.
        if (page != _bluetoothPage)
        {
            entries.ForEach(entry => entry.Device.Dispose());
            return;
        }

        _bluetoothEntries.AddRange(entries);
        ShowBluetoothRows();
        SetSubNote(_bluetoothEntries.Count > 0 ? null : "Eşleşmiş aygıt yok. Eklemek için sağ üstteki ayarları açın.");
    }

    private void StopBluetoothPage()
    {
        _bluetoothPage++;
        _bluetoothAttempt++;
        foreach (BluetoothEntry entry in _bluetoothEntries)
        {
            entry.Device.Dispose();
        }
        _bluetoothEntries.Clear();
    }

    private async Task AddClassicDevicesAsync(List<BluetoothEntry> entries, int page)
    {
        foreach ((DeviceInformation info, Guid container) in await PairedDevicesAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
        {
            if (await BluetoothDevice.FromIdAsync(info.Id) is { } device)
            {
                device.ConnectionStatusChanged += (_, _) => BluetoothStatusChanged(page);
                entries.Add(new BluetoothEntry(info.Name, ClassicGlyph(device.ClassOfDevice), container, false, () => device.ConnectionStatus == BluetoothConnectionStatus.Connected, device));
            }
        }
    }

    private async Task AddLowEnergyDevicesAsync(List<BluetoothEntry> entries, int page)
    {
        foreach ((DeviceInformation info, Guid container) in await PairedDevicesAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
        {
            // İki türü de destekleyen aygıt iki kez listelenir.
            if (entries.All(entry => entry.Name != info.Name) && await BluetoothLEDevice.FromIdAsync(info.Id) is { } device)
            {
                device.ConnectionStatusChanged += (_, _) => BluetoothStatusChanged(page);
                entries.Add(new BluetoothEntry(info.Name, LowEnergyGlyph(device.Appearance), container, false, () => device.ConnectionStatus == BluetoothConnectionStatus.Connected, device));
            }
        }
    }

    private static async Task<List<(DeviceInformation Info, Guid Container)>> PairedDevicesAsync(string selector)
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector, new[] { ContainerIdProperty });
        return devices
            .Where(device => !string.IsNullOrEmpty(device.Name))
            .Select(device => (device, device.Properties.TryGetValue(ContainerIdProperty, out object? id) && id is Guid container ? container : Guid.Empty))
            .ToList();
    }

    private static List<BluetoothEntry> MarkSwitchable(List<BluetoothEntry> entries)
    {
        return entries.Select(entry => entry with { CanSwitch = BluetoothAudio.IsSupported(entry.Container) }).ToList();
    }

    private void BluetoothStatusChanged(int page)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (page == _bluetoothPage)
            {
                _bluetoothAttempt++;
                SetSubNote(null);
                ShowBluetoothRows();
                await UpdateRadioTilesAsync();
            }
        });
    }

    private void ShowBluetoothRows()
    {
        List<QuickOption> rows = _bluetoothEntries
            .OrderByDescending(entry => entry.IsConnected())
            .ThenBy(entry => entry.Name)
            .Select(BluetoothRow)
            .ToList();
        SubItems.ItemsSource = rows;
    }

    private QuickOption BluetoothRow(BluetoothEntry entry)
    {
        bool connected = entry.IsConnected();
        return new QuickOption(entry.Name, connected ? "Bağlı" : "Bağlı değil", () => PromptBluetooth(entry), glyph: entry.Glyph)
        {
            IsSelected = connected
        };
    }

    private void PromptBluetooth(BluetoothEntry entry)
    {
        bool connected = entry.IsConnected();
        if (entry.CanSwitch)
        {
            ShowPrompt(entry.Name, connected ? "Bağlantıyı kes" : "Bağlan", _ => SwitchBluetooth(entry, !connected));
        }
        else
        {
            ShowPrompt($"{entry.Name}: Windows bu aygıtın bağlantısını yalnızca kendi ayarlarından değiştiriyor.", "Ayarları aç", _ => OpenUri(BluetoothSettingsUri));
        }
    }

    private async void SwitchBluetooth(BluetoothEntry entry, bool connect)
    {
        int page = _bluetoothPage;
        int attempt = ++_bluetoothAttempt;
        SetSubNote(connect ? $"{entry.Name} bağlanıyor..." : $"{entry.Name} bağlantısı kesiliyor...");

        Guid container = entry.Container;
        bool sent = await Task.Run(() => connect ? BluetoothAudio.Connect(container) : BluetoothAudio.Disconnect(container));
        if (attempt != _bluetoothAttempt)
        {
            return;
        }

        if (!sent)
        {
            SetSubNote("Windows bu komutu kabul etmedi.");
            return;
        }

        // Komutun başarısı yalnızca denendiğini gösterir; sonuç aygıtın durumundan okunur (BluetoothStatusChanged).
        await Task.Delay(BluetoothConnectWaitMs);
        if (page == _bluetoothPage && attempt == _bluetoothAttempt)
        {
            SetSubNote(connect ? "Aygıt yanıt vermedi. Açık ve yakında olduğundan emin olun." : "Aygıt bağlantıyı kesmedi.");
        }
    }

    private static string ClassicGlyph(BluetoothClassOfDevice kind)
    {
        // Çevre birimlerinde üst iki bit klavye ve işaretçi, alt bitler kumanda türü.
        int minor = (int)kind.MinorClass;
        return kind.MajorClass switch
        {
            BluetoothMajorClass.AudioVideo => "",
            BluetoothMajorClass.Computer => "",
            BluetoothMajorClass.Phone => "",
            BluetoothMajorClass.Peripheral when (minor & 0x10) != 0 => "",
            BluetoothMajorClass.Peripheral when (minor & 0x20) != 0 => "",
            BluetoothMajorClass.Peripheral when (minor & 0xF) is 1 or 2 => "",
            _ => ""
        };
    }

    private static string LowEnergyGlyph(BluetoothLEAppearance appearance)
    {
        if (appearance.Category == BluetoothLEAppearanceCategories.HumanInterfaceDevice)
        {
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Keyboard)
            {
                return "";
            }
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Mouse)
            {
                return "";
            }
            if (appearance.SubCategory == BluetoothLEAppearanceSubcategories.Gamepad || appearance.SubCategory == BluetoothLEAppearanceSubcategories.Joystick)
            {
                return "";
            }
        }
        return "";
    }
    #endregion

    #region Brightness and volume
    private async Task LoadBrightnessAsync()
    {
        int? brightness = await Task.Run(ScreenBrightness.Read);
        if (!_open)
        {
            return;
        }

        BrightnessRow.Visibility = brightness != null ? Visibility.Visible : Visibility.Collapsed;
        if (brightness != null)
        {
            _updatingSliders = true;
            BrightnessSlider.Value = brightness.Value;
            _updatingSliders = false;
        }
    }

    private async void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders)
        {
            return;
        }

        // WMI yavaş: sürüklerken yalnızca son değer gönderilir.
        _brightnessTarget = (int)Math.Round(e.NewValue);
        if (_brightnessBusy)
        {
            return;
        }

        _brightnessBusy = true;
        while (_brightnessTarget >= 0)
        {
            int target = _brightnessTarget;
            _brightnessTarget = -1;
            await Task.Run(() => ScreenBrightness.Set(target));
        }
        _brightnessBusy = false;
    }

    private void PollAudio()
    {
        if (_audio == null)
        {
            return;
        }

        try
        {
            VolumeGlyph.Glyph = VolumeGlyphFor(_audio);

            // Ses tuşlarıyla ya da başka uygulamadan değişebilir.
            if (!VolumeSlider.IsMouseCaptureWithin)
            {
                _updatingSliders = true;
                VolumeSlider.Value = Math.Round(_audio.Level * 100);
                _updatingSliders = false;
            }
        }
        catch (COMException)
        {
            // Aygıt çıkarılmış olabilir; bir sonraki açılışta varsayılan yeniden bulunur.
            _audio = null;
            VolumeRow.Visibility = Visibility.Collapsed;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSliders || _audio == null)
        {
            return;
        }

        try
        {
            _audio.Level = (float)(e.NewValue / 100);
            VolumeGlyph.Glyph = VolumeGlyphFor(_audio);
        }
        catch (COMException)
        {
            _audio = null;
        }
    }

    private void VolumeMute_Click(object sender, RoutedEventArgs e)
    {
        if (_audio == null)
        {
            return;
        }

        try
        {
            _audio.Muted = !_audio.Muted;
            VolumeGlyph.Glyph = VolumeGlyphFor(_audio);
        }
        catch (COMException)
        {
            _audio = null;
        }
    }

    private void VolumeMixer_Click(object sender, RoutedEventArgs e)
    {
        OpenUri("ms-settings:apps-volume");
    }
    #endregion

    #region Battery and settings
    private void UpdateBattery()
    {
        System.Windows.Forms.PowerStatus power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool hasBattery = (power.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0
            && power.BatteryLifePercent is >= 0 and <= 1;

        BatteryButton.Visibility = hasBattery ? Visibility.Visible : Visibility.Hidden;
        if (hasBattery)
        {
            int percent = (int)Math.Round(power.BatteryLifePercent * 100);
            bool charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            int step = (int)Math.Round(percent / 10.0);

            // Battery0-9 E850'den, Battery10 E83F; BatteryCharging0-10 EBAB'den başlar.
            BatteryGlyph.Text = charging
                ? ((char)(0xEBAB + step)).ToString()
                : step == 10 ? "" : ((char)(0xE850 + step)).ToString();
            BatteryText.Text = $"%{percent}";
        }
    }

    private void Battery_Click(object sender, RoutedEventArgs e)
    {
        OpenUri("ms-settings:batterysaver");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenUri("ms-settings:");
    }
    #endregion

    #region Media
    private async Task LoadMediaAsync()
    {
        try
        {
            MediaManager manager = await MediaManager.RequestAsync();
            if (!_open)
            {
                return;
            }

            _mediaManager = manager;
            _mediaManager.CurrentSessionChanged += MediaManager_CurrentSessionChanged;
            BindMediaSession(_mediaManager.GetCurrentSession());
            await UpdateMediaAsync();
        }
        catch (COMException)
        {
            // Medya kartı gizli kalır.
        }
    }

    private void MediaManager_CurrentSessionChanged(MediaManager sender, CurrentSessionChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_open && _mediaManager != null)
            {
                BindMediaSession(_mediaManager.GetCurrentSession());
                await UpdateMediaAsync();
            }
        });
    }

    private void BindMediaSession(MediaSession? session)
    {
        if (_mediaSession != null)
        {
            _mediaSession.MediaPropertiesChanged -= MediaSession_Changed;
            _mediaSession.PlaybackInfoChanged -= MediaSession_Changed;
            _mediaSession.TimelinePropertiesChanged -= MediaSession_TimelineChanged;
        }

        _mediaSession = session;

        if (_mediaSession != null)
        {
            _mediaSession.MediaPropertiesChanged += MediaSession_Changed;
            _mediaSession.PlaybackInfoChanged += MediaSession_Changed;
            _mediaSession.TimelinePropertiesChanged += MediaSession_TimelineChanged;
        }
        else
        {
            SetPlayingMotion(false);
        }
    }

    private void MediaSession_TimelineChanged(MediaSession sender, TimelinePropertiesChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_open)
            {
                UpdateMediaTimeline();
            }
        });
    }

    /// <summary>Uygulama konumu seyrek bildiriyor; aradaki süre çalarken buradan sayılır.</summary>
    private void UpdateMediaTimeline()
    {
        if (_mediaSession == null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = _mediaSession.GetTimelineProperties();
            _mediaStart = timeline.StartTime;
            _mediaDuration = timeline.EndTime - timeline.StartTime;
            _mediaPosition = timeline.Position;
            _mediaPositionAt = timeline.LastUpdatedTime;

            MediaTimeline.Visibility = _mediaDuration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
            if (_mediaDuration > TimeSpan.Zero)
            {
                _updatingSliders = true;
                MediaProgress.Maximum = _mediaDuration.TotalSeconds;
                _updatingSliders = false;
            }
            TickMediaProgress();
        }
        catch (COMException)
        {
            MediaTimeline.Visibility = Visibility.Collapsed;
        }
    }

    private void TickMediaProgress()
    {
        if (_mediaDuration <= TimeSpan.Zero || MediaProgress.IsMouseCaptureWithin)
        {
            return;
        }

        TimeSpan elapsed = _mediaPosition - _mediaStart;
        if (_mediaPlaying)
        {
            elapsed += DateTimeOffset.Now - _mediaPositionAt;
        }
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > _mediaDuration ? _mediaDuration : elapsed;

        _updatingSliders = true;
        MediaProgress.Value = elapsed.TotalSeconds;
        _updatingSliders = false;
        ShowMediaTimes(elapsed);
    }

    private void ShowMediaTimes(TimeSpan elapsed)
    {
        MediaElapsed.Text = FormatTime(elapsed);
        MediaRemaining.Text = "-" + FormatTime(_mediaDuration - elapsed);
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void MediaProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Sürüklerken süre elle gösterilir; konum bırakınca gönderilir.
        if (!_updatingSliders)
        {
            ShowMediaTimes(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private async void MediaProgress_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_mediaSession == null)
        {
            return;
        }

        // Kaydırıcı değerini bırakma olayından sonra da güncelliyor; onu bekle.
        await Dispatcher.Yield(DispatcherPriority.Input);
        TimeSpan target = _mediaStart + TimeSpan.FromSeconds(MediaProgress.Value);
        _mediaPosition = target;
        _mediaPositionAt = DateTimeOffset.Now;
        await _mediaSession.TryChangePlaybackPositionAsync(target.Ticks);
    }

    /// <summary>Çalarken küçük çubuklar oynar ve kapak tam boyda durur; duraklayınca kapak hafifçe küçülür.</summary>
    private void SetPlayingMotion(bool playing)
    {
        ScaleTransform[] bars = { MediaEq1, MediaEq2, MediaEq3 };
        MediaEqualizer.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < bars.Length; i++)
        {
            DoubleAnimation? bounce = null;
            if (playing)
            {
                bounce = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(EqualizerPeriodsMs[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                // Küçük bir süs için ekran hızında çizmeye gerek yok.
                Timeline.SetDesiredFrameRate(bounce, EqualizerFrameRate);
            }
            bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }

        double scale = playing ? 1 : PausedArtScale;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
        var duration = TimeSpan.FromMilliseconds(280);
        MediaArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        MediaArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
    }

    /// <summary>Yeni parçada kapak ve arka plan solarak gelir.</summary>
    private void FadeInNewTrack()
    {
        var duration = TimeSpan.FromMilliseconds(320);
        MediaArtHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));

        // Hedef sabit olmalı: o anki değer okunursa, solma sürerken gelen yeni parça arka planı silik bırakır.
        MediaBackdropImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, MediaBackdropOpacity, duration));
    }

    /// <summary>
    /// İlerleme çubuğunun rengi: kapağın canlı renklerinin ortalaması, koyu kalmasın diye açılmış.
    /// Renksiz kapakta beyaz kalır.
    /// </summary>
    private static Color CoverAccent(BitmapSource cover)
    {
        var bitmap = new FormatConvertedBitmap(cover, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

        double red = 0, green = 0, blue = 0, total = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            double saturation = max == 0 ? 0 : (max - min) / (double)max;
            double weight = saturation * saturation * max;
            blue += pixels[i] * weight;
            green += pixels[i + 1] * weight;
            red += pixels[i + 2] * weight;
            total += weight;
        }

        if (total < pixels.Length / 4.0 * MinAccentWeight)
        {
            return Color.FromRgb(0xF2, 0xF4, 0xF8);
        }

        (red, green, blue) = (red / total, green / total, blue / total);
        double luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue;
        double lift = luma >= MinAccentLuma ? 0 : (MinAccentLuma - luma) / (255 - luma);
        return Color.FromRgb(
            (byte)(red + (255 - red) * lift),
            (byte)(green + (255 - green) * lift),
            (byte)(blue + (255 - blue) * lift));
    }

    private void MediaSession_Changed<T>(MediaSession sender, T args)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_open)
            {
                await UpdateMediaAsync();
            }
        });
    }

    private async Task UpdateMediaAsync()
    {
        MediaSession? session = _mediaSession;
        bool wasVisible = MediaCard.Visibility == Visibility.Visible;

        if (session == null)
        {
            MediaCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
                GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();

                string track = $"{properties.Title}\n{properties.Artist}";
                bool newTrack = track != _mediaTrack;
                _mediaTrack = track;

                MediaTitle.Text = properties.Title;
                MediaArtist.Text = properties.Artist;
                BitmapSource? art = await ReadThumbnailAsync(properties);
                MediaArt.ImageSource = art;
                MediaBackdrop.ImageSource = art;
                MediaBackdropHost.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
                MediaProgress.Foreground = new SolidColorBrush(art != null ? CoverAccent(art) : Colors.White);
                if (newTrack)
                {
                    FadeInNewTrack();
                }
                MediaAppName.Text = AppNameOf(session.SourceAppUserModelId);
                MediaAppIcon.Source = FindMediaWindow(session.SourceAppUserModelId)?.Icon;
                MediaAppGlyph.Visibility = MediaAppIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;
                MediaPlayGlyph.Text = playback.PlaybackStatus == PlaybackStatus.Playing ? "" : "";
                MediaPrevious.IsEnabled = playback.Controls.IsPreviousEnabled;
                MediaNext.IsEnabled = playback.Controls.IsNextEnabled;
                MediaProgress.IsHitTestVisible = playback.Controls.IsPlaybackPositionEnabled;
                _mediaPlaying = playback.PlaybackStatus == PlaybackStatus.Playing;
                SetPlayingMotion(_mediaPlaying);
                UpdateMediaTimeline();
                MediaCard.Visibility = Visibility.Visible;
            }
            catch (COMException)
            {
                MediaCard.Visibility = Visibility.Collapsed;
            }
        }

        // Kart geç geldiyse yerleşim değişti; ikisinin de arkası yeniden alınsın.
        bool isVisible = MediaCard.Visibility == Visibility.Visible;
        if (_open && isVisible != wasVisible)
        {
            await RefreshGlassAsync(animateTint: false);
        }
    }

    private void MediaBackdropHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Bulanık görüntü kartın dışına taşar; camla aynı köşeden kırp.
        double radius = MediaCard.CornerRadius.TopLeft - 1;
        MediaBackdropHost.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    private static async Task<BitmapSource?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (properties.Thumbnail == null)
        {
            return null;
        }

        using var stream = await properties.Thumbnail.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(memory);
        memory.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 120;
        image.StreamSource = memory;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>"Spotify.exe" ya da "SpotifyAB.SpotifyMusic_abc!Spotify" gibi kimlikten görünen ad.</summary>
    private static string AppNameOf(string id)
    {
        string name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    private ApplicationWindow? FindMediaWindow(string id)
    {
        string name = AppNameOf(id);
        return AllWindows().FirstOrDefault(window =>
            string.Equals(window.AppUserModelID, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(System.IO.Path.GetFileNameWithoutExtension(window.WinFileName), name, StringComparison.OrdinalIgnoreCase));
    }

    private async void MediaPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession != null)
        {
            await _mediaSession.TrySkipPreviousAsync();
        }
    }

    private async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession != null)
        {
            await _mediaSession.TryTogglePlayPauseAsync();
        }
    }

    private async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession != null)
        {
            await _mediaSession.TrySkipNextAsync();
        }
    }

    private void MediaApp_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSession == null)
        {
            return;
        }

        string id = _mediaSession.SourceAppUserModelId;
        if (FindMediaWindow(id) is { } window)
        {
            CloseRequested?.Invoke();
            window.BringToFront();
        }
        else if (id.Contains('!'))
        {
            // Tepsiye küçülmüş Store uygulaması: penceresi yok, kimliğiyle açılır.
            CloseRequested?.Invoke();
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = true });
        }
    }
    #endregion
}
