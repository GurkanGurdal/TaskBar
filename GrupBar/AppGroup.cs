using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using ManagedShell.ShellFolders;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

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

    [JsonIgnore]
    public string Summary
    {
        get
        {
            int running = _items?.Count(item => item.IsRunning) ?? 0;
            int count = _items?.Count ?? 0;
            return running > 0 ? $"{count} öğe · {running} açık" : $"{count} öğe";
        }
    }

    public void NotifyRunStateChanged()
    {
        OnPropertyChanged(nameof(Summary));
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PreviewItems));
        NotifyRunStateChanged();
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    public override string ToString() => Name;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class GroupItem : INotifyPropertyChanged
{
    public const string StoreAppPrefix = @"shell:AppsFolder\";

    private ImageSource? _icon;
    private bool _iconRequested;
    private bool _isHighlighted;
    private IReadOnlyList<ApplicationWindow> _windows = [];
    private ApplicationWindow? _lastFocused;
    private string? _executable;
    private bool _executableResolved;
    private string? _shortcutAppId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    /// <summary>Uygulamanın kendi ikonu; arka planda yüklenir, hazır olunca haber verir.</summary>
    [JsonIgnore]
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


    /// <summary>Başlat menüsünde klavyeyle seçili.</summary>
    [JsonIgnore]
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted != value)
            {
                _isHighlighted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
            }
        }
    }

    [JsonIgnore]
    public bool IsRunning => _windows.Count > 0;

    [JsonIgnore]
    public bool IsActive => _windows.Any(window => window.State == ApplicationWindow.WindowState.Active);

    /// <summary>Store uygulamasının dosya konumu yok.</summary>
    [JsonIgnore]
    public bool HasFileLocation => !Path.StartsWith(StoreAppPrefix, StringComparison.OrdinalIgnoreCase);

    public bool Matches(ApplicationWindow window)
    {
        if (!HasFileLocation)
        {
            string appId = Path[StoreAppPrefix.Length..];
            return string.Equals(window.AppUserModelID, appId, StringComparison.OrdinalIgnoreCase) || IsFromPackage(window.WinFileName, appId);
        }

        // Kısayolun kendi kimliği varsa o kesindir: Chrome'un profil kısayolu bağımsız değişkenli olsa da eşleşir.
        string? executable = Executable();
        if (_shortcutAppId != null && string.Equals(window.AppUserModelID, _shortcutAppId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return executable != null && string.Equals(window.WinFileName, executable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Paketli masaüstü uygulamaları (Claude, Spotify) pencereye kimlik yazmıyor; exe paket klasöründen çalışır:
    /// Ad_Sürüm_Mimari__Yayıncı. Kimlik: Ad_Yayıncı!Uygulama.
    /// </summary>
    private static bool IsFromPackage(string? executable, string appId)
    {
        const string packagesRoot = @"\WindowsApps\";
        int root = executable?.IndexOf(packagesRoot, StringComparison.OrdinalIgnoreCase) ?? -1;
        string family = appId.Split('!')[0];
        int publisher = family.LastIndexOf('_');
        if (root < 0 || publisher <= 0)
        {
            return false;
        }

        string folder = executable![(root + packagesRoot.Length)..].Split('\\')[0];
        return folder.StartsWith(family[..publisher] + "_", StringComparison.OrdinalIgnoreCase)
            && folder.EndsWith(family[publisher..], StringComparison.OrdinalIgnoreCase);
    }

    public void SetWindows(IReadOnlyList<ApplicationWindow> windows)
    {
        bool wasRunning = IsRunning;
        bool wasActive = IsActive;
        _windows = windows;

        if (wasRunning != IsRunning)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
        if (wasActive != IsActive)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>
    /// Çalışan öğeye tıklanınca öne gelecek pencere, her seferinde sıradaki. Etkin pencereye bakılamaz:
    /// panel açılırken etkinliği çubuk alıyor.
    /// </summary>
    public ApplicationWindow? NextWindow()
    {
        if (_windows.Count == 0)
        {
            return null;
        }

        int last = _lastFocused == null ? -1 : _windows.ToList().IndexOf(_lastFocused);
        _lastFocused = _windows[(last + 1) % _windows.Count];
        return _lastFocused;
    }

    /// <summary>
    /// Pencereyle eşleştirilecek exe. Bağımsız değişkenli kısayol eşleştirilmez: Chrome'un web uygulaması kısayolu
    /// da chrome.exe'yi açar, Chrome açık diye o uygulama açık sayılmamalı.
    /// </summary>
    private string? Executable()
    {
        if (_executableResolved)
        {
            return _executable;
        }
        _executableResolved = true;

        string extension = System.IO.Path.GetExtension(Path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            _executable = Path;
        }
        else if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            _executable = ShortcutTarget(Path);
            _shortcutAppId = ShortcutAppId(Path);
        }
        return _executable;
    }

    private static string? ShortcutAppId(string path)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic? file = shell.NameSpace(System.IO.Path.GetDirectoryName(path))?.ParseName(System.IO.Path.GetFileName(path));
            return file?.ExtendedProperty("System.AppUserModel.ID") as string is { Length: > 0 } id ? id : null;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static string? ShortcutTarget(string path)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath;
            string arguments = shortcut.Arguments;
            return arguments.Length == 0 && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Ekranda henüz yokken yükler; görünen ikonları bekletmez.</summary>
    public void PreloadIcon()
    {
        if (!_iconRequested)
        {
            _iconRequested = true;
            _ = LoadIconAsync(background: true);
        }
    }

    private async Task LoadIconAsync(bool background = false)
    {
        string path = Path;
        _icon = await AppIcon.LoadAsync(path, background);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    public static GroupItem FromPath(string path)
    {
        string name = Directory.Exists(path)
            ? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
            : System.IO.Path.GetFileNameWithoutExtension(path);

        // Sürücü köklerinin (C:\) dosya adı yok.
        return new GroupItem { Name = name.Length > 0 ? name : path, Path = ShortcutStore.Keep(path) };
    }

    /// <summary>"Dosya konumunu aç" için: saklanan kısayolda kopyanın değil, hedefin konumu.</summary>
    [JsonIgnore]
    public string LocationPath
    {
        get
        {
            if (!ShortcutStore.IsKept(Path))
            {
                return Path;
            }

            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                string target = shell.CreateShortcut(Path).TargetPath;
                return File.Exists(target) || Directory.Exists(target) ? target : Path;
            }
            catch (COMException)
            {
                return Path;
            }
        }
    }

    public override string ToString() => Name;
}
