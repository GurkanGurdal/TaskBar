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
/// açık kalır, uygulama yeniden başlayınca kapalı başlar. Spotify'a hiçbir şey gönderilmez: Windows'un medya bilgisi
/// yalnızca okunur (ad, sanatçı, albüm, süre, konum).
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
    private bool _isPlaying;

    /// <summary>İkinci ekranda tam ekran (kullanıcı seçti); ekran çıkarılırsa çubuktaki altyazıya döner.</summary>
    private bool _onSecondScreen;
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

    /// <summary>Ana ekrandaki adanın yeri; çubuklar hazır değilse null.</summary>
    public Func<LyricsPlace?>? PlaceOf { get; set; }

    /// <summary>Şerit adanın üstündeki şarkı kartının yerini tutuyor.</summary>
    public bool HoldsChipSpot => _band?.IsShown == true;

    /// <summary>Şerit açılmak üzere: şarkı kartı yer versin.</summary>
    public event Action? Showing;

    public bool IsOn { get; private set; }

    public event Action<bool>? Changed;

    /// <summary>İkinci ekran (ana ekran dışındaki ilki); yoksa null.</summary>
    public static System.Windows.Forms.Screen? SecondScreen()
    {
        return System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => !screen.Primary);
    }

    /// <summary>Kipi açar: ikinci ekranda tam ekran ya da çubukta altyazı.</summary>
    public void Start(bool onSecondScreen)
    {
        if (IsOn)
        {
            return;
        }

        _onSecondScreen = onSecondScreen && SecondScreen() != null;
        SetOn(true);
    }

    public async void SetOn(bool on)
    {
        if (on == IsOn)
        {
            return;
        }

        IsOn = on;
        Changed?.Invoke(on);
        if (!on)
        {
            Stop();
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
            await ReadAsync();
        }
        catch (COMException)
        {
            // Windows medya bilgisi veremiyor: şerit çıkmaz.
        }
    }

    /// <summary>Çubuk gizlenip açıldı (ana ekran).</summary>
    public void FollowBar(bool barHidden)
    {
        _band?.FollowBar(barHidden);
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
        _onSecondScreen = false;
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
            _position = timeline.Position;
            _positionAt = timeline.LastUpdatedTime;
            _isPlaying = session.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing;
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

        // İkinci ekran çıkarıldıysa çubuktaki altyazıya dönülür.
        if (_onSecondScreen && SecondScreen() is not { } second)
        {
            _onSecondScreen = false;
            _stage?.Close();
            _stage = null;
        }
        else if (_onSecondScreen)
        {
            TickStage(SecondScreen()!);
            return;
        }

        LyricsPlace? place = PlaceOf?.Invoke();
        bool wanted = place != null && !place.FullScreen && _playing != null && _trackKey != null && ConvertCard.Current == null
            && DateTime.UtcNow < _problemUntil;
        if (!wanted)
        {
            _band?.Conceal();
            return;
        }

        if (_band == null)
        {
            _band = new LyricsBand(place!.Island, place.Edge, place.BarHidden);
        }

        (string current, string next) = Lines();
        _band.SetLines(current, next);
        if (!_band.IsShown)
        {
            Showing?.Invoke();
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
            _stage = new LyricsStage(screen.Bounds, () => SetOn(false));
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
            view = new LyricsView($"{_trackKey}#{index}", text, NextText(lines, index + 1), length, _songTitle, _artist, _cover, _accent, null, _isPlaying);
        }
        _stage.Update(view);
    }

    /// <summary>O anki satırın sırası (girişte -1) ve süresi (sıradaki satıra kadar).</summary>
    private (int Index, TimeSpan Length) Current(IReadOnlyList<LyricLine> lines)
    {
        TimeSpan now = _position + Lead;
        if (_isPlaying)
        {
            now += DateTimeOffset.Now - _positionAt;
        }

        int index = -1;
        for (int i = 0; i < lines.Count && lines[i].At <= now; i++)
        {
            index = i;
        }

        TimeSpan start = index < 0 ? TimeSpan.Zero : lines[index].At;
        TimeSpan end = index + 1 < lines.Count ? lines[index + 1].At : start + TimeSpan.FromSeconds(4);
        return (index, end - start);
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
