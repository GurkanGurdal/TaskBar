using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManagedShell.AppBar;
using ManagedShell.Common.Logging;

namespace GrupBar;

public sealed class AppSettings : INotifyPropertyChanged
{
    private const double MinThickness = 24;
    private const double MaxThickness = 96;
    private const int MaxRunningDots = 4;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrupBar", "settings.json");

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppBarEdge Edge
    {
        get => _edge;
        set => SetField(ref _edge, value);
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


    /// <summary>Başlat düğmesi ve Windows tuşu GrupBar'ın menüsünü açsın; kapalıyken Windows'unkini.</summary>
    public bool CustomStartMenu
    {
        get => _customStartMenu;
        set => SetField(ref _customStartMenu, value);
    }

    public ObservableCollection<GroupItem> Pinned { get; set; } = new();

    /// <summary>Kayıtta hiç yoksa çubuktaki sabitlenenlerle başlar: Windows kendi listesini uygulamalara açmıyor.</summary>
    public ObservableCollection<GroupItem> StartPinned { get; set; } = null!;

    /// <summary>Başlat menüsünden en son açılanlar, en yenisi başta.</summary>
    public ObservableCollection<GroupItem> StartRecents { get; set; } = new();

    public ObservableCollection<AppGroup> Groups { get; set; } = new();

    /// <summary>Gruplar düğmesinin altındaki noktalar: gruplardaki açık uygulamalar.</summary>
    [JsonIgnore]
    public IEnumerable<GroupItem> RunningGroupItems =>
        Groups.SelectMany(group => group.Items).Where(item => item.IsRunning).DistinctBy(item => item.Path).Take(MaxRunningDots).ToList();

    public void NotifyRunStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningGroupItems)));
    }

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
