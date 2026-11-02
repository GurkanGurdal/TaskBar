using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Ses tuşlarının göstergesi: Windows'unkinin yerine, saat çipinin kutucuklarıyla aynı boyda cam kutucuk.
/// Düzey kutucuğun kenarını saat yönünde dolaşan çizgi, ortada sayı; sessizde çizgi geri çekilir, sayının yerine simge gelir.
/// Yeri çubuğa göre (BarWindow.PlaceVolumeChip), çubuk açılıp kapanınca yeni yerine kayar. Odak almaz, tıklanmaz.
/// Sınırdayken basınca kutucuk bir an şişer. Yalnızca hareket sürerken çizer.
/// </summary>
public partial class VolumeChip : UserControl
{
    private const int HoldMs = 1500;
    private const int MoveMs = 240;
    private const double MaxStepSeconds = 1d / 30;
    private const double RestRatio = 0.002;

    /// <summary>Çizginin kutucuk kenarına uzaklığı; köşesi kutucuğunkiyle aynı merkezden döner.</summary>
    private const double RingInsetRatio = 0.105;
    private const double RingThicknessRatio = 0.065;
    private const double NumberFontRatio = 0.4;
    private const double MutedFontRatio = 0.44;
    private const double MutedBoldRatio = 0.012;

    private const double LevelStiffness = 340;
    private const double LevelDamping = 28;
    private const double AppearStiffness = 420;
    private const double AppearDamping = 26;
    private const double HideStiffness = 420;
    private const double HideDamping = 42;
    private const double PulseStiffness = 520;
    private const double PulseDamping = 16;
    private const double PulseKick = 1.4;
    private const double MaxPulse = 0.09;

    private const double FromScale = 0.72;
    private const double FadeSpan = 0.4;
    private const double HiddenAt = 0.04;

    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(HoldMs) };
    private readonly Stopwatch _clock = new();
    private double _perimeter = 1;
    private double _ringThickness = 3;
    private Spring _level;
    private Spring _appear;
    private Spring _pulse;
    private bool _moving;
    private bool _shown;
    private bool _hiding;
    private bool _glassReady;

    public VolumeChip()
    {
        InitializeComponent();
        _holdTimer.Tick += HoldTimer_Tick;
    }

    public event Action? Dismissed;

    /// <summary>Yeni yerine kaydı, arkası yeniden alınıyor.</summary>
    public event Action? Slid;

    public bool IsHiding => _hiding;

    /// <param name="thickness">Adanın kalınlığı: yazı ve çizgi ona göre ölçeklenir.</param>
    public void SetSize(Size size, double thickness, double cornerRadius)
    {
        Card.Width = size.Width;
        Card.Height = size.Height;
        Card.CornerRadius = new CornerRadius(cornerRadius);
        Number.FontSize = Math.Round(thickness * NumberFontRatio);
        CenterDigits();
        DrawMutedMark(size, thickness);

        _ringThickness = Math.Max(2, Math.Round(thickness * RingThicknessRatio));
        double inset = Math.Round(thickness * RingInsetRatio);
        double radius = Math.Max(cornerRadius - inset, 1);
        var ringArea = new Rect(inset, inset, size.Width - 2 * inset, size.Height - 2 * inset);
        _perimeter = 2 * (ringArea.Width - 2 * radius) + 2 * (ringArea.Height - 2 * radius) + 2 * Math.PI * radius;

        Geometry ring = RingShape(ringArea, radius);
        Track.Data = ring;
        Ring.Data = ring;
        Track.StrokeThickness = _ringThickness;
        Ring.StrokeThickness = _ringThickness;
        ApplyLevel();
    }

    /// <summary>Rakamların kuyruğu yok, satır kutusunun altında dururlar: kutu ortalanınca sayı kutucukta aşağıda görünür.</summary>
    private void CenterDigits()
    {
        var text = new FormattedText(
            "0",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(Number.FontFamily, Number.FontStyle, Number.FontWeight, Number.FontStretch),
            Number.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Rect ink = text.BuildGeometry(new Point()).Bounds;
        double drop = (ink.Top + ink.Bottom) / 2 - text.Height / 2;
        Number.Margin = new Thickness(0, 0, 0, 2 * drop);
    }

    /// <summary>Simge yazı tipinin kalın hali yok, kalın rakamların yanında cılız kalıyordu: simge şekle çevrilip dış çizgiyle kalınlaştırılır.</summary>
    private void DrawMutedMark(Size size, double thickness)
    {
        var text = new FormattedText(
            VolumeIcon.MutedGlyph,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Math.Round(thickness * MutedFontRatio),
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Geometry mark = text.BuildGeometry(new Point());
        Rect bounds = mark.Bounds;
        mark.Transform = new TranslateTransform(
            size.Width / 2 - (bounds.Left + bounds.Right) / 2,
            size.Height / 2 - (bounds.Top + bounds.Bottom) / 2);
        mark.Freeze();
        MutedMark.Data = mark;
        MutedMark.StrokeThickness = thickness * MutedBoldRatio;
    }

    /// <summary>Basışı gösterir. Kutucuk zaten açıksa yerinde kalır, süresi baştan başlar.</summary>
    /// <param name="changed">Basış bir şey değiştirdi; değiştirmediyse sınırdadır, kutucuk şişer.</param>
    internal void Present(AudioEndpoint audio, bool changed)
    {
        bool muted = audio.Muted;
        float level = audio.Level;

        Number.Text = Math.Round(level * 100).ToString(CultureInfo.CurrentCulture);
        Number.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        MutedMark.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        _level.Target = muted ? 0 : level;

        _holdTimer.Stop();
        _holdTimer.Start();

        if (!_shown)
        {
            // Kutucuk henüz yok: çizgi yerinde başlar.
            _level.Jump();
            ApplyLevel();
            return;
        }

        if (!changed)
        {
            _pulse.Kick(PulseKick);
        }

        _hiding = false;

        // Arkası henüz alınmadıysa kutucuk, görüntü gelince belirir.
        if (_glassReady)
        {
            _appear.Target = 1;
        }
        StartMoving();
    }

    /// <summary>Popup açıldı: arkası alınınca yaylanarak belirir. Dönüşüm camı etkilemez, yeniden alınmaz.</summary>
    public async Task RevealAsync()
    {
        _shown = true;
        _hiding = false;
        _glassReady = false;
        _appear.Target = 0;
        _appear.Jump();
        _pulse.Target = 0;
        _pulse.Jump();
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Card.Opacity = 0;

        await Card.RefreshGlassAsync(animateTint: false, isHidden: true);

        if (!_shown)
        {
            return;
        }

        _glassReady = true;
        _appear.Target = 1;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _appear.Jump();
            ApplyAppearance();
            return;
        }

        ApplyAppearance();
        StartMoving();
    }

    /// <summary>Yerleşim yeni yerde; kutucuk eski yerinden oraya kayar.</summary>
    public void SlideFrom(Vector fromOldPlace)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            FinishSlide();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(MoveMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Önceki kayma sürüyorsa bulunduğu yerden başlar.
        var slideY = new DoubleAnimation(Slide.Y + fromOldPlace.Y, 0, duration) { EasingFunction = ease };
        slideY.Completed += (_, _) => FinishSlide();
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Slide.X + fromOldPlace.X, 0, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    private void FinishSlide()
    {
        if (_shown && !_hiding)
        {
            _ = Card.RefreshGlassAsync(animateTint: true);
        }
        Slid?.Invoke();
    }

    private void HoldTimer_Tick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        _hiding = true;
        _appear.Target = 0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Finish();
            return;
        }
        StartMoving();
    }

    private void Finish()
    {
        _hiding = false;
        _shown = false;
        Card.Opacity = 0;
        Dismissed?.Invoke();
    }

    private void StartMoving()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _level.Jump();
            _pulse.Jump();
            ApplyLevel();
            ApplyAppearance();
            return;
        }

        if (!_moving)
        {
            _moving = true;
            _clock.Restart();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        double step = Math.Min(_clock.Elapsed.TotalSeconds, MaxStepSeconds);
        _clock.Restart();

        bool moving = _level.Advance(step, LevelStiffness, LevelDamping, RestRatio)
            | _pulse.Advance(step, PulseStiffness, PulseDamping, RestRatio)
            | (_hiding
                ? _appear.Advance(step, HideStiffness, HideDamping, RestRatio)
                : _appear.Advance(step, AppearStiffness, AppearDamping, RestRatio));

        if (_hiding && _appear.Value <= HiddenAt)
        {
            StopMoving();
            Finish();
            return;
        }

        ApplyLevel();
        ApplyAppearance();

        if (!moving)
        {
            StopMoving();
        }
    }

    private void StopMoving()
    {
        _moving = false;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
    }

    private void ApplyAppearance()
    {
        double appear = Math.Max(_appear.Value, 0);
        double scale = FromScale + (1 - FromScale) * appear + Math.Clamp(_pulse.Value, 0, MaxPulse);
        Pop.ScaleX = scale;
        Pop.ScaleY = scale;
        Card.Opacity = Math.Clamp(appear / FadeSpan, 0, 1);
    }

    /// <summary>Kesik çizgi uzunlukları çizgi kalınlığı cinsinden. Sıfırda yuvarlak uç nokta olarak kalmasın diye gizlenir.</summary>
    private void ApplyLevel()
    {
        double level = Math.Clamp(_level.Value, 0, 1);
        Ring.Visibility = level <= RestRatio ? Visibility.Hidden : Visibility.Visible;
        Ring.StrokeDashArray = new DoubleCollection { level * _perimeter / _ringThickness, 2 * _perimeter / _ringThickness };
    }

    /// <summary>Üst ortadan başlayıp saat yönünde kutucuğun kenarını dolaşır.</summary>
    private static Geometry RingShape(Rect area, double radius)
    {
        double left = area.Left;
        double top = area.Top;
        double right = area.Right;
        double bottom = area.Bottom;
        double middle = (left + right) / 2;
        var corner = new Size(radius, radius);

        var shape = new StreamGeometry();
        using (StreamGeometryContext context = shape.Open())
        {
            context.BeginFigure(new Point(middle, top), isFilled: false, isClosed: false);
            context.LineTo(new Point(right - radius, top), true, true);
            context.ArcTo(new Point(right, top + radius), corner, 0, false, SweepDirection.Clockwise, true, true);
            context.LineTo(new Point(right, bottom - radius), true, true);
            context.ArcTo(new Point(right - radius, bottom), corner, 0, false, SweepDirection.Clockwise, true, true);
            context.LineTo(new Point(left + radius, bottom), true, true);
            context.ArcTo(new Point(left, bottom - radius), corner, 0, false, SweepDirection.Clockwise, true, true);
            context.LineTo(new Point(left, top + radius), true, true);
            context.ArcTo(new Point(left + radius, top), corner, 0, false, SweepDirection.Clockwise, true, true);
            context.LineTo(new Point(middle, top), true, true);
        }
        shape.Freeze();
        return shape;
    }
}
