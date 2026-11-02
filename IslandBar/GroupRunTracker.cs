using System.Collections.Specialized;
using System.ComponentModel;
using ManagedShell.WindowsTasks;

namespace IslandBar;

/// <summary>Gruplardaki hangi uygulamanın açık olduğunu izler; sonuç <see cref="GroupItem"/> üzerinde durur.</summary>
internal sealed class GroupRunTracker : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ICollectionView _windows;
    private readonly HashSet<ApplicationWindow> _watched = new();

    public GroupRunTracker(Tasks tasks, AppSettings settings)
    {
        _settings = settings;
        _windows = tasks.GroupedWindows;
        ((INotifyCollectionChanged)_windows).CollectionChanged += Windows_CollectionChanged;
        Refresh();
    }

    public void Refresh()
    {
        List<ApplicationWindow> windows = _windows.OfType<ApplicationWindow>().Where(window => window.ShowInTaskbar).ToList();

        foreach (ApplicationWindow gone in _watched.Except(windows).ToList())
        {
            gone.PropertyChanged -= Window_PropertyChanged;
            _watched.Remove(gone);
        }
        foreach (ApplicationWindow added in windows.Except(_watched).ToList())
        {
            added.PropertyChanged += Window_PropertyChanged;
            _watched.Add(added);
        }

        foreach (AppGroup group in _settings.Groups)
        {
            foreach (GroupItem item in group.Items)
            {
                item.SetWindows(windows.Where(item.Matches).ToList());
            }
        }
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)_windows).CollectionChanged -= Windows_CollectionChanged;
        foreach (ApplicationWindow window in _watched)
        {
            window.PropertyChanged -= Window_PropertyChanged;
        }
        _watched.Clear();
    }

    private void Windows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Refresh();
    }

    private void Window_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.State) or nameof(ApplicationWindow.ShowInTaskbar))
        {
            Refresh();
        }
    }
}
