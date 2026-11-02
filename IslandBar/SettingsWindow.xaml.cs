using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using ManagedShell.AppBar;

namespace IslandBar;

public partial class SettingsWindow : Window
{
    private readonly BarWindow _bar;
    private readonly AppSettings _settings;

    public SettingsWindow(BarWindow bar, AppSettings settings)
    {
        _bar = bar;
        _settings = settings;

        InitializeComponent();

        EdgeBox.ItemsSource = new Dictionary<string, AppBarEdge>
        {
            ["Alt"] = AppBarEdge.Bottom,
            ["Üst"] = AppBarEdge.Top,
            ["Sol"] = AppBarEdge.Left,
            ["Sağ"] = AppBarEdge.Right
        };
        ThicknessBox.ItemsSource = new Dictionary<string, double>
        {
            ["İnce"] = 32,
            ["Orta"] = 40,
            ["Kalın"] = 48
        };
        ThemeBox.ItemsSource = new Dictionary<string, BarTheme>
        {
            ["Cam"] = BarTheme.Glass,
            ["Beyaz"] = BarTheme.White,
            ["Grafit"] = BarTheme.Graphite
        };

        DataContext = settings;
        UpdateButtons();

        StartupCheck.IsChecked = StartupRegistration.IsEnabled;
        StartupCheck.Checked += StartupCheck_Changed;
        StartupCheck.Unchecked += StartupCheck_Changed;
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.SetEnabled(StartupCheck.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            MessageBox.Show(this, "Windows'la başlat ayarı değiştirilemedi:\n" + ex.Message,
                "IslandBar", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Kutu gerçek durumu göstersin; bu geri alma yeniden yazmayı tetiklemesin.
            StartupCheck.Checked -= StartupCheck_Changed;
            StartupCheck.Unchecked -= StartupCheck_Changed;
            StartupCheck.IsChecked = StartupRegistration.IsEnabled;
            StartupCheck.Checked += StartupCheck_Changed;
            StartupCheck.Unchecked += StartupCheck_Changed;
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        int pinned = PinnedList.SelectedIndex;
        PinnedUpButton.IsEnabled = pinned > 0;
        PinnedDownButton.IsEnabled = pinned >= 0 && pinned < PinnedList.Items.Count - 1;
        PinnedRemoveButton.IsEnabled = pinned >= 0;

        int group = GroupList.SelectedIndex;
        GroupUpButton.IsEnabled = group > 0;
        GroupDownButton.IsEnabled = group >= 0 && group < GroupList.Items.Count - 1;
        GroupRenameButton.IsEnabled = group >= 0;
        GroupDeleteButton.IsEnabled = group >= 0;
    }

    private void MoveSelected<T>(ObservableCollection<T> items, ListBox list, int offset)
    {
        items.Move(list.SelectedIndex, list.SelectedIndex + offset);
        _settings.Save();
        UpdateButtons();
    }

    private void PinnedUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(_settings.Pinned, PinnedList, -1);
    }

    private void PinnedDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(_settings.Pinned, PinnedList, 1);
    }

    private void PinnedRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        _bar.Unpin((GroupItem)PinnedList.SelectedItem);
    }

    private void GroupUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(_settings.Groups, GroupList, -1);
    }

    private void GroupDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(_settings.Groups, GroupList, 1);
    }

    private void GroupRenameButton_Click(object sender, RoutedEventArgs e)
    {
        _bar.RenameGroup((AppGroup)GroupList.SelectedItem, this);
    }

    private void GroupDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _bar.DeleteGroup((AppGroup)GroupList.SelectedItem, this);
    }
}
