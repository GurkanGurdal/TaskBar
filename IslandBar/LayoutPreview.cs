using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

/// <summary>
/// Düzen klasörünün yüzü: düzenin kullandığı ekranlar küçük monitörler olarak, pencereler kaydedildikleri yerde ve
/// boyda, uygulamanın ikonundan alınan renkte (kullanıcının seçtiği "mini ekran" tasarımı). Yer kaydı olmayan düzende
/// uygulamalar ekranı eşit sütunlara böler. Pencereler alttan üste kayıtlı, çizim de o sırayla.
/// </summary>
public sealed class LayoutPreview : FrameworkElement
{
    public static readonly DependencyProperty GroupProperty = DependencyProperty.Register(
        nameof(Group), typeof(AppGroup), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LayoutPreview)d).Watch()));

    private const double ScreenGap = 3;
    private const double FrameThickness = 1.5;
    private const double FrameRadius = 5;
    private const double FrameOpacity = 0.5;
    private const double WindowInset = 2.5;
    private const double WindowGap = 1;
    private const double WindowRadius = 2.5;
    private const byte WindowAlpha = 0x70;
    private const double MaxIcon = 12;
    private const double IconRoom = 3;
    private const int MaxColumns = 4;

    private static readonly IconInkConverter Ink = new();
    private static readonly ConditionalWeakTable<ImageSource, object> Colors = new();

    private AppGroup? _group;
    private System.Collections.ObjectModel.ObservableCollection<GroupItem>? _items;

    public LayoutPreview()
    {
        // Kart kapalıyken ikonlar gelmiş olabilir.
        Loaded += (_, _) =>
        {
            Watch();
            InvalidateVisual();
        };
        Unloaded += (_, _) => Unwatch();
    }

    public AppGroup? Group
    {
        get => (AppGroup?)GetValue(GroupProperty);
        set => SetValue(GroupProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (Group is not { } group)
        {
            return;
        }

        (List<System.Drawing.Rectangle> screens, List<(GroupItem Item, System.Drawing.Rectangle Bounds)> windows) = Arrange(group);
        if (screens.Count == 0)
        {
            return;
        }

        // Ekranlar yan yana, gerçek en-boy oranlarıyla; toplamı kutuya sığar, ortalanır.
        double totalWidth = screens.Sum(screen => (double)screen.Width);
        double tallest = screens.Max(screen => (double)screen.Height);
        double room = ActualWidth - ScreenGap * (screens.Count - 1);
        double scale = Math.Min(room / totalWidth, ActualHeight / tallest);
        double x = (ActualWidth - (totalWidth * scale + ScreenGap * (screens.Count - 1))) / 2;

        var frame = new Pen(Resource("TextPrimary").Clone(), FrameThickness);
        frame.Brush.Opacity = FrameOpacity;
        frame.Freeze();
        Brush face = Resource("SubtleFill");

        foreach (System.Drawing.Rectangle screen in screens)
        {
            var box = new Rect(x, (ActualHeight - screen.Height * scale) / 2, screen.Width * scale, screen.Height * scale);
            Rect inner = Inset(box, WindowInset);
            drawing.DrawRoundedRectangle(face, frame, Inset(box, FrameThickness / 2), FrameRadius, FrameRadius);

            drawing.PushClip(new RectangleGeometry(inner, WindowRadius, WindowRadius));
            foreach ((GroupItem item, System.Drawing.Rectangle bounds) in windows.Where(window => screen.IntersectsWith(window.Bounds)))
            {
                var shown = System.Drawing.Rectangle.Intersect(screen, bounds);
                var rect = new Rect(
                    inner.X + (shown.X - screen.X) * inner.Width / screen.Width,
                    inner.Y + (shown.Y - screen.Y) * inner.Height / screen.Height,
                    shown.Width * inner.Width / screen.Width,
                    shown.Height * inner.Height / screen.Height);
                DrawWindow(drawing, item, Inset(rect, WindowGap / 2));
            }
            drawing.Pop();

            x += box.Width + ScreenGap;
        }
    }

    /// <summary>Düzenin ekranları (soldan sağa) ve pencereler. Kayıttaki ekran artık yoksa pencere en yakın ekrana düşer.</summary>
    private static (List<System.Drawing.Rectangle>, List<(GroupItem, System.Drawing.Rectangle)>) Arrange(AppGroup group)
    {
        var windows = new List<(GroupItem, System.Drawing.Rectangle)>();
        var screens = new List<System.Drawing.Rectangle>();

        foreach (GroupItem item in group.Items.Where(item => item.Spot != null))
        {
            WindowSpot spot = item.Spot!;
            var bounds = new System.Drawing.Rectangle(spot.Left, spot.Top, spot.Width, spot.Height);
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromRectangle(bounds).Bounds;
            if (!screen.IntersectsWith(bounds))
            {
                bounds.Location = new System.Drawing.Point(screen.X + (screen.Width - bounds.Width) / 2, screen.Y + (screen.Height - bounds.Height) / 2);
            }

            windows.Add((item, spot.Maximized ? screen : bounds));
            if (!screens.Contains(screen))
            {
                screens.Add(screen);
            }
        }

        if (windows.Count == 0)
        {
            System.Drawing.Rectangle screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            List<GroupItem> items = group.Items.Take(MaxColumns).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                int left = screen.X + screen.Width * i / items.Count;
                int right = screen.X + screen.Width * (i + 1) / items.Count;
                windows.Add((items[i], new System.Drawing.Rectangle(left, screen.Y, right - left, screen.Height)));
            }
            screens.Add(screen);
        }

        screens.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return (screens, windows);
    }

    private static void DrawWindow(DrawingContext drawing, GroupItem item, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        ImageSource? icon = item.Icon;
        Color tone = ToneOf(icon);
        var fill = new SolidColorBrush(Color.FromArgb(WindowAlpha, tone.R, tone.G, tone.B));
        fill.Freeze();
        drawing.DrawRoundedRectangle(fill, null, rect, WindowRadius, WindowRadius);

        double size = Math.Min(MaxIcon, Math.Min(rect.Width, rect.Height) - IconRoom);
        if (icon != null && size >= MaxIcon / 2)
        {
            var image = (ImageSource)Ink.Convert(icon, typeof(ImageSource), null, CultureInfo.InvariantCulture)!;
            drawing.DrawImage(image, new Rect(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size));
        }
    }

    /// <summary>Pencerenin rengi: ikonun canlı renkleri (medya kartındaki kapak rengiyle aynı hesap). İkon gelmeden vurgu rengi.</summary>
    private static Color ToneOf(ImageSource? icon)
    {
        if (icon is not BitmapSource bitmap)
        {
            return ThemeManager.AccentColor;
        }

        return (Color)Colors.GetValue(icon, _ => QuickSettingsPanel.CoverAccent(bitmap));
    }

    private static Brush Resource(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private static Rect Inset(Rect rect, double amount)
    {
        return new Rect(rect.X + amount, rect.Y + amount, Math.Max(0, rect.Width - 2 * amount), Math.Max(0, rect.Height - 2 * amount));
    }

    /// <summary>
    /// İkonlar arka planda yüklenir, düzen yeniden kaydedilebilir (öğe listesi bütünüyle değişir): değişince yeniden
    /// çizilir. Düzenler uygulama boyunca yaşıyor; ağaçtan çıkan önizleme onlara bağlı kalmasın.
    /// </summary>
    private void Watch()
    {
        Unwatch();
        if (Group is not { } group || !IsLoaded)
        {
            return;
        }

        _group = group;
        _items = group.Items;
        group.PropertyChanged += Group_PropertyChanged;
        _items.CollectionChanged += Items_CollectionChanged;
        foreach (GroupItem item in _items)
        {
            item.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void Unwatch()
    {
        if (_group != null)
        {
            _group.PropertyChanged -= Group_PropertyChanged;
            _group = null;
        }

        if (_items != null)
        {
            _items.CollectionChanged -= Items_CollectionChanged;
            foreach (GroupItem item in _items)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }
            _items = null;
        }
    }

    private void Group_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppGroup.Items))
        {
            Watch();
            InvalidateVisual();
        }
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (GroupItem item in e.OldItems?.OfType<GroupItem>() ?? [])
        {
            item.PropertyChanged -= Item_PropertyChanged;
        }
        foreach (GroupItem item in e.NewItems?.OfType<GroupItem>() ?? [])
        {
            item.PropertyChanged += Item_PropertyChanged;
        }
        InvalidateVisual();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupItem.Icon))
        {
            InvalidateVisual();
        }
    }
}
