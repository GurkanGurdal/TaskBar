using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GrupBar;

/// <summary>
/// Adayla aynı camdan açılır kart. Görünüm Theme.xaml'da; arkasındaki ekranı kendi alır.
/// Görüntü hazır olana dek görünmez kalır (<see cref="UIElement.Opacity"/> stilde 0), açan taraf sonra belirginleştirir.
/// </summary>
public class GlassCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassCard), new PropertyMetadata(new CornerRadius(16)));

    private const int ToolTipFadeMs = 110;

    /// <summary>Bulanıklığın kenarda saydamlaşmaması için görüntü kartın bu kadar dışına taşar.</summary>
    private const double BlurBleedRatio = 1;

    private readonly ImageBrush _glassBrush = new() { Stretch = Stretch.Fill };
    private readonly SolidColorBrush _tintBrush = new(GlassTint.OnDark);
    private Grid? _shadowHost;
    private Grid? _glassHost;
    private Border? _fallback;
    private WriteableBitmap? _bitmap;
    private Color _tintTarget = GlassTint.OnDark;
    private bool _busy;
    private bool _refreshAgain;
    private bool _recomposeToggle;

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Boşsa şablondaki bulanıklık geçerli.</summary>
    public double? BlurRadius { get; set; }

    /// <summary>İpuçları da bu camdan (şablonu Theme.xaml'da). Açan taraf olmadığı için belirme burada.</summary>
    public static void AttachToToolTips()
    {
        EventManager.RegisterClassHandler(typeof(ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler(ToolTip_Opened));
    }

    private static async void ToolTip_Opened(object sender, RoutedEventArgs e)
    {
        var tip = (ToolTip)sender;
        tip.ApplyTemplate();
        if (tip.Template?.FindName("Card", tip) is not GlassCard card)
        {
            return;
        }

        // Aynı ipucu yeniden açıldıysa eski görüntü görünmesin.
        card.BeginAnimation(OpacityProperty, null);
        await card.RefreshGlassAsync(animateTint: false);
        if (tip.IsOpen)
        {
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(ToolTipFadeMs)));
        }
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _shadowHost = (Grid)GetTemplateChild("PART_ShadowHost");
        _glassHost = (Grid)GetTemplateChild("PART_GlassHost");
        _fallback = (Border)GetTemplateChild("PART_Fallback");
        var glassImage = (Rectangle)GetTemplateChild("PART_GlassImage");
        glassImage.Fill = _glassBrush;
        if (BlurRadius is { } radius)
        {
            glassImage.Margin = new Thickness(-radius * BlurBleedRatio);
            glassImage.Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        }
        ((Border)GetTemplateChild("PART_Tint")).Background = _tintBrush;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_shadowHost == null || _glassHost == null)
        {
            return;
        }

        // Gölge yalnızca kartın dışında: içine düşerse camı karartır.
        double radius = CornerRadius.TopLeft;
        Size size = sizeInfo.NewSize;
        double bleed = -_shadowHost.Margin.Left;
        var outer = new RectangleGeometry(new Rect(0, 0, size.Width + 2 * bleed, size.Height + 2 * bleed));
        var inner = new RectangleGeometry(new Rect(bleed, bleed, size.Width, size.Height), radius, radius);
        _shadowHost.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        _glassHost.Clip = new RectangleGeometry(new Rect(size), radius, radius);
    }

    /// <summary>Arkadaki ekranı alıp cama yansıtır. Kart açıkken çağrılmalı.</summary>
    /// <param name="animateTint">Kart görünürken ton yumuşak geçsin; açılırken hemen doğru olsun.</param>
    public async Task RefreshGlassAsync(bool animateTint)
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

            IntPtr window = source.Handle;
            GlassFrame? frame = await Task.Run(() => GlassCapture.Capture(area, window));
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
            _fallback!.Visibility = _bitmap == null ? Visibility.Visible : Visibility.Collapsed;

            if (_refreshAgain)
            {
                _refreshAgain = false;
                _ = RefreshGlassAsync(animateTint);
            }
        }
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

        // Açılış animasyonu kartı ölçekliyor; ölçeksiz konumu üst öğeden hesapla.
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
