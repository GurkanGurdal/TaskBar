using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace IslandBar;

/// <summary>
/// Başlat düğmesinin yüzü: çubuktaki tek dolu yüzey, renkli uygulama ikonlarından bu yüzden ayrılır. Dört cam gerçekten delik,
/// içlerinden adanın camı görünür. Üzerine gelince camlar açılır, basınca anahtar içeri göçer, menü açılınca camlar döner.
/// Yalnızca durum değişince çizer; hareketler yaydır, yarıda kesilince hızını koruyarak yeni hedefe döner.
/// </summary>
internal sealed class StartKey : FrameworkElement
{
    public static readonly DependencyProperty IsHoveredProperty = Register(nameof(IsHovered));
    public static readonly DependencyProperty IsPressedProperty = Register(nameof(IsPressed));
    public static readonly DependencyProperty IsMenuOpenProperty = Register(nameof(IsMenuOpen));

    private const double CornerRadius = 11;
    private const double PaneRatio = 0.235;
    private const double GapRatio = 0.0625;
    private const double PaneRadiusRatio = 0.25;
    private const double HoverSpread = 1.4;
    private const double OpenSpread = 1;
    private const double PressedScale = 0.88;
    private const double OpenTurn = 45;
    private const double SpreadStiffness = 520;
    private const double SpreadDamping = 22;
    private const double ScaleStiffness = 900;
    private const double ScaleDamping = 30;
    private const double TurnStiffness = 240;
    private const double TurnDamping = 17;
    private const double MaxStepSeconds = 1d / 30;

    private readonly Stopwatch _clock = new();
    private Spring _spread;
    private Spring _scale = new() { Value = 1, Target = 1 };
    private Spring _turn;
    private bool _isMoving;

    public bool IsHovered
    {
        get => (bool)GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    public bool IsPressed
    {
        get => (bool)GetValue(IsPressedProperty);
        set => SetValue(IsPressedProperty, value);
    }

    public bool IsMenuOpen
    {
        get => (bool)GetValue(IsMenuOpenProperty);
        set => SetValue(IsMenuOpenProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override void OnRender(DrawingContext drawing)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        // Dururken tam piksele oturur: yoksa camların kenarları bulanıklaşıyor.
        double pane = Math.Round(size * PaneRatio);
        double gap = Math.Max(2, Math.Round(size * GapRatio));
        double center = size / 2;
        double first = Math.Floor((size - 2 * pane - gap) / 2) - _spread.Value;
        double second = Math.Floor((size - 2 * pane - gap) / 2) + pane + gap + _spread.Value;
        double paneRadius = pane * PaneRadiusRatio;

        var panes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (double y in new[] { first, second })
        {
            foreach (double x in new[] { first, second })
            {
                panes.Children.Add(new RectangleGeometry(new Rect(x, y, pane, pane), paneRadius, paneRadius));
            }
        }
        panes.Transform = new RotateTransform(_turn.Value, center, center);

        double radius = Math.Min(CornerRadius, size / 2);
        var face = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, size, size), radius, radius),
            panes);

        drawing.PushTransform(new ScaleTransform(_scale.Value, _scale.Value, center, center));
        drawing.DrawGeometry((Brush)FindResource("AccentFill"), null, face);
        drawing.Pop();
    }

    private static DependencyProperty Register(string name)
    {
        return DependencyProperty.Register(name, typeof(bool), typeof(StartKey), new PropertyMetadata(false, (d, _) => ((StartKey)d).UpdateTargets()));
    }

    private void UpdateTargets()
    {
        _spread.Target = IsMenuOpen ? OpenSpread : IsHovered ? HoverSpread : 0;
        _scale.Target = IsPressed ? PressedScale : 1;
        _turn.Target = IsMenuOpen ? OpenTurn : 0;

        if (!SystemParameters.ClientAreaAnimation)
        {
            _spread.Jump();
            _scale.Jump();
            _turn.Jump();
            InvalidateVisual();
            return;
        }

        if (!_isMoving)
        {
            _isMoving = true;
            _clock.Restart();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        double step = Math.Min(_clock.Elapsed.TotalSeconds, MaxStepSeconds);
        _clock.Restart();

        bool moving = _spread.Advance(step, SpreadStiffness, SpreadDamping)
            | _scale.Advance(step, ScaleStiffness, ScaleDamping)
            | _turn.Advance(step, TurnStiffness, TurnDamping);
        InvalidateVisual();

        if (!moving)
        {
            _isMoving = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
        }
    }
}
