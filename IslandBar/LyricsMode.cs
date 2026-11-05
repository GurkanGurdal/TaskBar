using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ManagedShell.AppBar;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace IslandBar;

/// <summary>Altyazının duracağı yer: ana ekrandaki ada.</summary>
internal sealed record LyricsPlace(System.Drawing.Rectangle Island, AppBarEdge Edge, bool BarHidden, bool FullScreen);

/// <summary>
/// Sözler kipi (Alt + S tekerleğindeki Sözler, kullanıcının seçimi): açıkken Spotify'da çalan şarkının zamanlı sözleri
/// adanın üstündeki altyazıda (<see cref="LyricsBand"/>), o anki satır ve sıradaki. Çeviri kipi gibi kapatılana kadar
/// açık kalır, uygulama yeniden başlayınca kapalı başlar. Windows'un medya bilgisi okunur (ad, sanatçı, albüm, süre,
/// konum); Spotify'a giden tek şey ikinci ekrandaki sahnenin düğmeleri (oynat / duraklat, atla, sar), Windows'un medya
/// denetimiyle (hızlı ayarlardaki medya kartı gibi).
/// Konum: Spotify konumu yalnızca çalma, duraklatma ve atlamada bildiriyor; arası bildirilen andan bu yana geçen süreyle
/// hesaplanır.
/// </summary>
internal sealed class LyricsMode
{
    private const string SpotifyId = "spotify";
    private const int TickMs = 100;
    private const int SettleMs = 350;

    /// <summary>Satır sözün söylendiği andan biraz önce gelsin: göz okuyana kadar ses yetişiyor.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(250);

    /// <summary>Bulunamadı, enstrümantal: kısa süre söylenir, sonra şerit sıradaki şarkıya kadar çekilir.</summary>
    private static readonly TimeSpan ProblemShown = TimeSpan.FromSeconds(4);

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _settle;
    private MediaManager? _manager;
    private readonly List<MediaSession> _sessions = [];
    private MediaSession? _playing;
    private Pulse? _pulse;
    private LyricsBand? _band;
    private CancellationTokenSource? _fetch;
    private string? _trackKey;
    private string _title = "";
    private Lyrics? _lyrics;
    private DateTime _problemUntil;
    private TimeSpan _position;
    private DateTimeOffset _positionAt;
    private TimeSpan _start;
    private TimeSpan _duration;
    private bool _canSeek;
    private bool _isPlaying;
    private string? _retryFor;
    private int _retries;
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tam ekran sahnenin ekranı (ad). Kip hep tam ekran açılır, ana ekranda da (kullanıcının isteği); altyazı şeridi
    /// yalnızca küçültülünce. Ekran çıkarılırsa sahne ana ekrana geçer.
    /// </summary>
    private string? _stageScreen;

    /// <summary>Şeridin durduğu ekran (null: ana ekran); küçültülmüş sahne şeridini kendi ekranındaki adaya koyar.</summary>
    private string? _bandScreen;

    private bool BandOnPrimary => _bandScreen == null
        || System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _bandScreen)?.Primary != false;

    /// <summary>Sahne küçültüldü (kullanıcının tasarımı, seçeneklerden A · Çubuktaki altyazı): sözler şeritte, şeridin tam
    /// ekran düğmesi sahneyi geri açar.</summary>
    private bool _minimized;
    private LyricsStage? _stage;
    private string _songTitle = "";
    private string _artist = "";
    private System.Windows.Media.Imaging.BitmapSource? _cover;
    private System.Windows.Media.Color _accent = ThemeManager.AccentColor;

    private LyricsMode()
    {
        _settle = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(SettleMs) };
        _settle.Tick += async (_, _) =>
        {
            _settle.Stop();
            await ReadAsync();
        };
    }

    public static LyricsMode Shared { get; } = new();

    /// <summary>İkinci ekrandaki çerçeve (ayardan).</summary>
    public Func<int>? FrameStyleOf { get; set; }

    /// <summary>Ayarda çerçeve değişti: açık sahne hemen geçer.</summary>
    public void ApplyFrame()
    {
        _stage?.SetFrame(FrameStyleOf?.Invoke() ?? LyricsStage.FrameChasers);
    }

    /// <summary>Verilen ekrandaki (null: ana ekran) adanın yeri; çubuk hazır değilse null.</summary>
    public Func<string?, LyricsPlace?>? PlaceOf { get; set; }

    /// <summary>Şerit ana ekranda adanın üstündeki şarkı kartının yerini tutuyor (kartlar yalnızca ana ekranda).</summary>
    public bool HoldsChipSpot => _band?.IsShown == true && BandOnPrimary;

    /// <summary>Şerit açılmak üzere: şarkı kartı yer versin.</summary>
    public event Action? Showing;

    public bool IsOn { get; private set; }

    public event Action<bool>? Changed;

    /// <summary>İkinci ekran (ana ekran dışındaki ilki); yoksa null.</summary>
    public static System.Windows.Forms.Screen? SecondScreen()
    {
        return System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => !screen.Primary);
    }

    /// <summary>Kipi açar: verilen ekranda tam ekran sahne.</summary>
    public void Start(System.Windows.Forms.Screen stageScreen)
    {
        if (IsOn)
        {
            return;
        }

        _stageScreen = stageScreen.DeviceName;
        SetOn(true);
    }

    private System.Windows.Forms.Screen? StageScreen()
    {
        return _stageScreen == null ? null : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == _stageScreen);
    }

    /// <param name="controlSpotify">
    /// Kullanıcı açıp kapatınca Spotify da çalar / durur (kullanıcının isteği: açılınca seçili şarkı başlasın, kapatınca
    /// dursun). Uygulama kapanırken false: çubuğu yeniden başlatmak müziği durdurmasın.
    /// </param>
    public async void SetOn(bool on, bool controlSpotify = true)
    {
        if (on == IsOn)
        {
            return;
        }

        IsOn = on;
        Changed?.Invoke(on);
        if (!on)
        {
            MediaSession? playing = controlSpotify ? SessionIn(PlaybackStatus.Playing) : null;
            Stop();
            if (playing != null)
            {
                await Send(playing, session => session.TryPauseAsync().AsTask());
            }
            return;
        }

        _pulse = new Pulse(_dispatcher, TickMs, Tick);
        try
        {
            MediaManager manager = await MediaManager.RequestAsync();
            if (!IsOn)
            {
                return;
            }

            _manager = manager;
            _manager.SessionsChanged += Manager_SessionsChanged;
            BindSessions();

            // Çalan Spotify yoksa seçili (duraklatılmış) şarkı başlar.
            if (controlSpotify && SessionIn(PlaybackStatus.Playing) == null && SessionIn(PlaybackStatus.Paused) is { } paused)
            {
                await Send(paused, session => session.TryPlayAsync().AsTask());
            }
            await ReadAsync();
        }
        catch (COMException)
        {
            // Windows medya bilgisi veremiyor: şerit çıkmaz.
        }
    }

    /// <summary>Bir ekrandaki çubuk gizlenip açıldı: şerit o ekrandaysa onu izler.</summary>
    public void FollowBar(string screen, bool primary, bool barHidden)
    {
        if (primary ? BandOnPrimary : _bandScreen == screen)
        {
            _band?.FollowBar(barHidden);
        }
    }

    private void Stop()
    {
        _pulse?.Dispose();
        _pulse = null;
        _settle.Stop();
        _fetch?.Cancel();
        if (_manager != null)
        {
            _manager.SessionsChanged -= Manager_SessionsChanged;
            _manager = null;
        }
        UnbindSessions();
        _playing = null;
        _trackKey = null;
        _lyrics = null;
        _band?.Close();
        _band = null;
        _stage?.Close();
        _stage = null;
        _stageScreen = null;
        _bandScreen = null;
        _minimized = false;
    }

    /// <summary>Sahne kapanır, sözler çubuktaki altyazıya iner (ikinci ekrandaki pencereler kullanılabilsin).</summary>
    private void Minimize()
    {
        if (_stageScreen == null || _minimized)
        {
            return;
        }

        _minimized = true;
        _stage?.Close();
        _stage = null;
        Tick();
    }

    /// <summary>Şeridin tam ekran düğmesi: sahne ikinci ekranda yeniden açılır.</summary>
    private void Expand()
    {
        if (!_minimized)
        {
            return;
        }

        _minimized = false;
        Tick();
    }

    private void Manager_SessionsChanged(MediaManager sender, SessionsChangedEventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (IsOn)
            {
                BindSessions();
                RestartSettle();
            }
        });
    }

    private void BindSessions()
    {
        UnbindSessions();
        if (_manager == null)
        {
            return;
        }

        foreach (MediaSession session in _manager.GetSessions())
        {
            if (session.SourceAppUserModelId.Contains(SpotifyId, StringComparison.OrdinalIgnoreCase))
            {
                session.MediaPropertiesChanged += Session_Changed;
                session.PlaybackInfoChanged += Session_Changed;
                session.TimelinePropertiesChanged += Session_Changed;
                _sessions.Add(session);
            }
        }
    }

    private void UnbindSessions()
    {
        foreach (MediaSession session in _sessions)
        {
            session.MediaPropertiesChanged -= Session_Changed;
            session.PlaybackInfoChanged -= Session_Changed;
            session.TimelinePropertiesChanged -= Session_Changed;
        }
        _sessions.Clear();
    }

    private void Session_Changed<T>(MediaSession sender, T args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Konum hemen; şarkının kendisi durulunca (değişimde birkaç olay üst üste geliyor).
            ReadTimeline(sender);
            RestartSettle();
        });
    }

    private void RestartSettle()
    {
        _settle.Stop();
        if (IsOn)
        {
            _settle.Start();
        }
    }

    private void ReadTimeline(MediaSession session)
    {
        if (session != _playing)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = session.GetPlaybackInfo();
            _start = timeline.StartTime;
            _duration = timeline.EndTime - timeline.StartTime;
            _position = timeline.Position - timeline.StartTime;
            _positionAt = timeline.LastUpdatedTime;
            _isPlaying = playback.PlaybackStatus == PlaybackStatus.Playing;
            _canSeek = playback.Controls.IsPlaybackPositionEnabled && _duration > TimeSpan.Zero;
        }
        catch (COMException)
        {
            // Spotify kapanıyor.
        }
    }

    /// <summary>Çalan (yoksa duraklatılmış) Spotify oturumu; şarkı değiştiyse sözleri istenir.</summary>
    private async Task ReadAsync()
    {
        try
        {
            MediaSession? session = _sessions.FirstOrDefault(candidate => candidate.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing)
                ?? _sessions.FirstOrDefault(candidate => candidate.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Paused);
            _playing = session;
            if (session == null)
            {
                _trackKey = null;
                return;
            }

            ReadTimeline(session);
            GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
            if (!IsOn || string.IsNullOrWhiteSpace(properties.Title))
            {
                return;
            }

            string key = $"{properties.Title}\n{properties.Artist}";
            if (key == _trackKey)
            {
                return;
            }

            _trackKey = key;
            _title = string.IsNullOrWhiteSpace(properties.Artist) ? properties.Title : $"{properties.Title} · {properties.Artist}";
            _songTitle = properties.Title;
            _artist = properties.Artist;
            _lyrics = null;
            _cover = await QuickSettingsPanel.ReadThumbnailAsync(properties);
            _accent = _cover != null ? QuickSettingsPanel.CoverAccent(_cover) : ThemeManager.AccentColor;
            if (key != _trackKey)
            {
                return;
            }
            _problemUntil = DateTime.MaxValue;
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            TimeSpan duration = timeline.EndTime - timeline.StartTime;

            _fetch?.Cancel();
            var fetch = new CancellationTokenSource();
            _fetch = fetch;
            Lyrics lyrics = await LyricsSource.FindAsync(properties.Title, properties.Artist, properties.AlbumTitle, duration, fetch.Token);
            if (fetch.IsCancellationRequested || key != _trackKey)
            {
                return;
            }

            _lyrics = lyrics;
            _problemUntil = lyrics.Problem != null ? DateTime.UtcNow + ProblemShown : DateTime.MaxValue;

            // Bağlantı aksadıysa şarkı değişmeden birkaç kez yeniden denenir (ilk istekte bir kez "ulaşılamadı" görüldü).
            if (key != _retryFor)
            {
                _retryFor = key;
                _retries = 0;
            }
            if (lyrics.Transient && _retries < MaxRetries)
            {
                _retries++;
                await Task.Delay(RetryDelay);
                if (IsOn && key == _trackKey)
                {
                    _trackKey = null;
                    RestartSettle();
                }
            }
        }
        catch (Exception e) when (e is COMException or OperationCanceledException)
        {
            // Spotify kapanıyor ya da şarkı değişti.
        }
    }

    /// <summary>Satırı hesaplar, şeridin görünüp görünmeyeceğine karar verir.</summary>
    private void Tick()
    {
        if (!IsOn)
        {
            return;
        }

        // Sahnenin ekranı çıkarıldıysa sahne ana ekrana geçer.
        System.Windows.Forms.Screen? stageScreen = StageScreen();
        if (_stageScreen != null && stageScreen == null && System.Windows.Forms.Screen.PrimaryScreen is { } primary)
        {
            _stageScreen = primary.DeviceName;
            stageScreen = primary;
            _stage?.Close();
            _stage = null;
        }
        if (stageScreen != null && !_minimized)
        {
            _band?.Conceal();
            TickStage(stageScreen);
            return;
        }

        // Küçültülmüş sahnenin şeridi kendi ekranındaki adada (kullanıcının isteği), hep görünür (tam ekran düğmesi
        // kaybolmasın), çalan yoksa da. Şerit başka ekrandaysa yeniden kurulur.
        string? bandScreen = _minimized ? _stageScreen : null;
        if (_band != null && bandScreen != _bandScreen)
        {
            _band.Close();
            _band = null;
        }
        _bandScreen = bandScreen;
        LyricsPlace? place = PlaceOf?.Invoke(bandScreen);
        bool wanted = place != null && !place.FullScreen && ConvertCard.Current == null
            && (_minimized || _playing != null && _trackKey != null && DateTime.UtcNow < _problemUntil);
        if (!wanted)
        {
            _band?.Conceal();
            return;
        }

        if (_band == null)
        {
            _band = new LyricsBand(place!.Island, place.Edge, place.BarHidden);
        }

        (string current, string next) = _minimized && (_playing == null || _trackKey == null)
            ? ("Sözler", "Spotify'da bir şarkı çalınca burada")
            : Lines();
        _band.SetLines(current, next);
        _band.SetButtons(_minimized ? Expand : null, _minimized ? () => SetOn(false) : null);
        _band.TrackPointer();
        if (!_band.IsShown)
        {
            if (BandOnPrimary)
            {
                Showing?.Invoke();
            }
            _band.Present();
        }
    }

    /// <summary>
    /// İkinci ekrandaki sahne: şarkı çalmıyorsa da açık kalır (kendi ekranı), bekleme notuyla. Girişte ve sözü
    /// olmayan şarkıda posterde şarkının adı.
    /// </summary>
    private void TickStage(System.Windows.Forms.Screen screen)
    {
        if (_stage == null)
        {
            _stage = new LyricsStage(screen.Bounds, FrameStyleOf?.Invoke() ?? LyricsStage.FrameChasers,
                new LyricsCommands(PlayPause, Previous, Next, Seek, () => SetOn(false), Minimize));
            _stage.Show();
        }

        LyricsView view;
        if (_playing == null || _trackKey == null)
        {
            view = new LyricsView("bekle", "", "", TimeSpan.Zero, "", "", null, ThemeManager.AccentColor, "Spotify'da bir şarkı çalınca sözleri burada", false);
        }
        else if (_lyrics == null || _lyrics.Problem != null)
        {
            view = new LyricsView($"{_trackKey}#ad", _songTitle, "", TimeSpan.FromSeconds(3), _songTitle, _artist, _cover, _accent,
                _lyrics?.Problem ?? "Sözler aranıyor…", _isPlaying);
        }
        else
        {
            (int index, TimeSpan length) = Current(_lyrics.Lines);
            IReadOnlyList<LyricLine> lines = _lyrics.Lines;
            string text = index < 0 ? _songTitle : lines[index].Text.Length > 0 ? lines[index].Text : "• • •";
            view = new LyricsView($"{_trackKey}#{index}", text, NextText(lines, index + 1), length, _songTitle, _artist, _cover, _accent, null, _isPlaying)
            {
                Lines = lines
            };
        }

        if (_playing != null && _trackKey != null)
        {
            view = view with { Song = _trackKey, Position = Position(), Duration = _duration, CanSeek = _canSeek };
        }
        _stage.Update(view);
    }

    /// <summary>O anki satırın sırası (girişte -1) ve süresi (sıradaki satıra kadar).</summary>
    private (int Index, TimeSpan Length) Current(IReadOnlyList<LyricLine> lines)
    {
        TimeSpan now = Position() + Lead;

        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].At <= now; i++)
        {
            index = i;
        }

        TimeSpan start = index < 0 ? TimeSpan.Zero : lines[index].At;
        TimeSpan end = index + 1 < lines.Count ? lines[index + 1].At : start + TimeSpan.FromSeconds(4);
        return (index, end - start);
    }

    /// <summary>Şarkıdaki konum: bildirilen konum ve çalıyorsa o andan bu yana geçen süre.</summary>
    private TimeSpan Position()
    {
        TimeSpan now = _position;
        if (_isPlaying)
        {
            now += DateTimeOffset.Now - _positionAt;
        }
        return _duration > TimeSpan.Zero && now > _duration ? _duration : now;
    }

    private async void PlayPause()
    {
        await Command(session => session.TryTogglePlayPauseAsync().AsTask());
    }

    private async void Previous()
    {
        await Command(session => session.TrySkipPreviousAsync().AsTask());
    }

    private async void Next()
    {
        await Command(session => session.TrySkipNextAsync().AsTask());
    }

    /// <summary>Konum hemen burada da değişir: Spotify yeni konumu bildirene kadar satır eskisinde kalmasın.</summary>
    private async void Seek(TimeSpan target)
    {
        if (!_canSeek)
        {
            return;
        }

        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > _duration ? _duration : target;
        _position = target;
        _positionAt = DateTimeOffset.Now;
        await Command(session => session.TryChangePlaybackPositionAsync((_start + target).Ticks).AsTask());
    }

    private async Task Command(Func<MediaSession, Task<bool>> send)
    {
        if (_playing is { } session)
        {
            await Send(session, send);
        }
    }

    private static async Task Send(MediaSession session, Func<MediaSession, Task<bool>> send)
    {
        try
        {
            await send(session);
        }
        catch (COMException)
        {
            // Spotify kapanıyor.
        }
    }

    private MediaSession? SessionIn(PlaybackStatus status)
    {
        try
        {
            return _sessions.FirstOrDefault(session => session.GetPlaybackInfo().PlaybackStatus == status);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private (string Current, string Next) Lines()
    {
        if (_lyrics == null)
        {
            return (_title, "Sözler aranıyor…");
        }
        if (_lyrics.Problem != null)
        {
            return (_title, _lyrics.Problem);
        }

        IReadOnlyList<LyricLine> lines = _lyrics.Lines;
        int index = Current(lines).Index;

        // Girişte şarkının adı, altında ilk satır; satır arasındaki boş satır ara demek.
        string current = index < 0 ? _title : lines[index].Text.Length > 0 ? lines[index].Text : "♪";
        string next = NextText(lines, index + 1);
        return (current, next);
    }

    private static string NextText(IReadOnlyList<LyricLine> lines, int from)
    {
        for (int i = from; i < lines.Count; i++)
        {
            if (lines[i].Text.Length > 0)
            {
                return lines[i].Text;
            }
        }
        return "";
    }
}
