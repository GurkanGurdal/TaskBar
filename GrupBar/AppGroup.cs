using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using ManagedShell.ShellFolders;

namespace GrupBar;

public sealed class AppGroup : INotifyPropertyChanged, IDisposable
{
    private string _name = "";
    private string? _iconPath;
    private ShellItem? _icon;
    private ObservableCollection<GroupItem> _items = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    public string? IconPath
    {
        get => _iconPath;
        set
        {
            _iconPath = value;
            _icon?.Dispose();
            _icon = null;
            OnPropertyChanged(nameof(IconPath));
            OnPropertyChanged(nameof(Icon));
        }
    }

    public ObservableCollection<GroupItem> Items
    {
        get => _items;
        set
        {
            _items.CollectionChanged -= Items_CollectionChanged;
            _items = value;

            // JSON'daki null'ı AppSettings düzeltiyor; o ana kadar burası boş kalabilir.
            if (_items != null)
            {
                _items.CollectionChanged += Items_CollectionChanged;
            }
            OnPropertyChanged(nameof(Items));
            OnPropertyChanged(nameof(PreviewItems));
        }
    }

    /// <summary>Grup düğmesindeki küçük ızgara için ilk dört öğe.</summary>
    [JsonIgnore]
    public IEnumerable<GroupItem> PreviewItems => _items?.Take(4).ToList() ?? [];

    [JsonIgnore]
    public ShellItem? Icon => IconPath == null ? null : _icon ??= new ShellItem(IconPath);

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PreviewItems));
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;

        foreach (GroupItem item in Items)
        {
            item.Dispose();
        }
    }

    public override string ToString() => Name;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class GroupItem : IDisposable
{
    public const string StoreAppPrefix = @"shell:AppsFolder\";

    private ShellItem? _shell;

    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    [JsonIgnore]
    public ShellItem Shell => _shell ??= new ShellItem(Path);

    public static GroupItem FromPath(string path)
    {
        string name = Directory.Exists(path)
            ? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            : System.IO.Path.GetFileNameWithoutExtension(path);

        // Sürücü köklerinin (C:\) dosya adı yok.
        return new GroupItem { Name = name.Length > 0 ? name : path, Path = path };
    }

    public void Dispose()
    {
        _shell?.Dispose();
        _shell = null;
    }

    public override string ToString() => Name;
}
