using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using MediaManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace IslandBar;

internal sealed record NowPlayingTrack(string Title, string Artist, BitmapSource? Cover, string AppId);

/// <summary>
/// Spotify'da yeni şarkının başlamasını izler. Geçerli oturuma değil Spotify'ın oturumuna bağlanır:
/// arkada Chrome'da duran bir video "geçerli" sayılabiliyor.
/// </summary>
internal sealed class NowPlayingWatcher : IDisposable
{
    private const string SpotifyId = "spotify";

    /// <summary>Uygulama şarkı değişiminde birkaç olay üst üste gönderiyor, kapak başlıktan sonra gelebiliyor; durulunca okunur.</summary>
    private const int SettleMs = 350;

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(SettleMs) };
    private readonly List<MediaSession> _sessions = new();
    private MediaManager? _manager;
    private string? _lastTrack;
    private bool _isPrimed;
    private bool _isReading;
    private bool _readAgain;
    private bool _isDisposed;

    /// <summary>Yeni şarkı başladı.</summary>
    public event Action<NowPlayingTrack>? TrackStarted;

    /// <summary>Aynı şarkının bilgisi tazelendi: kapak geç gelmiş olabilir. Yalnızca <see cref="WantsUpdates"/> açıkken.</summary>
    public event Action<NowPlayingTrack>? TrackUpdated;

    public bool WantsUpdates { get; set; }

    public async void Start()
    {
        _settleTimer.Tick += SettleTimer_Tick;
        try
        {
            MediaManager manager = await MediaManager.RequestAsync();
            if (_isDisposed)
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
            // Windows medya bilgisi veremiyor: kart hiç çıkmaz.
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _settleTimer.Stop();
        if (_manager != null)
        {
            _manager.SessionsChanged -= Manager_SessionsChanged;
        }
        UnbindSessions();
    }

    private void Manager_SessionsChanged(MediaManager sender, SessionsChangedEventArgs args)
    {
        _ui.BeginInvoke(() =>
        {
            if (!_isDisposed)
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
        }
        _sessions.Clear();
    }

    private void Session_Changed<T>(MediaSession sender, T args)
    {
        _ui.BeginInvoke(RestartSettle);
    }

    private void RestartSettle()
    {
        _settleTimer.Stop();
        if (!_isDisposed)
        {
            _settleTimer.Start();
        }
    }

    private async void SettleTimer_Tick(object? sender, EventArgs e)
    {
        _settleTimer.Stop();
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        if (_isReading)
        {
            _readAgain = true;
            return;
        }

        _isReading = true;
        try
        {
            // Yalnızca çalan şarkı sayılır: duraklatılmışken değişen şarkı, çalmaya başlayınca bildirilir.
            foreach (MediaSession session in _sessions.ToList())
            {
                if (session.GetPlaybackInfo().PlaybackStatus != PlaybackStatus.Playing)
                {
                    continue;
                }

                GlobalSystemMediaTransportControlsSessionMediaProperties properties = await session.TryGetMediaPropertiesAsync();
                if (string.IsNullOrWhiteSpace(properties.Title))
                {
                    continue;
                }

                string key = $"{properties.Title}\n{properties.Artist}";
                bool isNew = key != _lastTrack;
                _lastTrack = key;
                if (isNew || WantsUpdates)
                {
                    BitmapSource? cover = await QuickSettingsPanel.ReadThumbnailAsync(properties);
                    var track = new NowPlayingTrack(properties.Title, properties.Artist, cover, session.SourceAppUserModelId);

                    // IslandBar açıldığında zaten çalan şarkı yeni değil.
                    if (isNew && _isPrimed)
                    {
                        TrackStarted?.Invoke(track);
                    }
                    else if (!isNew)
                    {
                        TrackUpdated?.Invoke(track);
                    }
                }
                break;
            }
        }
        catch (COMException)
        {
            // Spotify o an kapanıyor olabilir; oturum listesi değişince yeniden okunur.
        }
        finally
        {
            _isPrimed = true;
            _isReading = false;
            if (_readAgain)
            {
                _readAgain = false;
                RestartSettle();
            }
        }
    }
}
