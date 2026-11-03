using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

/// <summary>
/// "IslandBar Araçları" kartının içi: son ekran görüntüleri, kopyalanan son metinler ve dosya rafı. Sürüklenen şey dosyanın
/// kendisi olduğu için dosya kabul eden her yere bırakılabilir (tarayıcı, sohbet kutusu, Gezgin). Ekran görüntüsü her zaman
/// kopyalanır, geçmişten silinmez. Raftaki dosya gerçek dosya gibi davranır: aynı sürücüdeki klasöre bırakılınca taşınır,
/// raftan düşer. Metin tıklanınca panoya geri konur, sürüklenince metin olarak bırakılır.
/// </summary>
public partial class ToolsPanel : UserControl
{
    /// <summary>Çubuk kendi sürüklemesini pin ya da grup öğesi sanmasın.</summary>
    public const string InternalDragFormat = "IslandBar.Screenshot";

    private const double GhostWidth = 170;
    private const double ShelfGhostSize = 56;
    private const double TextGhostWidth = 220;
    private const double TextGhostHeight = 34;
    private const string ShotHintText = "Sürükleyip istediğin yere bırak. Tıklayınca açılır.";

    private readonly ScreenshotHistory _history = ScreenshotHistory.Shared;
    private readonly ClipboardHistory _copied = ClipboardHistory.Shared;
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
        CopiedList.ItemsSource = _copied.Items;
        ShelfList.ItemsSource = _shelf.Items;
        UpdateState();

        // Liste uygulama boyunca yaşıyor, çubuklar ekran değişince yeniden açılıyor: kapanan kart listeye bağlı kalmasın.
        Loaded += (_, _) =>
        {
            FocusTimer.Shared.Changed += UpdateTimerState;
            UpdateTimerState();
            _history.Items.CollectionChanged += Items_CollectionChanged;
            _copied.Items.CollectionChanged += Items_CollectionChanged;
            _shelf.Items.CollectionChanged += Items_CollectionChanged;
            UpdateState();
        };
        Unloaded += (_, _) =>
        {
            FocusTimer.Shared.Changed -= UpdateTimerState;
            _history.Items.CollectionChanged -= Items_CollectionChanged;
            _copied.Items.CollectionChanged -= Items_CollectionChanged;
            _shelf.Items.CollectionChanged -= Items_CollectionChanged;
        };

        // Kart kapalıyken dosya taşınmış ya da silinmiş olabilir; metin ayarı da değişmiş olabilir.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _shelf.Prune();
                UpdateState();
            }
        };
    }

    /// <summary>
    /// Tekerlekten açılınca yalnızca o aracın bölümü görünür (başlık ve ayraçlar olmadan); null tüm kart. Hatırlatıcılar
    /// burada değil, takvimde.
    /// </summary>
    public ToolKind? Only
    {
        get => _only;
        set
        {
            _only = value;
            ToolsTitle.Visibility = value == null ? Visibility.Visible : Visibility.Collapsed;
            TimerDivider.Visibility = ShotDivider.Visibility = CopiedDivider.Visibility = value == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateState();
        }
    }

    private ToolKind? _only;

    private Visibility SectionVisibility(ToolKind kind) => _only == null || _only == kind ? Visibility.Visible : Visibility.Collapsed;

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
        ShotHint.Text = ShotHintText;
        ShotCount.Text = count > 0 ? $"{count} / {ScreenshotHistory.MaxItems}" : string.Empty;
        ClearButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShotHint.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;

        TimerSection.Visibility = SectionVisibility(ToolKind.Timer);
        ShotSection.Visibility = SectionVisibility(ToolKind.Screenshots);
        ShelfSection.Visibility = SectionVisibility(ToolKind.Shelf);

        int copied = _copied.Items.Count;
        CopiedSection.Visibility = _copied.IsWatching || _only == ToolKind.Copied ? SectionVisibility(ToolKind.Copied) : Visibility.Collapsed;
        CopiedCount.Text = copied > 0 ? $"{copied} / {ClipboardHistory.MaxItems}" : string.Empty;
        CopiedClearButton.Visibility = copied > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopiedScroll.Visibility = copied > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopiedHint.Visibility = copied > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopiedEmpty.Visibility = copied > 0 ? Visibility.Collapsed : Visibility.Visible;

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
        if (_dragged || sender == ShelfAll)
        {
            return;
        }

        // Metin panoya geri konur; kart kapanır ki kullanıcı hemen yapıştırsın.
        if (((FrameworkElement)sender).DataContext is CopiedText text)
        {
            _copied.CopyToClipboard(text);
        }
        else
        {
            OpenFile(((FrameworkElement)sender).DataContext is ScreenshotItem shot ? shot.Path : ShelfItemOf(sender).Path);
        }
        Opened?.Invoke();
    }

    private void StartDrag(FrameworkElement tile)
    {
        string[] paths;
        DragDropEffects allowed;
        DataObject data;

        if (tile.DataContext is CopiedText text)
        {
            // Metin olarak: yazı kabul eden her yere (sohbet kutusu, belge, arama çubuğu) bırakılır.
            data = new DataObject(DataFormats.UnicodeText, text.Text);
            data.SetData(DataFormats.Text, text.Text);
            data.SetData(InternalDragFormat, true);
            _ghost = new DragGhost(TextGhostImage(text.Preview), TextGhostWidth, TextGhostHeight);
            RunDrag(tile, data, DragDropEffects.Copy);
            return;
        }

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

        data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData(InternalDragFormat, true);
        RunDrag(tile, data, allowed);
    }

    private void RunDrag(FrameworkElement tile, DataObject data, DragDropEffects allowed)
    {
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

    /// <summary>Sürüklenen metnin görseli: kart zemininde tek satır, ekranın ölçeğinde çizilir.</summary>
    private BitmapSource TextGhostImage(string text)
    {
        var label = new Border
        {
            Width = TextGhostWidth,
            Height = TextGhostHeight,
            Padding = new Thickness(10, 0, 10, 0),
            Background = (Brush)FindResource("PopupFill"),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        label.Measure(new Size(TextGhostWidth, TextGhostHeight));
        label.Arrange(new Rect(0, 0, TextGhostWidth, TextGhostHeight));

        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(TextGhostWidth * scale), (int)Math.Ceiling(TextGhostHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(label);
        bitmap.Freeze();
        return bitmap;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        _ghost?.FollowCursor();
    }

    private void Tile_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        _ghost?.FollowCursor();
    }

    private const int MaxTimerMinutes = 600;

    private void UpdateTimerState()
    {
        FocusTimer timer = FocusTimer.Shared;
        TimerStopButton.Visibility = timer.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        if (!timer.IsRunning)
        {
            TimerState.Text = string.Empty;
            return;
        }

        (string left, string right) = timer.Display();
        TimerState.Text = $"{left}:{right} kaldı · {timer.EndsAt:t}'de biter";
    }

    /// <summary>Süre başlayınca kart kapanır; kalan süre artık saatte.</summary>
    private void StartTimer(int minutes)
    {
        if (minutes is < 1 or > MaxTimerMinutes)
        {
            return;
        }

        FocusTimer.Shared.Start(TimeSpan.FromMinutes(minutes));
        TimerMinutes.Clear();
        Opened?.Invoke();
    }

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        StartTimer(int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture));
    }

    private void TimerCustom_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TimerMinutes.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int minutes))
        {
            StartTimer(minutes);
        }
        else
        {
            TimerMinutes.Focus();
        }
    }

    private void TimerMinutes_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsAsciiDigit);
    }

    private void TimerMinutes_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TimerCustom_Click(sender, e);
        }
    }

    private void TimerStop_Click(object sender, RoutedEventArgs e)
    {
        FocusTimer.Shared.Stop();
    }

    private void CopiedClear_Click(object sender, RoutedEventArgs e)
    {
        _copied.Clear();
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        _copied.CopyToClipboard((CopiedText)((FrameworkElement)sender).DataContext);
        Opened?.Invoke();
    }

    private void RemoveText_Click(object sender, RoutedEventArgs e)
    {
        _copied.Remove((CopiedText)((FrameworkElement)sender).DataContext);
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

    /// <summary>Görüntüdeki yazı panoya geçer, Kopyalananlar'a da düşer. Yazı yoksa pano değişmez, kart açık kalır.</summary>
    private async void CopyShotText_Click(object sender, RoutedEventArgs e)
    {
        string text = await ScreenshotText.RecognizeAsync(ItemOf(sender).Path);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShotHint.Text = "Bu görüntüde okunabilen yazı bulunamadı.";
            return;
        }

        try
        {
            Clipboard.SetText(text);
            Opened?.Invoke();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Pano o an başkasında; kullanıcı yeniden dener.
        }
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
