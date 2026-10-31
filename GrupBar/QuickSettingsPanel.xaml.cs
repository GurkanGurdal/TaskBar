using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Control;
using Windows.Networking.Connectivity;
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
    private const byte KeyWin = 0x5B;
    private const byte KeyControl = 0x11;
    private const uint KeyRelease = 2;
    private const string AirplaneKey = @"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState";

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly QuickTile _wifiTile;
    private readonly QuickTile _bluetoothTile;
    private readonly QuickTile _airplaneTile;
    private readonly QuickTile _energyTile;
    private readonly QuickTile[][] _pages;
    private int _page;
    private bool _open;

    private Radio? _wifiRadio;
    private Radio? _bluetoothRadio;

    private AudioEndpoint? _audio;
    private bool _updatingSliders;
    private int _brightnessTarget = -1;
    private bool _brightnessBusy;

    private MediaManager? _mediaManager;
    private MediaSession? _mediaSession;

    public QuickSettingsPanel()
    {
        InitializeComponent();

        _wifiTile = new QuickTile("", "Wi-Fi", () => ToggleRadio(_wifiRadio, "ms-settings:network-wifi"), () => OpenUri("ms-settings:network-wifi"));
        _bluetoothTile = new QuickTile("", "Bluetooth", () => ToggleRadio(_bluetoothRadio, "ms-settings:bluetooth"), () => OpenUri("ms-settings:bluetooth"));
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
                new QuickTile("", "Erişilebilirlik", () => OpenUri("ms-settings:easeofaccess"), opensMenu: true),
                new QuickTile("", "Yansıt", () => SendShortcut(0x50), opensMenu: true) // Win+P
            },
            new[]
            {
                new QuickTile("", "Gece ışığı", () => OpenUri("ms-settings:nightlight")),
                new QuickTile("", "Canlı altyazı", () => SendShortcut(0x4C, control: true)), // Win+Ctrl+L
                new QuickTile("", "Mobil etkin nokta", () => OpenUri("ms-settings:network-mobilehotspot")),
                new QuickTile("", "Yakın paylaşım", () => OpenUri("ms-settings:crossdevice")),
                new QuickTile("", "Kablosuz ekran", () => SendShortcut(0x4B), opensMenu: true) // Win+K
            }
        };

        ShowPage(0);
        _pollTimer.Tick += (_, _) => PollAudio();

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
        ShowPage(0);

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
    private void ShowPage(int page)
    {
        _page = page;
        Tiles.ItemsSource = _pages[page];

        PageDots.Children.Clear();
        for (int i = 0; i < _pages.Length; i++)
        {
            int target = i;
            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Margin = new Thickness(2, 3, 2, 3),
                Fill = new SolidColorBrush(i == page ? Colors.White : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF))
            };
            dot.MouseDown += (_, e) =>
            {
                ShowPage(target);
                e.Handled = true;
            };
            PageDots.Children.Add(dot);
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
        }

        _mediaSession = session;

        if (_mediaSession != null)
        {
            _mediaSession.MediaPropertiesChanged += MediaSession_Changed;
            _mediaSession.PlaybackInfoChanged += MediaSession_Changed;
        }
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

                MediaTitle.Text = properties.Title;
                MediaArtist.Text = properties.Artist;
                ImageSource? art = await ReadThumbnailAsync(properties);
                MediaArt.ImageSource = art;
                MediaBackdrop.ImageSource = art;
                MediaBackdropHost.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
                MediaAppName.Text = AppNameOf(session.SourceAppUserModelId);
                MediaAppIcon.Source = FindMediaWindow(session.SourceAppUserModelId)?.Icon;
                MediaAppGlyph.Visibility = MediaAppIcon.Source == null ? Visibility.Visible : Visibility.Collapsed;
                MediaPlayGlyph.Text = playback.PlaybackStatus == PlaybackStatus.Playing ? "" : "";
                MediaPrevious.IsEnabled = playback.Controls.IsPreviousEnabled;
                MediaNext.IsEnabled = playback.Controls.IsNextEnabled;
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

    private static async Task<ImageSource?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
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
