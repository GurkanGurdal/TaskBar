using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Adayla aynı camdan açılır kart. Görünüm Theme.xaml'da; arkasındaki ekranı kendi alır.
/// Görüntü hazır olana dek görünmez kalır (<see cref="UIElement.Opacity"/> stilde 0), açan taraf sonra belirginleştirir.
/// </summary>
public class GlassCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassCard), new PropertyMetadata(new CornerRadius(16)));

    private const int PopupFadeMs = 110;
    private const double DefaultBlurRadius = 9;

    /// <summary>Düğmenin üzerindeyken alınan görüntü bu süre içinde açılan kartta yeniden alınmaz.</summary>
    private const int PreparedGlassMs = 1500;

    private readonly ImageBrush _glassBrush = new() { Stretch = Stretch.Fill };
    private readonly SolidColorBrush _tintBrush = new(GlassTint.Rest);
    private Grid? _shadowHost;
    private Border? _shadowBorder;
    private Image? _shadowImage;
    private Size _bakedSize = Size.Empty;
    private Grid? _glassHost;
    private Border? _fallback;
    private Border? _tint;
    private Border? _sheen;
    private WriteableBitmap? _bitmap;
    private Color _tintTarget = GlassTint.Rest;
    private bool _busy;
    private bool _refreshAgain;
    private bool _recomposeToggle;
    private System.Drawing.Rectangle _restArea;
    private System.Drawing.Rectangle _preparedArea;
    private long _preparedAt;

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double BlurRadius { get; set; } = DefaultBlurRadius;

    /// <summary>İpuçları ve sağ tık menüleri de bu camdan (şablonları Theme.xaml'da). Açan taraf olmadığı için belirme burada.</summary>
    public static void AttachToPopups()
    {
        EventManager.RegisterClassHandler(typeof(ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler(Popup_Opened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, new RoutedEventHandler(Popup_Opened));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(Popup_Opened));
    }

    private static async void Popup_Opened(object sender, RoutedEventArgs e)
    {
        // Alt menünün olayı üst menülerden de geçer.
        if (e.OriginalSource != sender)
        {
            return;
        }

        var host = (Control)sender;
        host.ApplyTemplate();
        if (host.Template?.FindName(host is MenuItem ? "SubCard" : "Card", host) is not GlassCard card)
        {
            return;
        }

        // Aynı kart yeniden açıldıysa eski görüntü görünmesin.
        card.BeginAnimation(OpacityProperty, null);
        await card.RefreshGlassAsync(animateTint: false, isHidden: true);

        bool isOpen = host switch
        {
            ToolTip tip => tip.IsOpen,
            ContextMenu menu => menu.IsOpen,
            MenuItem item => item.IsSubmenuOpen,
            _ => false
        };
        if (isOpen)
        {
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(PopupFadeMs)));
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _shadowHost = (Grid)GetTemplateChild("PART_ShadowHost");
        _shadowBorder = (Border)GetTemplateChild("PART_ShadowBorder");
        _shadowImage = (Image)GetTemplateChild("PART_ShadowImage");
        _glassHost = (Grid)GetTemplateChild("PART_GlassHost");
        _fallback = (Border)GetTemplateChild("PART_Fallback");
        _glassHost.Visibility = ThemeManager.UsesGlass ? Visibility.Visible : Visibility.Collapsed;
        var glassImage = (Rectangle)GetTemplateChild("PART_GlassImage");
        glassImage.Fill = _glassBrush;

        // Görüntü kartın dışına taşar: kenar kırılmasının en dış şeridi görünmesin.
        glassImage.Margin = new Thickness(-BlurRadius);
        _tint = (Border)GetTemplateChild("PART_Tint");
        _tint.Background = _tintBrush;
        _sheen = (Border)GetTemplateChild("PART_Sheen");
    }

    /// <summary>
    /// Kartın yalnızca bir bölümü görünür, geri kalanı yokmuş gibi. Yerleşim ve cam yerinde kalır: görüntü gerilmez,
    /// kartın boyu değişmediği için arkası da yeniden alınmaz. Eksi değer kartı o yönde taşırır.
    /// </summary>
    /// <param name="left">Soldan gizlenen genişlik.</param>
    /// <param name="right">Sağdan gizlenen genişlik.</param>
    public void SetReveal(double left, double right)
    {
        if (_shadowHost == null || _glassHost == null || _shadowBorder == null || _shadowImage == null)
        {
            return;
        }

        double radius = CornerRadius.TopLeft;
        double bleed = -_shadowHost.Margin.Left;
        Size size = RenderSize;
        double width = Math.Max(0, size.Width - left - right);

        var outer = new RectangleGeometry(new Rect(0, 0, size.Width + 2 * bleed, size.Height + 2 * bleed));
        var inner = new RectangleGeometry(new Rect(bleed + left, bleed, width, size.Height), radius, radius);
        _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        _glassHost.Clip = new RectangleGeometry(new Rect(left, 0, width, size.Height), radius, radius);

        _shadowBorder.Margin = new Thickness(bleed + left, bleed, bleed + right, bleed);
        _shadowBorder.Visibility = Visibility.Visible;
        _shadowImage.Visibility = Visibility.Collapsed;
        _fallback!.Margin = new Thickness(left, 0, right, 0);
        _tint!.Margin = new Thickness(left, 0, right, 0);
        _sheen!.Margin = new Thickness(left + 1, 1, right + 1, 1);
    }

    public void ClearReveal()
    {
        if (_shadowHost == null || _glassHost == null || _shadowBorder == null || _shadowImage == null)
        {
            return;
        }

        double bleed = -_shadowHost.Margin.Left;
        _shadowBorder.Margin = new Thickness(bleed);
        _fallback!.Margin = default;
        _tint!.Margin = default;
        _sheen!.Margin = new Thickness(1);
        ClipToCard(RenderSize);

        // Resim hâlâ bu boydaysa canlı gölgeye gerek yok.
        if (_bakedSize == RenderSize)
        {
            _shadowBorder.Visibility = Visibility.Hidden;
            _shadowImage.Visibility = Visibility.Visible;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_shadowHost == null || _glassHost == null)
        {
            return;
        }

        ClipToCard(sizeInfo.NewSize);

        // Resim eski boyda kaldı; yeniden çizilene dek canlı gölge.
        if (_shadowBorder != null && _shadowImage != null)
        {
            _shadowBorder.Visibility = Visibility.Visible;
            _shadowImage.Visibility = Visibility.Collapsed;
            _bakedSize = Size.Empty;
        }
    }

    private void ClipToCard(Size size)
    {
        // Gölge yalnızca kartın dışında: içine düşerse camı karartır.
        double radius = CornerRadius.TopLeft;
        double bleed = -_shadowHost!.Margin.Left;
        var outer = new RectangleGeometry(new Rect(0, 0, size.Width + 2 * bleed, size.Height + 2 * bleed));
        var inner = new RectangleGeometry(new Rect(bleed, bleed, size.Width, size.Height), radius, radius);
        _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        _glassHost!.Clip = new RectangleGeometry(new Rect(size), radius, radius);
    }

    /// <summary>Arkadaki ekranı alıp cama yansıtır. Kart açıkken çağrılmalı.</summary>
    /// <param name="animateTint">Kart görünürken ton yumuşak geçsin; açılırken hemen doğru olsun.</param>
    /// <param name="isHidden">Kart henüz görünmüyor: görüntüye karışamaz, pencereyi hariç tutup beklemeye gerek yok.</param>
    public async Task RefreshGlassAsync(bool animateTint, bool isHidden = false)
    {
        if (_busy)
        {
            // Kart yer değiştirmiş olabilir; bitince yeniden al.
            _refreshAgain = true;
            return;
        }

        _busy = true;
        try
        {
            // Kartın konumu ilk açılışta ancak yerleşimden sonra kesinleşir.
            await Dispatcher.Yield(DispatcherPriority.Loaded);

            if (PresentationSource.FromVisual(this) is not HwndSource source || !TryGetRestRect(out System.Drawing.Rectangle area))
            {
                return;
            }

            BakeShadow();
            _restArea = area;

            if (!ThemeManager.UsesGlass)
            {
                return;
            }

            // Hariç tutma ekran görüntülerine de yansır; hazır görüntü varken açılış sırasında hiç gerekmesin.
            bool prepared = isHidden && area == _preparedArea && Environment.TickCount64 - _preparedAt < PreparedGlassMs;
            _preparedArea = System.Drawing.Rectangle.Empty;
            if (prepared)
            {
                return;
            }

            IntPtr window = isHidden ? IntPtr.Zero : source.Handle;
            double blur = BlurRadius;
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            GlassFrame? frame = await Task.Run(() => GlassCapture.Capture(area, window) is { } captured ? GlassCapture.Blur(captured, blur, dpiScale) : null);
            if (frame != null)
            {
                Show(frame, ref _bitmap, _glassBrush);
                ApplyTint(frame.Luminance, animateTint);
            }

            // Ada için de geçerli: hariç tutmanın kalkması yeni kare ister.
            _recomposeToggle = !_recomposeToggle;
            _glassHost!.Opacity = _recomposeToggle ? 0.999 : 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran alınamıyor; sonraki denemede düzelir.
        }
        finally
        {
            _busy = false;

            // Görüntü hiç alınamadıysa (eski Windows) kart okunabilir kalsın.
            _fallback!.Visibility = _bitmap == null && ThemeManager.UsesGlass ? Visibility.Visible : Visibility.Collapsed;

            if (_refreshAgain)
            {
                _refreshAgain = false;
                _ = RefreshGlassAsync(animateTint);
            }
        }
    }

    /// <summary>Kart kapalıyken arkasını önceden alır: basışta hazır olsun. Yeri son açılıştan bilinir.</summary>
    /// <param name="shift">Düğme son açılıştan beri bu kadar kaydı.</param>
    public async Task PrepareAsync(Vector shift)
    {
        if (!ThemeManager.UsesGlass || _busy || _restArea.IsEmpty || Visibility != Visibility.Visible)
        {
            return;
        }

        _busy = true;
        try
        {
            System.Drawing.Rectangle area = _restArea;
            area.Offset((int)Math.Round(shift.X), (int)Math.Round(shift.Y));

            double blur = BlurRadius;
            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            GlassFrame? frame = await Task.Run(() => GlassCapture.Capture(area, IntPtr.Zero) is { } captured ? GlassCapture.Blur(captured, blur, dpiScale) : null);
            if (frame != null)
            {
                Show(frame, ref _bitmap, _glassBrush);
                ApplyTint(frame.Luminance, animate: false);
                _preparedArea = area;
                _preparedAt = Environment.TickCount64;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran alınamıyor; açılışta yeniden denenir.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Gölge bir kez resme çizilir. Canlı DropShadowEffect, kartın görüntüsü her alındığında yazılımla
    /// yeniden hesaplanıyor; açılış ve kapanışın başlamasını geciktiriyordu.
    /// </summary>
    private void BakeShadow()
    {
        if (_shadowHost == null || _shadowBorder == null || _shadowImage == null || _bakedSize == RenderSize)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var image = new RenderTargetBitmap(
            (int)Math.Ceiling(_shadowHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(_shadowHost.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        _shadowBorder.Visibility = Visibility.Visible;
        image.Render(_shadowBorder);
        image.Freeze();

        _shadowImage.Source = image;
        _shadowImage.Visibility = Visibility.Visible;
        _shadowBorder.Visibility = Visibility.Hidden;
        _bakedSize = RenderSize;
    }

    /// <summary>Görüntüyü fırçaya yazar; boyut değişmediyse aynı bitmap kullanılır.</summary>
    internal static void Show(GlassFrame frame, ref WriteableBitmap? bitmap, ImageBrush brush)
    {
        if (bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            brush.ImageSource = bitmap;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
    }

    private void ApplyTint(double luminance, bool animate)
    {
        Color target = GlassTint.For(luminance);
        if (GlassTint.IsNear(target, _tintTarget))
        {
            return;
        }

        _tintTarget = target;
        if (animate)
        {
            _tintBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(350)));
        }
        else
        {
            _tintBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _tintBrush.Color = target;
        }
    }

    private bool TryGetRestRect(out System.Drawing.Rectangle area)
    {
        area = System.Drawing.Rectangle.Empty;
        if (VisualTreeHelper.GetParent(this) is not Visual parent)
        {
            return false;
        }

        // Dönüşümden bağımsız konumu üst öğeden hesapla.
        Vector offset = VisualTreeHelper.GetOffset(this);
        Point origin = parent.PointToScreen(new Point(offset.X, offset.Y));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        area = new System.Drawing.Rectangle(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Round(ActualWidth * scale),
            (int)Math.Round(ActualHeight * scale));
        return !area.IsEmpty;
    }
}
