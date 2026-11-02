using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ManagedShell.WindowsTasks;

namespace IslandBar;

/// <summary>
/// Uygulama düğmesinin altındaki gösterge (kullanıcının seçtiği "bölmeli çizgi"; önceki çizgi ve yanındaki nokta
/// beğenilmedi): pencere sayısı kadar parça, en çok dört. Tek pencerede pasifken kısa, birden çok pencerede ya da
/// etkinken tam boy; etkin uygulamada öndeki pencerenin parçası parlak ve uzun (ötekiler kısalır), tekerlekle
/// gezinirken uzunluk komşu parçaya akar. Dikkat
/// isteyen pencerede tümü kehribar: Windows'taki gibi birkaç kez yanıp söner, sonra uygulamaya geçilene kadar sabit
/// kalır (sürekli animasyon yok). Yan çubukta dik durur.
/// </summary>
public sealed class WindowMarks : FrameworkElement
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(WindowMarks), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, StateChanged));

    public static readonly DependencyProperty ActiveIndexProperty = DependencyProperty.Register(
        nameof(ActiveIndex), typeof(int), typeof(WindowMarks), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender, FocusChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ApplicationWindow.WindowState), typeof(WindowMarks),
        new FrameworkPropertyMetadata(ApplicationWindow.WindowState.Inactive, FrameworkPropertyMetadataOptions.AffectsRender, StateChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(WindowMarks), new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Görünen uzunluğun payı; durum değişince yaylanarak hedefine gider.</summary>
    private static readonly DependencyProperty ExtentProperty = DependencyProperty.Register(
        "Extent", typeof(double), typeof(WindowMarks), new FrameworkPropertyMetadata(ShortExtent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Odaktaki parçanın sırası; kayarken kesirli.</summary>
    private static readonly DependencyProperty FocusProperty = DependencyProperty.Register(
        "FocusAt", typeof(double), typeof(WindowMarks), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty EmphasisProperty = DependencyProperty.Register(
        "Emphasis", typeof(double), typeof(WindowMarks), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Odaktaki parça ötekilerden bu kadar katı uzun.</summary>
    private const double FocusBoost = 1.2;
    private const int FocusMs = 240;
    private const double Length = 16;
    private const double Thickness = 3;
    private const double Gap = 2;
    private const int MaxSegments = 4;
    private const double ShortExtent = 0.5;
    private const int GrowMs = 260;
    private const int ShrinkMs = 180;
    private const int PulseMs = 600;
    private const double PulseOpacity = 0.35;
    private const int PulseCount = 3;

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public int ActiveIndex
    {
        get => (int)GetValue(ActiveIndexProperty);
        set => SetValue(ActiveIndexProperty, value);
    }

    public ApplicationWindow.WindowState State
    {
        get => (ApplicationWindow.WindowState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    private double Extent => (double)GetValue(ExtentProperty);

    private double FocusAt => (double)GetValue(FocusProperty);

    private double Emphasis => (double)GetValue(EmphasisProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        return Orientation == Orientation.Horizontal ? new Size(Length, Thickness) : new Size(Thickness, Length);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        int segments = Math.Min(Count, MaxSegments);
        if (segments == 0)
        {
            return;
        }

        bool horizontal = Orientation == Orientation.Horizontal;
        double full = horizontal ? ActualWidth : ActualHeight;
        double length = full * Extent;

        // Öndeki pencerenin parçası uzar, ötekiler o kadar kısalır; toplam boy aynı. Odak kayarken uzunluk komşuya akar.
        var weights = new double[segments];
        for (int i = 0; i < segments; i++)
        {
            weights[i] = 1 + FocusBoost * Emphasis * Math.Max(0, 1 - Math.Abs(i - FocusAt));
        }
        double unit = (length - Gap * (segments - 1)) / weights.Sum();
        if (unit <= 0)
        {
            return;
        }

        Brush rest = Resource("MarkFill");
        Brush lit = Resource(State == ApplicationWindow.WindowState.Flashing ? "AttentionFill" : "AccentFill");
        bool active = State == ApplicationWindow.WindowState.Active;
        bool flashing = State == ApplicationWindow.WindowState.Flashing;

        double offset = (full - length) / 2;
        for (int i = 0; i < segments; i++)
        {
            double piece = unit * weights[i];
            Brush fill = flashing || (active && (segments == 1 || Math.Abs(i - FocusAt) < 0.5)) ? lit : rest;
            Rect rect = horizontal ? new Rect(offset, 0, piece, ActualHeight) : new Rect(0, offset, ActualWidth, piece);
            double radius = Math.Min(rect.Width, rect.Height) / 2;
            drawing.DrawRoundedRectangle(fill, null, rect, radius, radius);
            offset += piece + Gap;
        }
    }

    private static void FocusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WindowMarks)d).MoveFocus();
    }

    /// <summary>Odak etkin pencerenin parçasına yaylanarak gider (beşinci ve sonrası son parçada); etkin pencere yoksa uzama söner.</summary>
    private void MoveFocus()
    {
        bool animate = SystemParameters.ClientAreaAnimation && IsLoaded;
        int segments = Math.Min(Count, MaxSegments);
        bool focused = State == ApplicationWindow.WindowState.Active && segments > 1 && ActiveIndex >= 0;
        if (focused)
        {
            BeginAnimation(FocusProperty, new DoubleAnimation(Math.Min(ActiveIndex, segments - 1), TimeSpan.FromMilliseconds(animate ? FocusMs : 0))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            });
        }
        BeginAnimation(EmphasisProperty, new DoubleAnimation(focused ? 1 : 0, TimeSpan.FromMilliseconds(animate ? FocusMs : 0)));
    }

    private static void StateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var marks = (WindowMarks)d;
        marks.Animate(e.Property == StateProperty);
        marks.MoveFocus();
    }

    /// <summary>
    /// Tek pencerede pasifken kısa; birden çok pencerede parçalar sığsın diye hep tam boy. Yanıp sönme yalnızca dikkat
    /// isteği başlayınca: pencere sayısı değişince yeniden başlamaz.
    /// </summary>
    private void Animate(bool stateChanged)
    {
        bool full = State != ApplicationWindow.WindowState.Inactive || Count > 1;
        double target = full ? 1 : ShortExtent;
        bool animate = SystemParameters.ClientAreaAnimation && IsLoaded;
        var duration = TimeSpan.FromMilliseconds(animate ? (full ? GrowMs : ShrinkMs) : 0);
        BeginAnimation(ExtentProperty, new DoubleAnimation(target, duration)
        {
            EasingFunction = full ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } : null
        });

        if (!stateChanged)
        {
            return;
        }

        if (State == ApplicationWindow.WindowState.Flashing && animate)
        {
            // AutoReverse her turu 1'de bitirir: sonunda sabit kehribar kalır.
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, PulseOpacity, TimeSpan.FromMilliseconds(PulseMs))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(PulseCount)
            });
        }
        else
        {
            BeginAnimation(OpacityProperty, null);
        }
    }

    private static Brush Resource(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }
}
