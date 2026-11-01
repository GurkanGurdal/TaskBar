using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandBar;

/// <summary>
/// "IslandBar Araçları" kartının içi. Şimdilik tek araç: son ekran görüntüleri. Sürüklenen şey dosyanın kendisi olduğu için
/// dosya kabul eden her yere bırakılabilir (tarayıcı, sohbet kutusu, Gezgin); her zaman kopyalanır, geçmişten silinmez.
/// </summary>
public partial class ToolsPanel : UserControl
{
    /// <summary>Çubuk kendi sürüklemesini pin ya da grup öğesi sanmasın.</summary>
    public const string InternalDragFormat = "IslandBar.Screenshot";

    private const double GhostWidth = 170;

    private readonly ScreenshotHistory _history = ScreenshotHistory.Shared;
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
        UpdateState();

        // Liste uygulama boyunca yaşıyor, çubuklar ekran değişince yeniden açılıyor: kapanan kart listeye bağlı kalmasın.
        Loaded += (_, _) =>
        {
            _history.Items.CollectionChanged += Items_CollectionChanged;
            UpdateState();
        };
        Unloaded += (_, _) => _history.Items.CollectionChanged -= Items_CollectionChanged;
    }

    /// <summary>Sürükleme başladı: kart bırakılacak yeri kapatmasın.</summary>
    public event Action? DragStarted;

    /// <summary>Sürükleme bitti; bir yere bırakıldıysa true.</summary>
    public event Action<bool>? DragFinished;

    /// <summary>Görüntü açıldı, kart kapanabilir.</summary>
    public event Action? Opened;

    private static ScreenshotItem ItemOf(object sender) => (ScreenshotItem)((FrameworkElement)sender).DataContext;

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
    }

    private void Shot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedTile = (FrameworkElement)sender;
        _pressPoint = e.GetPosition(_pressedTile);
        _dragged = false;

        // Eşik aşılmadan fare kutudan çıkabilir.
        _pressedTile.CaptureMouse();
        e.Handled = true;
    }

    private void Shot_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTile != sender || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(_pressedTile) - _pressPoint;
        if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _dragged = true;
            _pressedTile.ReleaseMouseCapture();
            StartDrag(_pressedTile, ItemOf(_pressedTile));
            _pressedTile = null;
        }
    }

    private void Shot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedTile != sender)
        {
            return;
        }

        _pressedTile.ReleaseMouseCapture();
        _pressedTile = null;
        e.Handled = true;
        if (!_dragged)
        {
            OpenFile(ItemOf(sender).Path);
            Opened?.Invoke();
        }
    }

    private void StartDrag(FrameworkElement tile, ScreenshotItem item)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
        data.SetData(InternalDragFormat, true);

        double aspect = (double)item.Thumbnail.PixelHeight / Math.Max(1, item.Thumbnail.PixelWidth);
        _ghost = new DragGhost(item.Thumbnail, GhostWidth, GhostWidth * aspect);
        _ghost.Show();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        tile.QueryContinueDrag += Tile_QueryContinueDrag;
        DragStarted?.Invoke();

        DragDropEffects result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
        }
        finally
        {
            tile.QueryContinueDrag -= Tile_QueryContinueDrag;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _ghost.Close();
            _ghost = null;
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
