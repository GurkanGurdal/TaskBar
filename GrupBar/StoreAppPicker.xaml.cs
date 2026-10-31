using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ManagedShell.ShellFolders;
using ManagedShell.UWPInterop;

namespace GrupBar;

public partial class StoreAppPicker : Window
{
    private List<GroupItem> _apps = new();

    public StoreAppPicker()
    {
        InitializeComponent();
    }

    public List<GroupItem> SelectedApps { get; } = new();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _apps = await Task.Run(() =>
        {
            StoreAppHelper.AppList.FetchApps();
            return StoreAppHelper.AppList
                .Select(app => new GroupItem { Name = LocalizedName(app), Path = GroupItem.StoreAppPrefix + app.AppUserModelId })
                .Where(app => !string.IsNullOrWhiteSpace(app.Name))
                .OrderBy(app => app.Name, StringComparer.Create(CultureInfo.CurrentCulture, true))
                .ToList();
        });

        AppList.ItemsSource = _apps;
        ApplyFilter();
        FilterBox.Focus();
    }

    private static string LocalizedName(StoreApp app)
    {
        // Manifestteki ad çoğu zaman İngilizce; Başlat menüsündeki adı kabuktan al.
        using var shell = new ShellItem(GroupItem.StoreAppPrefix + app.AppUserModelId);
        string name = shell.DisplayName;
        return string.IsNullOrWhiteSpace(name) ? app.DisplayName : name;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (AppList.ItemsSource == null)
        {
            return;
        }

        string filter = FilterBox.Text.Trim();
        CollectionViewSource.GetDefaultView(AppList.ItemsSource).Filter = app =>
            ((GroupItem)app).Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = AppList.SelectedItems.Count > 0;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedApps.AddRange(AppList.SelectedItems.Cast<GroupItem>());
        DialogResult = true;
    }
}
