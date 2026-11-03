using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace IslandBar;

/// <summary>Tekerlekteki araçlar.</summary>
public enum ToolKind
{
    Timer,
    Screenshots,
    Copied,
    Shelf,
    Reminders,
    Translate
}

/// <summary>
/// IslandBar araçlarının tekerleği (kullanıcının seçimi: çubukta düğme yerine Alt + S ile imlecin olduğu yerde tam
/// halka; Kando, Logitech Actions Ring ve Loop gibi). Basılı tutup fareyi bir aracın yönüne götürüp bırakmak onu açar
/// (Loop ve Blender'ın pie menüsü gibi); yön seçilmeden bırakılırsa tekerlek açık kalır, araca tıklanır. Dosya
/// sürüklerken açılırsa Dosya rafı dilimine bırakılan dosya rafa eklenir (çubuktaki düğmenin yerine). Esc ve dışarı
/// tıklamak kapatır.
/// </summary>
internal sealed class ToolsRing : Window
{
    private const double Radius = 78;
    private const double TileSize = 52;
    private const double TileCorner = 15;
    private const double EdgeRoom = 24;
    private const double DeadZone = 26;
    private const double HoverScale = 1.12;
    private const int OpenMs = 200;
    private const int StaggerMs = 25;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;

    private static readonly (ToolKind Kind, string Glyph, string Name)[] Tools =
    [
        (ToolKind.Timer, "", "Zamanlayıcı"),
        (ToolKind.Screenshots, "", "Ekran görüntüleri"),
        (ToolKind.Copied, "", "Kopyalananlar"),
        (ToolKind.Shelf, "", "Dosya rafı"),
        (ToolKind.Reminders, "", "Hatırlatıcılar"),
        (ToolKind.Translate, "", "Çeviri")
    ];

    private readonly System.Drawing.Point _center;
    private readonly GlassCard[] _tiles = new GlassCard[Tools.Length];
    private readonly Border[] _highlights = new Border[Tools.Length];
    private readonly TextBlock _label;
    private readonly GlassCard _labelPlate;
    private int _selected = -1;
    private bool _closing;

    /// <param name="center">İmlecin ekran pikseli; halka bunun çevresinde açılır.</param>
    public ToolsRing(System.Drawing.Point center)
    {
        _center = center;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        AllowDrop = true;
        double size = 2 * (Radius + TileSize / 2 + EdgeRoom);
        Width = size;
        Height = size;

        var canvas = new Canvas { Width = size, Height = size, Background = Brushes.Transparent };
        for (int i = 0; i < Tools.Length; i++)
        {
            double angle = Angle(i);

            // Seçilince karonun üstünde ada düğmelerinin üzerine gelme zemini.
            var highlight = new Border { CornerRadius = new CornerRadius(TileCorner), Background = Brushes.Transparent };
            // Açık kip (Çeviri) adadaki etkin uygulama gibi soluk zeminde.
            var face = new Grid { Background = IsToolOn(Tools[i].Kind) ? (Brush)FindResource("ActiveBrush") : Brushes.Transparent };
            face.Children.Add(highlight);
            face.Children.Add(new TextBlock
            {
                Text = Tools[i].Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = (Brush)FindResource("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Adayla aynı cam: arkasındaki ekranı kendi alır (açılmadan önce, görünmezken).
            var tile = new GlassCard
            {
                Width = TileSize,
                Height = TileSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(TileCorner),
                BlurRadius = 18,
                Cursor = Cursors.Hand,
                ToolTip = ToolName(i),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.6, 0.6),
                Opacity = 0,
                Content = face,
                Tag = i
            };
            _highlights[i] = highlight;
            tile.MouseEnter += (_, _) => Select((int)tile.Tag);
            tile.MouseLeftButtonUp += (_, _) => Choose((int)tile.Tag);
            Canvas.SetLeft(tile, size / 2 + Radius * Math.Cos(angle) - TileSize / 2);
            Canvas.SetTop(tile, size / 2 + Radius * Math.Sin(angle) - TileSize / 2);
            canvas.Children.Add(tile);
            _tiles[i] = tile;
        }

        _label = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary")
        };
        // Ortalı katmanda: boyunu şablonu yüklenmeden ölçmek yanlış çıkıyor, etiket sağa kayıyordu.
        _labelPlate = new GlassCard
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 3, 9, 4),
            BlurRadius = 18,
            Content = _label,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var root = new Grid();
        root.Children.Add(canvas);
        root.Children.Add(_labelPlate);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
            }
        };
        Deactivated += (_, _) => Dismiss();
        DragOver += Ring_DragOver;
        Drop += Ring_Drop;
    }

    /// <summary>Bir araç seçildi; halkanın merkezi (ekran pikseli) kartın açılacağı yer.</summary>
    public event Action<ToolKind, System.Drawing.Point>? Chosen;

    /// <summary>Tuş bırakıldı: yön seçildiyse o araç açılır, seçilmediyse tekerlek tıklama için açık kalır.</summary>
    public void Release()
    {
        // Dosya sürüklenirken bırakılan tuş seçim sayılmaz: dosya bir dilime bırakılacak.
        if (_selected >= 0 && Mouse.LeftButton != MouseButtonState.Pressed && System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.None)
        {
            Choose(_selected);
        }
    }

    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        CompositionTarget.Rendering -= TrackCursor;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        double scale = source.CompositionTarget.TransformToDevice.M11;
        int half = (int)Math.Round(Width * scale / 2);
        SetWindowPos(source.Handle, IntPtr.Zero, _center.X - half, _center.Y - half, 0, 0, NoSize | NoZOrder);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Yön izleme hemen başlar: kullanıcı cam hazırlanırken de fareyi götürmüş olabilir.
        CompositionTarget.Rendering += TrackCursor;

        // Karolar görünmezken arkaları alınır; kendilerini görüntüye katmazlar.
        await Task.WhenAll(_tiles.Select(tile => tile.RefreshGlassAsync(animateTint: false, isHidden: true)));
        if (_closing)
        {
            return;
        }

        bool animate = SystemParameters.ClientAreaAnimation;
        for (int i = 0; i < _tiles.Length; i++)
        {
            GlassCard tile = _tiles[i];
            var scale = (ScaleTransform)tile.RenderTransform;
            if (!animate)
            {
                tile.Opacity = 1;
                scale.ScaleX = scale.ScaleY = 1;
                continue;
            }

            var begin = TimeSpan.FromMilliseconds(i * StaggerMs);
            var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(OpenMs)) { BeginTime = begin, EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            tile.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(OpenMs)) { BeginTime = begin });
        }
    }

    private static bool IsToolOn(ToolKind kind)
    {
        return kind == ToolKind.Translate && TranslateMode.Shared.IsOn;
    }

    private static string ToolName(int index)
    {
        return IsToolOn(Tools[index].Kind) ? "Çeviriyi kapat" : Tools[index].Name;
    }

    private static double Angle(int index)
    {
        return -Math.PI / 2 + index * 2 * Math.PI / Tools.Length;
    }

    /// <summary>Basılı tutarken imlecin merkezden gittiği yön aracı seçer (pencerenin dışına çıksa da).</summary>
    private void TrackCursor(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
        double scale = source.CompositionTarget.TransformToDevice.M11;
        double dx = (cursor.X - _center.X) / scale;
        double dy = (cursor.Y - _center.Y) / scale;
        if (Math.Sqrt(dx * dx + dy * dy) < DeadZone)
        {
            if (!_tiles.Any(tile => tile.IsMouseOver))
            {
                Select(-1);
            }
            return;
        }

        double angle = Math.Atan2(dy, dx) + Math.PI / 2;
        int nearest = (int)Math.Round(angle / (2 * Math.PI / Tools.Length));
        Select(((nearest % Tools.Length) + Tools.Length) % Tools.Length);
    }

    private void Select(int index)
    {
        if (index == _selected)
        {
            return;
        }

        if (_selected >= 0)
        {
            Emphasize(_selected, false);
        }

        _selected = index;
        if (index < 0)
        {
            _labelPlate.Opacity = 0;
            return;
        }

        Emphasize(index, true);
        _label.Text = ToolName(index);
        ShowLabel();
    }

    /// <summary>Ad etiketi de cam: boyu değişince arkası yeniden alınır, hazır olunca görünür.</summary>
    private async void ShowLabel()
    {
        await _labelPlate.RefreshGlassAsync(animateTint: false, isHidden: _labelPlate.Opacity == 0);
        if (_selected >= 0 && !_closing)
        {
            _labelPlate.Opacity = 1;
        }
    }

    /// <summary>Seçilen karo büyür ve ada düğmeleri gibi üzerine gelme zemini alır.</summary>
    private void Emphasize(int index, bool on)
    {
        var scale = (ScaleTransform)_tiles[index].RenderTransform;
        var to = new DoubleAnimation(on ? HoverScale : 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, to);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, to);
        _highlights[index].Background = on ? (Brush)FindResource("HoverBrush") : Brushes.Transparent;
    }

    private void Choose(int index)
    {
        if (_closing || index < 0)
        {
            return;
        }

        ToolKind kind = Tools[index].Kind;
        Dismiss();
        Chosen?.Invoke(kind, _center);
    }

    private int TileAt(DragEventArgs e)
    {
        for (int i = 0; i < _tiles.Length; i++)
        {
            Point at = e.GetPosition(_tiles[i]);
            if (at.X >= 0 && at.Y >= 0 && at.X <= TileSize && at.Y <= TileSize)
            {
                return i;
            }
        }
        return -1;
    }

    private void Ring_DragOver(object sender, DragEventArgs e)
    {
        int tile = TileAt(e);
        bool shelf = tile >= 0 && Tools[tile].Kind == ToolKind.Shelf && BarWindow.SetDropEffect(e);
        if (!shelf)
        {
            e.Effects = DragDropEffects.None;
        }
        Select(tile);
        e.Handled = true;
    }

    private void Ring_Drop(object sender, DragEventArgs e)
    {
        int tile = TileAt(e);
        if (tile >= 0 && Tools[tile].Kind == ToolKind.Shelf && BarWindow.SetDropEffect(e) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            FileShelf.Shared.Add(paths);
            Dismiss();
            Chosen?.Invoke(ToolKind.Shelf, _center);
        }
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
