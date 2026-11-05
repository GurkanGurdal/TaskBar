using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;

namespace IslandBar;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private readonly ShellManager _shellManager;
    private readonly Mutex _instanceMutex;
    private readonly EventWaitHandle _showSettingsSignal;
    private readonly List<BarWindow> _bars = new();
    private WinKeyWatcher? _winKeyWatcher;
    private VolumeKeyWatcher? _volumeKeyWatcher;
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppSettings? _settings;
    private SettingsWindow? _settingsWindow;
    private string _screenLayout = "";
    private TrayIcon? _trayIcon;
    private bool _shellReleased;

    public App()
    {
        // Claude Code kancası: olayı çalışan çubuğa iletip hemen çıkar, Claude'u bekletmez.
        if (ClaudeHook.RunIfRequested())
        {
            Environment.Exit(0);
        }

        if (TaskbarRecovery.RunWatchdogIfRequested())
        {
            Environment.Exit(0);
        }

        // Terminal ya da ajan bir işin içinde başlattıysa işin dışında yeniden başla: iş topluca kapanınca çubuk ve bekçi
        // birlikte, iz bırakmadan ölüyordu.
        if (JobEscape.RelaunchOutside())
        {
            Environment.Exit(0);
        }

        // ShellManager Windows tarafında kayıt yapıyor; ikinci kopya ona hiç ulaşmamalı.
        _instanceMutex = new Mutex(true, TaskbarRecovery.InstanceMutexName, out bool isFirstInstance);
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\IslandBar.ShowSettings");
        if (!isFirstInstance)
        {
            _showSettingsSignal.Set();
            Environment.Exit(0);
        }

        // Görev çubuğu kaydı da eski klasörde olabilir; okunmadan önce taşınmalı.
        LegacyData.Migrate();
        TaskbarRecovery.ClearRestartRequest();

        // Önceki çalışma temizlik yapamadan öldüyse önce onu düzelt, sonra gerçek durumu kaydet.
        TaskbarRecovery.RestoreIfRecorded();
        TaskbarRecovery.RecordState();

        ShellConfig config = ShellManager.DefaultShellConfig;
        config.TaskIconSize = IconSize.Large;

        _shellManager = new ShellManager(config);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    public void ExitGracefully()
    {
        // Bu sinyal olmadan pencere kapanırken ABM_REMOVE gönderilmez.
        _shellManager.AppBarManager.SignalGracefulShutdown();
        Shutdown();
    }

    public void ApplyTaskbarMode(bool hideWindowsTaskbar)
    {
        _shellManager.ExplorerHelper.HideExplorerTaskbar = hideWindowsTaskbar;

        if (hideWindowsTaskbar)
        {
            WarnIfTaskbarStaysVisible();
        }
        else
        {
            TaskbarRecovery.RaiseWindowsTaskbar();
        }
    }

    private void WarnIfTaskbarStaysVisible()
    {
        // ExplorerHelper çubuğu sürekli gizliyor; birkaç saniye sonra hâlâ görünüyorsa gizleme çalışmıyordur.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            IntPtr windowsTray = WindowHelper.FindWindowsTray(_shellManager.NotificationArea.Handle);
            if (_shellManager.ExplorerHelper.HideExplorerTaskbar && windowsTray != IntPtr.Zero && NativeMethods.IsWindowVisible(windowsTray))
            {
                MessageBox.Show("Windows görev çubuğu gizlenemedi. Windows Ayarları > Kişiselleştirme > Görev çubuğu bölümünden " +
                    "\"Görev çubuğunu otomatik olarak gizle\" seçeneğini açabilirsiniz.",
                    "IslandBar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        };
        timer.Start();
    }

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        // Ekran değişince tüm çubuklar kapatılıp yeniden açılıyor; bu sırada uygulama kapanmamalı.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        UiStallWatch.Attach(Dispatcher);
        FrameWatch.StartIfRequested(Dispatcher, e.Args);
        GlassCard.AttachToPopups();
        _settings = AppSettings.Load();
        ThemeManager.Apply(_settings.Theme);
        _settings.PropertyChanged += Settings_PropertyChanged;
        ApplyTaskbarMode(_settings.HideWindowsTaskbar);

        ScreenshotHistory.Shared.SetWatching(_settings.KeepScreenshots);
        ClipboardHistory.Shared.SetWatching(_settings.KeepCopiedText);

        OpenBars();

        ApplyStartMenuMode();
        ApplyVolumeChipMode();

        _displayChangeTimer.Tick += DisplayChangeTimer_Tick;

        _trayIcon = new TrayIcon(OpenSettings, ExitGracefully);

        ThreadPool.RegisterWaitForSingleObject(_showSettingsSignal,
            (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);

        ClaudeHook.Listen(note => Dispatcher.BeginInvoke(() => ShowClaudeNote(note)));
        LyricsMode.Shared.PlaceOf = screen => _bars.FirstOrDefault(bar => screen == null ? bar.Screen.Primary : bar.Screen.DeviceName == screen) is { } bar
            && bar.TryGetIslandRestRect(out System.Drawing.Rectangle island)
            ? new LyricsPlace(island, bar.AppBarEdge, bar.IsIslandHidden, bar.IsFullScreenHere)
            : null;
        LyricsMode.Shared.Showing += () => _bars.FirstOrDefault(bar => bar.Screen.Primary)?.YieldChipSpot();
        LyricsMode.Shared.FrameStyleOf = () => _settings.LyricsFrame;
        FocusTimer.Shared.Finished += FocusTimer_Finished;
        ReminderStore.Shared.Due += Reminder_Due;
        ReminderStore.Shared.Start();
        _toolsKeyWatcher = new ToolsKeyWatcher(
            () => Dispatcher.BeginInvoke(OpenToolsRing),
            () => Dispatcher.BeginInvoke(() => _toolsRing?.Release()));

        TaskbarRecovery.StartWatchdog();

        StartupRegistration.RepairApproval();
#if !DEBUG
        // Testte Debug derlemesi kısayolu kendine çevirmesin.
        StartupRegistration.EnsureStartMenuShortcut();

        // Uygulama taşındıysa kanca yeni yeri göstersin; Debug derlemesi kullanıcının kancasını kendine çevirmesin.
        ClaudeHookSetup.Sync(_settings.ShowClaudeChip);
#endif
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.HideWindowsTaskbar))
        {
            ApplyTaskbarMode(_settings!.HideWindowsTaskbar);
            ApplyWinKeyMode();
        }
        else if (e.PropertyName == nameof(AppSettings.KeepScreenshots))
        {
            ScreenshotHistory.Shared.SetWatching(_settings!.KeepScreenshots);
        }
        else if (e.PropertyName == nameof(AppSettings.KeepCopiedText))
        {
            ClipboardHistory.Shared.SetWatching(_settings!.KeepCopiedText);
        }
        else if (e.PropertyName == nameof(AppSettings.CustomStartMenu))
        {
            ApplyStartMenuMode();
        }
        else if (e.PropertyName == nameof(AppSettings.ShowVolumeChip))
        {
            ApplyVolumeChipMode();
        }
        else if (e.PropertyName == nameof(AppSettings.LyricsFrame))
        {
            LyricsMode.Shared.ApplyFrame();
        }
        else if (e.PropertyName == nameof(AppSettings.ShowClaudeChip))
        {
#if !DEBUG
            ClaudeHookSetup.Sync(_settings!.ShowClaudeChip);
#endif
        }
        else if (e.PropertyName == nameof(AppSettings.Theme))
        {
            Dispatcher.BeginInvoke(() =>
            {
                CloseBars();
                ThemeManager.Apply(_settings!.Theme);
                OpenBars();
            }, DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(AppSettings.AutoHideBar))
        {
            // Saydamlık pencere açıldıktan sonra değişmiyor; çubuklar yeniden açılmalı. Değişiklik menüden
            // de gelebilir, o menünün kapanması bitsin.
            Dispatcher.BeginInvoke(() =>
            {
                CloseBars();
                OpenBars();
            }, DispatcherPriority.Background);
        }

        _settings!.Save();
    }

    private void ApplyStartMenuMode()
    {
        ApplyWinKeyMode();
        if (_settings!.CustomStartMenu)
        {
            PrepareStartPanels();
        }
    }

    /// <summary>
    /// Windows tuşu kendi menümüzü açar; Windows çubuğu gizliyken Win+rakam da bizim çubuğun düğmelerine gider.
    /// Tamamlayıcı modda rakamlar görünür Windows çubuğunun.
    /// </summary>
    private void ApplyWinKeyMode()
    {
        _winKeyWatcher?.Dispose();
        _winKeyWatcher = null;

        bool start = _settings!.CustomStartMenu;
        bool numbers = _settings.HideWindowsTaskbar;
        if (start || numbers)
        {
            _winKeyWatcher = new WinKeyWatcher(
                start ? () => Dispatcher.BeginInvoke(ToggleStartPanel) : null,
                numbers ? (index, newInstance) => Dispatcher.BeginInvoke(() => PressTaskAt(index, newInstance)) : null);
        }
    }

    /// <summary>Sıra ana ekrandaki çubuğun düğmeleri: sabitlenenler her çubukta aynı, Windows da ana çubuğu sayar.</summary>
    private void PressTaskAt(int index, bool newInstance)
    {
        _bars.FirstOrDefault(bar => bar.Screen.Primary)?.PressTaskAt(index, newInstance);
    }

    private void ApplyVolumeChipMode()
    {
        _volumeKeyWatcher?.Dispose();
        _volumeKeyWatcher = null;

        if (_settings!.ShowVolumeChip)
        {
            _volumeKeyWatcher = new VolumeKeyWatcher(key => Dispatcher.BeginInvoke(() => VolumeKey_Pressed(key)));
        }
    }

    private void VolumeKey_Pressed(VolumeKey key)
    {
        try
        {
            if (AudioEndpoint.TryOpen() is { } audio)
            {
                bool changed = audio.Press(key);
                _bars.FirstOrDefault(bar => bar.Screen.Primary)?.ShowVolume(audio, changed);
            }
        }
        catch (COMException)
        {
            // Aygıt tam bu sırada çıkarılmış olabilir; sonraki basışta varsayılan yeniden bulunur.
        }
    }

    private ToolsKeyWatcher? _toolsKeyWatcher;
    private ToolsRing? _toolsRing;
    private ToolCardWindow? _toolCard;

    /// <summary>Alt + S: imlecin olduğu yerde araç tekerleği; açık tekerlek ya da kart varsa önce o kapanır.</summary>
    private void OpenToolsRing()
    {
        _toolsRing?.Dismiss();
        _toolCard?.Dismiss();
        var ring = new ToolsRing(System.Windows.Forms.Cursor.Position);
        ring.Chosen += OpenToolCard;
        ring.FilesDropped += files => OpenConvertCard(files);
        ring.Closed += (_, _) =>
        {
            if (_toolsRing == ring)
            {
                _toolsRing = null;
            }
        };
        _toolsRing = ring;
        ring.Show();
        ring.Activate();
    }

    private void OpenToolCard(ToolKind kind, System.Drawing.Point center)
    {
        _toolCard?.Dismiss();
        if (kind == ToolKind.Translate)
        {
            // Kartı yok: kip açılır ya da kapanır.
            TranslateMode.Shared.Toggle();
            return;
        }
        if (kind == ToolKind.Lyrics)
        {
            // Kartı yok: kip açılır ya da kapanır, hep tam ekran. Tekerlek ikinci ekrandaysa sahne sormadan orada açılır;
            // ana ekrandaysa ve ikinci ekran varsa nerede açılacağı sorulur ("Bir daha sorma" hatırlanır; kullanıcının
            // seçimleri), yoksa ana ekranda.
            System.Windows.Forms.Screen ringScreen = System.Windows.Forms.Screen.FromPoint(center);
            if (LyricsMode.Shared.IsOn)
            {
                LyricsMode.Shared.SetOn(false);
            }
            else if (!ringScreen.Primary)
            {
                LyricsMode.Shared.Start(ringScreen);
            }
            else if (LyricsMode.SecondScreen() == null || _settings!.LyricsScreen == 2)
            {
                LyricsMode.Shared.Start(ringScreen);
            }
            else if (_settings.LyricsScreen == 1)
            {
                LyricsMode.Shared.Start(LyricsMode.SecondScreen()!);
            }
            else
            {
                new LyricsPrompt(center, (second, remember) =>
                {
                    if (remember)
                    {
                        _settings.LyricsScreen = second ? 1 : 2;
                    }
                    LyricsMode.Shared.Start(second && LyricsMode.SecondScreen() is { } other ? other : ringScreen);
                }).Show();
            }
            return;
        }
        if (kind == ToolKind.Convert)
        {
            // Kartı adanın üstünde; açıksa kapanır.
            if (ConvertCard.Current is { } open)
            {
                open.Dismiss();
            }
            else
            {
                OpenConvertCard([]);
            }
            return;
        }

        var card = new ToolCardWindow(kind, center);
        card.Closed += (_, _) =>
        {
            if (_toolCard == card)
            {
                _toolCard = null;
            }
        };
        _toolCard = card;
        card.Show();
    }

    /// <summary>Dönüştür kartı ana ekrandaki adanın üstünde; açıksa dosyalar ona eklenir.</summary>
    private void OpenConvertCard(string[] files)
    {
        if (ConvertCard.Current is not { } card)
        {
            if (_bars.FirstOrDefault(bar => bar.Screen.Primary) is not { } bar || !bar.TryGetIslandRestRect(out System.Drawing.Rectangle island))
            {
                return;
            }

            bar.YieldChipSpot();
            card = new ConvertCard(island, bar.AppBarEdge, bar.IsIslandHidden, bar.IsClockChipShown);
            card.Show();
        }
        card.AddFiles(files);
    }

    private const int TimerNoticeSeconds = 30;
    private const int NextReminderDelayMs = 700;
    private static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(10);
    private readonly Queue<Reminder> _dueReminders = new();
    private Reminder? _shownReminder;

    /// <summary>Zamanı gelen hatırlatıcı sıraya girer; aynı anda tek kart, kullanıcı yanıtlayınca sıradaki.</summary>
    private void Reminder_Due(Reminder reminder)
    {
        _dueReminders.Enqueue(reminder);
        if (_shownReminder == null)
        {
            ShowNextReminder();
        }
    }

    private void ShowNextReminder()
    {
        if (!_dueReminders.TryDequeue(out Reminder? reminder) || _bars.FirstOrDefault(bar => bar.Screen.Primary) is not { } bar)
        {
            _shownReminder = null;
            return;
        }

        _shownReminder = reminder;
        System.Media.SystemSounds.Asterisk.Play();
        bar.ShowReminder(reminder, done =>
        {
            if (done)
            {
                ReminderStore.Shared.Remove(reminder);
            }
            else
            {
                ReminderStore.Shared.Snooze(reminder, SnoozeDelay);
            }

            // Kart önce kapansın; sıradaki hemen açılırsa kapanış onu da götürürdü.
            var next = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(NextReminderDelayMs) };
            next.Tick += (_, _) =>
            {
                next.Stop();
                ShowNextReminder();
            };
            next.Start();
        });
    }

    /// <summary>Süre doldu: ana ekrandaki çubukta kart, kısa bir Windows sesiyle.</summary>
    private void FocusTimer_Finished(TimeSpan total)
    {
        System.Media.SystemSounds.Exclamation.Play();
        string length = total.TotalHours >= 1 ? $"{total.TotalHours:0.#} saat" : $"{Math.Round(total.TotalMinutes)} dk";
        _bars.FirstOrDefault(bar => bar.Screen.Primary)?.ShowNotice("", "Süre doldu", $"{length} zamanlayıcı", TimerNoticeSeconds);
    }

    /// <summary>Kart Claude'un penceresinin bulunduğu ekranda çıkar; pencere yoksa (terminalde) ana ekranda.</summary>
    private void ShowClaudeNote(ClaudeNote note)
    {
        (_bars.FirstOrDefault(bar => bar.ShowsClaudeWindow) ?? _bars.FirstOrDefault(bar => bar.Screen.Primary))?.ShowClaude(note);
    }

    /// <summary>Windows tuşu ilk seferde beklemesin; açılışı yavaşlatmamak için boşta yapılır.</summary>
    private async void PrepareStartPanels()
    {
        await StartMenuPanel.PreloadAsync(_settings!);

        foreach (BarWindow bar in _bars.ToList())
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await bar.WarmStartPanelAsync();
        }
    }

    /// <summary>Açık olan varsa kapanır; yoksa imlecin bulunduğu ekrandaki çubukta açılır.</summary>
    private void ToggleStartPanel()
    {
        if (_bars.FirstOrDefault(bar => bar.IsStartPanelOpen) is { } open)
        {
            open.ToggleStartPanel();
            return;
        }

        System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        (_bars.FirstOrDefault(bar => bar.Screen.Bounds == screen) ?? _bars.FirstOrDefault())?.ToggleStartPanel();
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_bars[0], _settings!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    #region Bars
    private void OpenBars()
    {
        // Ana ekran önce: ayarlar penceresi ilk çubuğu kullanıyor.
        List<AppBarScreen> screens = AppBarScreen.FromAllScreens().OrderByDescending(screen => screen.Primary).ToList();
        _screenLayout = LayoutOf(screens);

        foreach (AppBarScreen screen in screens)
        {
            var bar = new BarWindow(_shellManager, _settings!, screen);
            bar.Show();
            _bars.Add(bar);
        }

        // Ekran düzeni değişti ya da tema değişti: ekrandaki hatırlatıcı kartı eski çubukla gitti, yeniden çıksın.
        if (_shownReminder is { } shown)
        {
            _shownReminder = null;
            Reminder_Due(shown);
        }
    }

    private void CloseBars()
    {
        foreach (BarWindow bar in _bars)
        {
            bar.AllowClose = true;
            bar.Close();
        }
        _bars.Clear();
    }

    public void NotifyDisplayChange()
    {
        // Ekran değişimi art arda birkaç bildirim üretiyor; durulmasını bekle.
        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    private void DisplayChangeTimer_Tick(object? sender, EventArgs e)
    {
        _displayChangeTimer.Stop();

        if (LayoutOf(AppBarScreen.FromAllScreens()) != _screenLayout)
        {
            CloseBars();
            OpenBars();
        }
    }

    private static string LayoutOf(IEnumerable<AppBarScreen> screens)
    {
        return string.Join(";", screens.OrderBy(screen => screen.DeviceName)
            .Select(screen => $"{screen.DeviceName},{screen.Primary},{screen.Bounds},{screen.HMonitor}"));
    }
    #endregion

    private void App_OnExit(object sender, ExitEventArgs e)
    {
        ReleaseShell();
    }

    private void ReleaseShell()
    {
        if (_shellReleased)
        {
            return;
        }
        _shellReleased = true;

        _trayIcon?.Dispose();
        _toolsKeyWatcher?.Dispose();
        TranslateMode.Shared.SetOn(false);
        LyricsMode.Shared.SetOn(false, controlSpotify: false);
        ScreenshotHistory.Shared.StopWatching();

        // Tepsi servisi kapanmadan: Windows çubuğuna giden mesajlar onun üzerinden geçiyor.
        _shellManager.ExplorerHelper.HideExplorerTaskbar = false;
        _shellManager.Dispose();

        TaskbarRecovery.RaiseWindowsTaskbar();
        TaskbarRecovery.ClearRecord();
    }

    private void App_OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Otomatik gizleme ayarı kalıcı; sonraki oturuma değişmiş kalmasın.
        _shellManager.AppBarManager.SignalGracefulShutdown();
        ReleaseShell();
    }

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShellLogger.Error($"App: Unhandled exception: {e.Exception}");
        WriteCrashLog(e.Exception);
        TaskbarRecovery.RequestRestart();
        e.Handled = true;
        ExitGracefully();
    }

    internal static string CrashLogPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandBar", "crash.log");

    /// <summary>Çubuk beklenmeyen hatada kapanıp yeniden başlıyor; nedeni başka hiçbir yerde kalmıyor.</summary>
    internal static void WriteCrashLog(object error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            System.IO.File.AppendAllText(CrashLogPath, $"{DateTime.Now:s}\n{error}\n\n");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Kayıt yazılamazsa kapanış yine sürmeli.
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Arka plan iş parçacığında çöküş: süreç ölmeden önce AppBar kaydını arayüz iş parçacığında sil.
        // Exit olayını beklemeden temizle; bu işleyici dönünce süreç sonlanıyor.
        ShellLogger.Error($"App: Unhandled exception: {e.ExceptionObject}");
        WriteCrashLog(e.ExceptionObject);
        TaskbarRecovery.RequestRestart();
        Dispatcher.Invoke(() =>
        {
            ExitGracefully();
            ReleaseShell();
        }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
    }
}
