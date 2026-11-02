using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandBar;

/// <summary>
/// "IslandBar Araçları" kartının içi: son ekran görüntüleri ve dosya rafı. Sürüklenen şey dosyanın kendisi olduğu için dosya
/// kabul eden her yere bırakılabilir (tarayıcı, sohbet kutusu, Gezgin). Ekran görüntüsü her zaman kopyalanır, geçmişten
/// silinmez. Raftaki dosya gerçek dosya gibi davranır: aynı sürücüdeki klasöre bırakılınca taşınır, raftan düşer.
/// </summary>
public partial class ToolsPanel : UserControl
{
    /// <summary>Çubuk kendi sürüklemesini pin ya da grup öğesi sanmasın.</summary>
    public const string InternalDragFormat = "IslandBar.Screenshot";

    private const double GhostWidth = 170;
    private const double ShelfGhostSize = 56;

    private readonly ScreenshotHistory _history = ScreenshotHistory.Shared;
    private readonly FileShelf _shelf = FileShelf.Shared;
    private FrameworkElement? _pressedTile;
    private Point _pressPoint;
    private bool _dragged;
    private DragGhost? _ghost;

    public ToolsPanel()
    {
        InitializeComponent();

        // Tasarımcıda yerleşim boş kalsın.
        if (DesignerProperties.GetIsInDesignMode(this))
        {
            return;
        }

        ShotList.ItemsSource = _history.Items;
        ShelfList.ItemsSource = _shelf.Items;
        UpdateState();

        // Liste uygulama boyunca yaşıyor, çubuklar ekran değişince yeniden açılıyor: kapanan kart listeye bağlı kalmasın.
        Loaded += (_, _) =>
        {
            _history.Items.CollectionChanged += Items_CollectionChanged;
            _shelf.Items.CollectionChanged += Items_CollectionChanged;
            UpdateState();
        };
        Unloaded += (_, _) =>
        {
            _history.Items.CollectionChanged -= Items_CollectionChanged;
            _shelf.Items.CollectionChanged -= Items_CollectionChanged;
        };

        // Kart kapalıyken dosya taşınmış ya da silinmiş olabilir.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _shelf.Prune();
            }
        };
    }

    /// <summary>Sürükleme başladı: kart bırakılacak yeri kapatmasın.</summary>
    public event Action? DragStarted;

    /// <summary>Sürükleme bitti; bir yere bırakıldıysa true.</summary>
    public event Action<bool>? DragFinished;

    /// <summary>Görüntü açıldı, kart kapanabilir.</summary>
    public event Action? Opened;

    private static ScreenshotItem ItemOf(object sender) => (ScreenshotItem)((FrameworkElement)sender).DataContext;

    private static ShelfItem ShelfItemOf(object sender) => (ShelfItem)((FrameworkElement)sender).DataContext;

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        int count = _history.Items.Count;
        ShotCount.Text = count > 0 ? $"{count} / {ScreenshotHistory.MaxItems}" : string.Empty;
        ClearButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotHint.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;

        int shelved = _shelf.Items.Count;
        ShelfCount.Text = shelved > 0 ? shelved.ToString(CultureInfo.CurrentCulture) : string.Empty;
        ShelfClearButton.Visibility = shelved > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfAll.Visibility = shelved > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShelfEmpty.Visibility = shelved > 0 ? Visibility.Collapsed : Visibility.Visible;
        ShelfScroll.Visibility = shelved > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        ShelfZone.Background = BarWindow.SetDropEffect(e) ? (Brush)FindResource("DropHighlight") : Brushes.Transparent;
    }

    private void Panel_DragLeave(object sender, DragEventArgs e)
    {
        ShelfZone.Background = Brushes.Transparent;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        ShelfZone.Background = Brushes.Transparent;
        if (BarWindow.SetDropEffect(e) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _shelf.Add(paths);
        }
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedTile = (FrameworkElement)sender;
        _pressPoint = e.GetPosition(_pressedTile);
        _dragged = false;

        // Eşik aşılmadan fare kutudan çıkabilir.
        _pressedTile.CaptureMouse();
        e.Handled = true;
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTile != sender || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(_pressedTile) - _pressPoint;
        if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            // Önce alan temizlenir: yakalamayı bırakmak aynı olaya yeniden girebiliyor, içerideki sürükleme bitince
            // dışarıdaki alanı boş bulup çubuğu kapatıyordu.
            FrameworkElement tile = _pressedTile;
            _pressedTile = null;
            _dragged = true;
            tile.ReleaseMouseCapture();
            StartDrag(tile);
        }
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedTile != sender)
        {
            return;
        }

        _pressedTile.ReleaseMouseCapture();
        _pressedTile = null;
        e.Handled = true;
        if (!_dragged && sender != ShelfAll)
        {
            OpenFile(((FrameworkElement)sender).DataContext is ScreenshotItem shot ? shot.Path : ShelfItemOf(sender).Path);
            Opened?.Invoke();
        }
    }

    private void StartDrag(FrameworkElement tile)
    {
        string[] paths;
        DragDropEffects allowed;

        if (tile.DataContext is ScreenshotItem shot)
        {
            paths = [shot.Path];
            allowed = DragDropEffects.Copy;
            double aspect = (double)shot.Thumbnail.PixelHeight / Math.Max(1, shot.Thumbnail.PixelWidth);
            _ghost = new DragGhost(shot.Thumbnail, GhostWidth, GhostWidth * aspect);
        }
        else
        {
            List<ShelfItem> items = tile == ShelfAll ? _shelf.Items.ToList() : [ShelfItemOf(tile)];
            if (items.Count == 0)
            {
                return;
            }

            paths = items.Select(item => item.Path).ToArray();
            allowed = DragDropEffects.Copy | DragDropEffects.Move;
            if (items[0].Icon is { } icon)
            {
                _ghost = new DragGhost(icon, ShelfGhostSize, ShelfGhostSize);
            }
        }

        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData(InternalDragFormat, true);

        _ghost?.Show();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        tile.QueryContinueDrag += Tile_QueryContinueDrag;
        DragStarted?.Invoke();

        DragDropEffects result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(tile, data, allowed);
        }
        finally
        {
            tile.QueryContinueDrag -= Tile_QueryContinueDrag;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _ghost?.Close();
            _ghost = null;
            _shelf.Prune();
            DragFinished?.Invoke(result != DragDropEffects.None);
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        _ghost?.FollowCursor();
    }

    private void Tile_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        _ghost?.FollowCursor();
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Clear();
    }

    private void RevealShelf_Click(object sender, RoutedEventArgs e)
    {
        Process.Start("explorer.exe", $"/select,\"{ShelfItemOf(sender).Path}\"")?.Dispose();
        Opened?.Invoke();
    }

    private void RemoveShelf_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Remove(ShelfItemOf(sender));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
    }

    private void CopyShot_Click(object sender, RoutedEventArgs e)
    {
        _history.CopyToClipboard(ItemOf(sender));
    }

    private void RevealShot_Click(object sender, RoutedEventArgs e)
    {
        Process.Start("explorer.exe", $"/select,\"{ItemOf(sender).Path}\"")?.Dispose();
        Opened?.Invoke();
    }

    private void DeleteShot_Click(object sender, RoutedEventArgs e)
    {
        _history.Remove(ItemOf(sender));
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"ToolsPanel: Could not open {path}: {e.Message}");
        }
    }
}
