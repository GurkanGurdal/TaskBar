using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.System;

namespace GrupBar;

/// <summary>
/// GrupBar'ın Başlat menüsü: arama, sabitlenenler, tüm uygulamalar, hesap ve güç. Windows'un kendi menüsünün
/// görünümü Explorer'a girmeden değiştirilemiyor; bu onun yerine açılır.
/// </summary>
public partial class StartMenuPanel : UserControl
{
    private const int Columns = 6;

    /// <summary>Sağ tık menüsü şablonunun gölge payı (Theme.xaml).</summary>
    private const double MenuShadowMargin = 14;

    private static readonly string[] DocumentExtensions = { ".chm", ".txt", ".htm", ".html", ".url", ".pdf", ".rtf", ".md" };

    /// <summary>Liste çubuklar arasında ortak; simgeleri tekrar tekrar yüklenmesin.</summary>
    private static Task<List<GroupItem>>? _appsLoading;

    private AppSettings? _settings;
    private List<GroupItem> _apps = [];
    private List<GroupItem> _results = [];
    private int _selected = -1;
    private bool _open;
    private bool _accountLoaded;

    public StartMenuPanel()
    {
        InitializeComponent();
    }

    /// <summary>Bir eylem başka bir pencere açacaksa panel önce kapanır.</summary>
    public event Action? CloseRequested;

    public event Action<GroupItem>? LaunchFailed;

    public event Action<GroupItem>? PinToBarRequested;

    public event Action? WindowsStartRequested;

    public double CardHeight
    {
        set => Body.Height = value;
    }

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        PinnedList.ItemsSource = settings.StartPinned;
    }

    /// <summary>İlk açılış beklemesin diye liste ve simgeler önceden yüklenir.</summary>
    public static async Task PreloadAsync(AppSettings settings)
    {
        // Sıra sırayla yüklenir: en önde görünenler uzun listenin arkasında beklemesin.
        foreach (GroupItem pin in settings.StartPinned)
        {
            _ = pin.Icon;
        }

        foreach (GroupItem app in await LoadAppsAsync())
        {
            _ = app.Icon;
        }
    }

    /// <summary>İki yüzü aşkın kutuyu önceden kurar: yoksa ilk Windows tuşu bir saniye bekliyor.</summary>
    public async Task WarmAsync()
    {
        _apps = await LoadAppsAsync();
        _results = _apps;
        AppList.ItemsSource = _results;
        Measure(new Size(Width, double.PositiveInfinity));
        Arrange(new Rect(DesiredSize));
    }

    /// <summary>Durumu sıfırlar, görüntüyü alır. Bittiğinde panel gösterilmeye hazırdır.</summary>
    public async Task OpenAsync()
    {
        _open = true;
        SearchBox.Text = string.Empty;
        Scroller.ScrollToTop();
        UpdatePinnedVisibility();
        _ = LoadAccountAsync();

        await Card.RefreshGlassAsync(animateTint: false);
        _ = FillAppsAsync();
    }

    public void Close()
    {
        _open = false;
        Select(-1);
    }

    public Task RefreshGlassAsync(bool animateTint)
    {
        return Card.RefreshGlassAsync(animateTint);
    }

    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }, DispatcherPriority.Input);
    }

    #region Apps
    private async Task FillAppsAsync()
    {
        // Çok sayıda kutu ilk seferde biraz sürüyor: panel önce görünsün.
        if (AppList.ItemsSource == null)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }

        List<GroupItem> apps = await LoadAppsAsync();
        if (!_open)
        {
            return;
        }

        _apps = apps;
        ApplyFilter();

        // Sonraki açılış, bu arada yüklenen ya da kaldırılan uygulamaları görsün.
        _appsLoading = null;
    }

    private static Task<List<GroupItem>> LoadAppsAsync()
    {
        return _appsLoading ??= ReadAppsAsync(_lastApps);
    }

    private static List<GroupItem> _lastApps = [];

    private static Task<List<GroupItem>> ReadAppsAsync(List<GroupItem> known)
    {
        var result = new TaskCompletionSource<List<GroupItem>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Kabuk nesneleri COM başlatılmış iş parçacığı ister.
        var reader = new Thread(() =>
        {
            Dictionary<string, GroupItem> byPath = known.ToDictionary(app => app.Path, StringComparer.OrdinalIgnoreCase);
            StringComparer order = StringComparer.Create(CultureInfo.CurrentCulture, true);
            List<GroupItem> apps = ShortcutStore.InstalledApps()
                .Where(app => IsLaunchableApp(app.Key, app.Value))
                .Select(app =>
                {
                    string path = GroupItem.StoreAppPrefix + app.Value;
                    return byPath.TryGetValue(path, out GroupItem? existing) ? existing : new GroupItem { Name = app.Key, Path = path };
                })
                .OrderBy(app => app.Name, order)
                .ToList();

            // Değişiklik yoksa aynı liste: kutular her açılışta yeniden kurulmasın.
            if (apps.SequenceEqual(known))
            {
                apps = known;
            }
            _lastApps = apps;
            result.SetResult(apps);
        })
        {
            IsBackground = true,
            Name = "StartApps"
        };
        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        return result.Task;
    }

    /// <summary>Başlat klasöründe yardım dosyaları, web bağlantıları ve kaldırıcılar da duruyor.</summary>
    private static bool IsLaunchableApp(string name, string id)
    {
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("kaldır", StringComparison.CurrentCultureIgnoreCase)
            || id.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !DocumentExtensions.Contains(Path.GetExtension(id), StringComparer.OrdinalIgnoreCase);
    }
    #endregion

    #region Search
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;

        Select(-1);
        _results = searching
            ? _apps.Select(app => (App: app, Rank: MatchRank(app.Name, query)))
                .Where(match => match.Rank >= 0)
                .OrderBy(match => match.Rank)
                .Select(match => match.App)
                .ToList()
            : _apps;
        AppList.ItemsSource = _results;

        UpdatePinnedVisibility();
        AppsTitle.Text = searching ? "En iyi sonuçlar" : "Tüm uygulamalar";
        AppsTitle.Visibility = searching && _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResults.Visibility = searching && _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WindowsSearchButton.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        WindowsSearchText.Text = $"\"{query}\" için bu bilgisayarda ara";

        if (searching && _results.Count > 0)
        {
            Select(0);
        }
    }

    /// <summary>Küçük olan önce: başında geçen, sonra sözcük başında, sonra herhangi bir yerde. Yoksa -1.</summary>
    private static int MatchRank(string name, string query)
    {
        // Türkçe i / ı ayrımı İngilizce adlarda aramayı bozmasın.
        int at = CultureInfo.CurrentCulture.CompareInfo.IndexOf(name, query, CompareOptions.IgnoreCase);
        if (at < 0)
        {
            at = CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, query, CompareOptions.IgnoreCase);
        }

        return at < 0 ? -1 : at == 0 ? 0 : name[at - 1] == ' ' ? 1 : 2;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int step = e.Key switch
        {
            Key.Right => SearchBox.Text.Length == 0 ? 1 : 0,
            Key.Left => SearchBox.Text.Length == 0 ? -1 : 0,
            Key.Down => Columns,
            Key.Up => -Columns,
            _ => 0
        };

        if (step != 0 && _results.Count > 0)
        {
            int next = _selected < 0 ? 0 : _selected + step;
            if (next >= 0 && next < _results.Count)
            {
                Select(next);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_selected >= 0)
            {
                Launch(_results[_selected]);
            }
            else if (SearchBox.Text.Trim().Length > 0)
            {
                SearchWindows();
            }
            e.Handled = true;
        }
    }

    private void Select(int index)
    {
        if (_selected >= 0 && _selected < _results.Count)
        {
            _results[_selected].IsHighlighted = false;
        }

        _selected = index;
        if (index < 0)
        {
            return;
        }

        _results[index].IsHighlighted = true;
        if (AppList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement tile)
        {
            tile.BringIntoView();
        }
    }

    private void WindowsSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchWindows();
    }

    private void SearchWindows()
    {
        Open("search-ms:query=" + Uri.EscapeDataString(SearchBox.Text.Trim()));
    }
    #endregion

    #region Tiles
    private void UpdatePinnedVisibility()
    {
        PinnedSection.Visibility = SearchBox.Text.Trim().Length == 0 && _settings?.StartPinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        Launch((GroupItem)((FrameworkElement)sender).DataContext);
    }

    private void Launch(GroupItem item)
    {
        CloseRequested?.Invoke();
        if (!Launcher.Launch(item))
        {
            LaunchFailed?.Invoke(item);
        }
    }

    private bool IsPinned(GroupItem item)
    {
        return _settings!.StartPinned.Any(pin => string.Equals(pin.Path, item.Path, StringComparison.OrdinalIgnoreCase));
    }

    private void TileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        bool pinned = IsPinned((GroupItem)menu.DataContext);
        ((MenuItem)menu.Items[0]).Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        ((MenuItem)menu.Items[1]).Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
    }

    private static GroupItem ItemOf(object sender)
    {
        return (GroupItem)((FrameworkElement)sender).DataContext;
    }

    private void PinToStart_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);
        _settings!.StartPinned.Add(new GroupItem { Name = item.Name, Path = item.Path });
        _settings.Save();
        UpdatePinnedVisibility();
    }

    private void UnpinFromStart_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);
        GroupItem? pin = _settings!.StartPinned.FirstOrDefault(pinned => string.Equals(pinned.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        if (pin != null)
        {
            _settings.StartPinned.Remove(pin);
            _settings.Save();
        }
        UpdatePinnedVisibility();
    }

    private void PinToBar_Click(object sender, RoutedEventArgs e)
    {
        GroupItem item = ItemOf(sender);
        PinToBarRequested?.Invoke(new GroupItem { Name = item.Name, Path = item.Path });
    }
    #endregion

    #region Footer
    private async Task LoadAccountAsync()
    {
        if (_accountLoaded)
        {
            return;
        }
        _accountLoaded = true;
        AccountName.Text = Environment.UserName;

        try
        {
            IReadOnlyList<User> users = await User.FindAllAsync(UserType.LocalUser, UserAuthenticationStatus.LocallyAuthenticated);
            if (users.FirstOrDefault() is not { } user)
            {
                return;
            }

            if (await user.GetPropertyAsync(KnownUserProperties.DisplayName) is string { Length: > 0 } name)
            {
                AccountName.Text = name;
            }

            if (await user.GetPictureAsync(UserPictureSize.Size64x64) is { } reference)
            {
                using var stream = await reference.OpenReadAsync();
                var picture = new BitmapImage();
                picture.BeginInit();
                picture.CacheOption = BitmapCacheOption.OnLoad;
                picture.StreamSource = stream.AsStreamForRead();
                picture.EndInit();
                picture.Freeze();
                AccountPicture.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
                AccountPicture.Visibility = Visibility.Visible;
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or IOException)
        {
            // Hesap bilgisi alınamazsa Windows kullanıcı adı ve genel simge kalır.
        }
    }

    private void Open(string target)
    {
        CloseRequested?.Invoke();
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        Open("ms-settings:accounts");
    }

    private void Explorer_Click(object sender, RoutedEventArgs e)
    {
        Open("explorer.exe");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Open("ms-settings:");
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = PowerButton.ContextMenu;
        menu.PlacementTarget = PowerButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;

        // Sağ kenarı düğmeyle hizalı: boyutu ancak açılınca belli, yoksa kartın dışına taşar.
        menu.HorizontalOffset = PowerButton.ActualWidth - menu.ActualWidth + MenuShadowMargin;
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        LockWorkStation();
    }

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        SetSuspendState(false, false, false);
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        RunShutdown("/l");
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        RunShutdown("/r /t 0");
    }

    private void ShutDown_Click(object sender, RoutedEventArgs e)
    {
        RunShutdown("/s /hybrid /t 0");
    }

    private void RunShutdown(string arguments)
    {
        CloseRequested?.Invoke();
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"StartMenuPanel: shutdown {arguments} failed: {e.Message}");
        }
    }

    private void WindowsStart_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        WindowsStartRequested?.Invoke();
    }

    [DllImport("user32")]
    private static extern bool LockWorkStation();

    [DllImport("powrprof")]
    private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
    #endregion
}
