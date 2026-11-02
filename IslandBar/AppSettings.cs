using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManagedShell.AppBar;
using ManagedShell.Common.Logging;

namespace IslandBar;

public sealed class AppSettings : INotifyPropertyChanged
{
    private const double MinThickness = 24;
    private const double MaxThickness = 96;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private AppBarEdge _edge = AppBarEdge.Bottom;
    private double _thickness = 40;
    private bool _showClock = true;
    private bool _showDate = true;
    private bool _hideWindowsTaskbar = true;
    private bool _autoHideBar;
    private bool _customStartMenu = true;
    private bool _clockChipOnDesktop = true;
    private bool _showNowPlayingChip = true;
    // Başka uygulamanın ayar dosyasına yazdığı için kullanıcı açıklamayı okuyup kendisi açar.
    private bool _showClaudeChip;
    private bool _showVolumeChip = true;
    private bool _keepScreenshots = true;
    private bool _keepCopiedText = true;
    private BarTheme _theme = BarTheme.Glass;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppBarEdge Edge
    {
        get => _edge;
        set => SetField(ref _edge, value);
    }

    public BarTheme Theme
    {
        get => _theme;
        set => SetField(ref _theme, value);
    }

    /// <summary>Alınan ekran görüntülerinin kopyası IslandBar Araçları için saklansın.</summary>
    public bool KeepScreenshots
    {
        get => _keepScreenshots;
        set => SetField(ref _keepScreenshots, value);
    }

    /// <summary>Kopyalanan son metinler IslandBar Araçları'nda tutulsun (yalnızca bellekte).</summary>
    public bool KeepCopiedText
    {
        get => _keepCopiedText;
        set => SetField(ref _keepCopiedText, value);
    }

    public double Thickness
    {
        get => _thickness;
        set => SetField(ref _thickness, value);
    }

    public bool ShowClock
    {
        get => _showClock;
        set => SetField(ref _showClock, value);
    }

    public bool ShowDate
    {
        get => _showDate;
        set => SetField(ref _showDate, value);
    }

    public bool HideWindowsTaskbar
    {
        get => _hideWindowsTaskbar;
        set => SetField(ref _hideWindowsTaskbar, value);
    }

    public bool AutoHideBar
    {
        get => _autoHideBar;
        set => SetField(ref _autoHideBar, value);
    }


    /// <summary>Otomatik gizlemede, altında pencere yokken çubuk gizlenmek yerine saate dönüşsün.</summary>
    public bool ClockChipOnDesktop
    {
        get => _clockChipOnDesktop;
        set => SetField(ref _clockChipOnDesktop, value);
    }

    /// <summary>Başlat düğmesi ve Windows tuşu IslandBar'ın menüsünü açsın; kapalıyken Windows'unkini.</summary>
    public bool CustomStartMenu
    {
        get => _customStartMenu;
        set => SetField(ref _customStartMenu, value);
    }

    /// <summary>Spotify'da yeni şarkı başlayınca çubuğun yanında küçük kart çıksın.</summary>
    public bool ShowNowPlayingChip
    {
        get => _showNowPlayingChip;
        set => SetField(ref _showNowPlayingChip, value);
    }

    /// <summary>
    /// Claude Code bitirince ya da onay bekleyince şarkı kartıyla aynı kart çıksın. Açıkken IslandBar Claude Code'un ayar
    /// dosyasına kanca yazar; varsayılan kapalı (kullanıcının seçimi).
    /// </summary>
    public bool ShowClaudeChip
    {
        get => _showClaudeChip;
        set => SetField(ref _showClaudeChip, value);
    }

    /// <summary>Ses tuşlarında Windows'un göstergesi yerine çubuğun kartı; kapalıyken tuşlara hiç karışılmaz.</summary>
    public bool ShowVolumeChip
    {
        get => _showVolumeChip;
        set => SetField(ref _showVolumeChip, value);
    }

    public ObservableCollection<GroupItem> Pinned { get; set; } = new();

    /// <summary>Kayıtta hiç yoksa çubuktaki sabitlenenlerle başlar: Windows kendi listesini uygulamalara açmıyor.</summary>
    public ObservableCollection<GroupItem> StartPinned { get; set; } = null!;

    /// <summary>Başlat menüsünden en son açılanlar, en yenisi başta.</summary>
    public ObservableCollection<GroupItem> StartRecents { get; set; } = new();

    public ObservableCollection<AppGroup> Groups { get; set; } = new();

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException e)
        {
            // Sonraki kayıt gruplarının üzerine yazmasın diye bozuk dosyayı sakla.
            ShellLogger.Error($"AppSettings: {FilePath} is corrupt: {e.Message}");
            KeepCorruptFile();
            settings = new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        settings.Normalize();
        if (settings.SecureShortcuts())
        {
            settings.Save();
        }
        return settings;
    }

    /// <summary>Eski kayıtlar ve silinmiş kısayollar için; yeni öğeler eklenirken zaten korunur.</summary>
    private bool SecureShortcuts()
    {
        List<GroupItem> items = Pinned.Concat(StartPinned).Concat(StartRecents).Concat(Groups.SelectMany(group => group.Items)).ToList();
        bool changed = ShortcutStore.Repair(items);

        foreach (GroupItem item in items)
        {
            string kept = ShortcutStore.Keep(item.Path);
            changed |= kept != item.Path;
            item.Path = kept;
        }
        return changed;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Yazarken çökerse eski dosya sağlam kalsın.
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, FilePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Ayar yazılamadı diye görev çubuğu kapanmamalı.
            ShellLogger.Error($"AppSettings: Could not save {FilePath}: {e.Message}");
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void KeepCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".bozuk", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"AppSettings: Could not keep corrupt file: {e.Message}");
        }
    }

    private void Normalize()
    {
        if (!Enum.IsDefined(Edge))
        {
            Edge = AppBarEdge.Bottom;
        }
        if (!Enum.IsDefined(Theme))
        {
            Theme = BarTheme.Glass;
        }
        Thickness = Math.Clamp(Thickness, MinThickness, MaxThickness);

        Pinned ??= new ObservableCollection<GroupItem>();
        RemoveInvalidItems(Pinned);

        StartPinned ??= new ObservableCollection<GroupItem>(Pinned.Select(item => new GroupItem { Name = item.Name, Path = item.Path }));
        RemoveInvalidItems(StartPinned);
        StartRecents ??= new ObservableCollection<GroupItem>();
        RemoveInvalidItems(StartRecents);

        Groups ??= new ObservableCollection<AppGroup>();
        foreach (AppGroup group in Groups.Where(g => g == null).ToList())
        {
            Groups.Remove(group);
        }

        foreach (AppGroup group in Groups)
        {
            group.Name ??= "";
            group.Items ??= new ObservableCollection<GroupItem>();
            RemoveInvalidItems(group.Items);
        }
    }

    private static void RemoveInvalidItems(ObservableCollection<GroupItem> items)
    {
        foreach (GroupItem item in items.Where(i => i == null || string.IsNullOrWhiteSpace(i.Path)).ToList())
        {
            items.Remove(item);
        }
    }
}
