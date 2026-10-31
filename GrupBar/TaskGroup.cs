using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

namespace GrupBar;

/// <summary>
/// Çubukta tek düğme olan uygulama: aynı uygulamanın pencereleri ve varsa sabitlendiği öğe.
/// Sabitlenmiş uygulama kapalıyken de durur; o zaman hiç penceresi yoktur.
/// </summary>
public sealed class TaskGroup : INotifyPropertyChanged
{
    private ApplicationWindow? _lastActive;

    public TaskGroup(string key, GroupItem? pin = null)
    {
        Key = key;
        Pin = pin;
        if (pin != null)
        {
            pin.PropertyChanged += Pin_PropertyChanged;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public GroupItem? Pin { get; }

    public bool IsPinned => Pin != null;

    public bool IsUnpinned => Pin == null;

    public bool HasWindows => Windows.Count > 0;

    /// <summary>İpucu yalnızca kapalı pinde: açık uygulamanın üzerinde pencere önizlemesi var.</summary>
    public string? ClosedPinName => HasWindows ? null : Pin?.Name;

    public ObservableCollection<ApplicationWindow> Windows { get; } = new();

    public ImageSource? Icon => Pin != null ? Pin.Icon : Windows.FirstOrDefault()?.Icon;

    public string? Title => HasMany ? $"{Windows[0].Title} ({Windows.Count})" : Windows.FirstOrDefault()?.Title ?? Pin?.Name;

    public bool HasMany => Windows.Count > 1;

    public string CloseLabel => HasMany ? "Tüm pencereleri kapat" : "Pencereyi kapat";

    public ApplicationWindow? ActiveWindow => Windows.FirstOrDefault(window => window.State == ApplicationWindow.WindowState.Active);

    public ApplicationWindow.WindowState State
    {
        get
        {
            if (Windows.Any(window => window.State == ApplicationWindow.WindowState.Flashing))
            {
                return ApplicationWindow.WindowState.Flashing;
            }
            return ActiveWindow != null ? ApplicationWindow.WindowState.Active : ApplicationWindow.WindowState.Inactive;
        }
    }

    /// <summary>
    /// Windows'taki gibi aynı uygulama: önce uygulama kimliği, kimliği olmayanlarda exe yolu. Kimlik uygulama
    /// başına değil de profil başınaysa (Chrome gibi) Windows da ayrı gösterir.
    /// </summary>
    public static string KeyOf(ApplicationWindow window)
    {
        string? id = window.AppUserModelID;
        return string.IsNullOrEmpty(id) ? (window.WinFileName ?? string.Empty).ToLowerInvariant() : id;
    }

    /// <summary>Tıklanınca öne gelecek pencere: etkin olan varsa sıradaki, yoksa en son etkin olan.</summary>
    public ApplicationWindow NextWindow(ApplicationWindow? active)
    {
        if (active != null)
        {
            return Windows[(Windows.IndexOf(active) + 1) % Windows.Count];
        }
        return _lastActive != null && Windows.Contains(_lastActive) ? _lastActive : Windows[0];
    }

    public void SetWindows(IReadOnlyCollection<ApplicationWindow> windows)
    {
        bool changed = false;

        foreach (ApplicationWindow window in Windows.Except(windows).ToList())
        {
            window.PropertyChanged -= Window_PropertyChanged;
            Windows.Remove(window);
            changed = true;
        }

        foreach (ApplicationWindow window in windows.Except(Windows).ToList())
        {
            window.PropertyChanged += Window_PropertyChanged;
            Windows.Add(window);
            changed = true;
        }

        if (changed)
        {
            // Boş ad "tüm özellikler" demek.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public void Detach()
    {
        SetWindows(Array.Empty<ApplicationWindow>());
        if (Pin != null)
        {
            Pin.PropertyChanged -= Pin_PropertyChanged;
        }
    }

    private void Pin_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupItem.Icon))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    private void Window_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApplicationWindow.State) && ((ApplicationWindow)sender!).State == ApplicationWindow.WindowState.Active)
        {
            _lastActive = (ApplicationWindow)sender!;
        }

        if (e.PropertyName is nameof(ApplicationWindow.State) or nameof(ApplicationWindow.Title) or nameof(ApplicationWindow.Icon))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
        }
    }
}
