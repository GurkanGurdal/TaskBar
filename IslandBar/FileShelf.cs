using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace IslandBar;

public sealed class ShelfItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _iconRequested;

    public ShelfItem(string path)
    {
        Path = path;
        string name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        // Sürücü köklerinin (C:\) dosya adı yok.
        Name = name.Length > 0 ? name : path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested)
            {
                _iconRequested = true;
                _ = LoadIconAsync();
            }
            return _icon;
        }
    }

    private async Task LoadIconAsync()
    {
        _icon = await AppIcon.LoadAsync(Path);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }
}

/// <summary>
/// Dosya rafı: sürüklenen dosyanın başka yere götürülene dek beklediği liste. Dosyanın yolunu tutar, kopyalamaz; dosya
/// taşınır ya da silinirse raftan da düşer. Liste uygulama boyunca yaşar, her ekrandaki kart aynısını gösterir.
/// </summary>
internal sealed class FileShelf
{
    private static readonly string SaveFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "shelf.txt");

    private FileShelf()
    {
        try
        {
            if (File.Exists(SaveFile))
            {
                foreach (string path in File.ReadAllLines(SaveFile).Where(Exists))
                {
                    Items.Add(new ShelfItem(path));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"FileShelf: Could not read {SaveFile}: {e.Message}");
        }
    }

    public static FileShelf Shared { get; } = new();

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Exists(path) && Items.All(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                Items.Add(new ShelfItem(path));
            }
        }
        Save();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    /// <summary>Taşınan ya da silinen dosyalar raftan düşer.</summary>
    public void Prune()
    {
        List<ShelfItem> gone = Items.Where(item => !Exists(item.Path)).ToList();
        foreach (ShelfItem item in gone)
        {
            Items.Remove(item);
        }

        if (gone.Count > 0)
        {
            Save();
        }
    }

    private static bool Exists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private void Save()
    {
        try
        {
            File.WriteAllLines(SaveFile, Items.Select(item => item.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"FileShelf: Could not write {SaveFile}: {e.Message}");
        }
    }
}
