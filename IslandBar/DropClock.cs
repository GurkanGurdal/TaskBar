using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IslandBar;

/// <summary>
/// Saat çipinin içi: ada iki cam damlaya ayrılır (Liquid Glass), birinde saat, öbüründe dakika. Dakika damlasında sıvı var:
/// saat başında boş, sonuna doğru dolu. Damlaların şekli <see cref="DropShape"/>, camı adanın katmanları (BarWindow).
/// Yalnızca dakika değişince oynar: rakam yuvarlanır, sıvı yükselip çalkalanır, sonra durur.
/// </summary>
internal sealed class DropClock : FrameworkElement
{
    private const double FontRatio = 0.56;
    private const double DigitSpacingRatio = 0.125;
    private const double TilePaddingRatio = 0.2;
    private const double TallTileRatio = 0.82;
    private const double GapRatio = 0.14;
    private const double GrowFrom = 0.72;
    private const double MinuteAlpha = 0.88;
    private const double LiquidAlpha = 0.17;
    private const double SurfaceAlpha = 0.26;
    private const double RollDistance = 0.72;
    private const int RollMs = 460;
    private const int RollStaggerMs = 70;
    private const int LiquidMs = 1300;
    private const double WaveRatio = 0.05;
    private const double RestWave = 0.42;
    private const double WaveStep = 2;

    private readonly Brush _hourBrush = Frozen(255);
    private readonly Brush _minuteBrush = Frozen((byte)(255 * MinuteAlpha));
    private readonly Brush _liquidBrush = Frozen((byte)(255 * LiquidAlpha));
    private readonly Pen _surfacePen = FrozenPen();
    private readonly DrawingVisual _liquid = new();
    private readonly DrawingVisual _digits = new();
    private readonly Stopwatch _tick = new();
    private string _hours = string.Empty;
    private string _minutes = string.Empty;
    private string _oldHours = string.Empty;
    private string _oldMinutes = string.Empty;
    private double _level;
    private double _oldLevel;
    private double _thickness;
    private double _radius;
    private bool _isTall;
    private double _fontSize;
    private double[] _hourAdvances = [];
    private double[] _minuteAdvances = [];
    private double _digitSpacing;
    private double _baseline;
    private Typeface? _hourFace;
    private Typeface? _minuteFace;
    private double _gap;
    private double _digitsPresence;
    private double _liquidPresence;
    private bool _isAnimating;

    public DropClock()
    {
        if (ThemeManager.UsesGlass)
        {
            _digits.Effect = FrozenShadow();
        }
        AddVisualChild(_liquid);
        AddVisualChild(_digits);
    }

    public double TileLength { get; private set; }

    public double RestGap { get; private set; }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _liquid : _digits;

    public void Configure(double thickness, double radius, bool isTall)
    {
        if (thickness == _thickness && isTall == _isTall)
        {
            return;
        }

        _thickness = thickness;
        _radius = radius;
        _isTall = isTall;
        _fontSize = Math.Round(thickness * FontRatio);
        RestGap = Math.Round(thickness * GapRatio);

        var family = (FontFamily)FindResource("ClockFont");
        _hourFace = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _minuteFace = new Typeface(family, FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

        // Rakamlar yazı kutusuna göre değil mürekkebine göre ortalanır: kuyrukları olmadığı için kutuda aşağıda dururlar.
        FormattedText zero = Text("0", _hourFace, _hourBrush);
        Rect ink = zero.BuildGeometry(new Point()).Bounds;
        _hourAdvances = Advances(_hourFace, _hourBrush);
        _minuteAdvances = Advances(_minuteFace, _minuteBrush);
        _digitSpacing = _fontSize * DigitSpacingRatio;
        _baseline = (ink.Top + ink.Bottom) / 2;

        TileLength = isTall
            ? Math.Round(thickness * TallTileRatio)
            : Math.Max(thickness, Math.Ceiling(2 * _hourAdvances.Max() + _digitSpacing + 2 * thickness * TilePaddingRatio));
        Render();
    }

    /// <summary>Bölge ayarından gelen metinler; yalnızca rakamları alınır (ÖS / ÖÖ damlaya sığmaz).</summary>
    public void SetTime(string hours, string minutes)
    {
        hours = new string(hours.Where(char.IsDigit).ToArray());
        minutes = new string(minutes.Where(char.IsDigit).ToArray());
        if (hours == _hours && minutes == _minutes)
        {
            return;
        }

        bool first = _hours.Length == 0;
        _oldHours = _hours;
        _oldMinutes = _minutes;
        _oldLevel = _level;
        _hours = hours;
        _minutes = minutes;
        _level = int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value / 60d : 0;

        if (first || _digitsPresence <= 0 || !SystemParameters.ClientAreaAnimation)
        {
            _oldHours = hours;
            _oldMinutes = minutes;
            _oldLevel = _level;
            Render();
            return;
        }

        _tick.Restart();
        if (!_isAnimating)
        {
            _isAnimating = true;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    /// <param name="gap">Damlaların arası.</param>
    /// <param name="digits">0 görünmez, 1 tam; rakamlar büyüyerek gelir.</param>
    /// <param name="liquid">0 boş, 1 bugünkü düzey: ayrılma bitince sıvı dolar.</param>
    public void Update(double gap, double digits, double liquid)
    {
        _gap = gap;
        _digitsPresence = digits;
        _liquidPresence = liquid;
        Render();
    }

    protected override Size MeasureOverride(Size availableSize) => default;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Render();
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        Render();

        if (_tick.ElapsedMilliseconds > Math.Max(LiquidMs, RollMs + RollStaggerMs))
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _isAnimating = false;
            _oldHours = _hours;
            _oldMinutes = _minutes;
            _oldLevel = _level;
            Render();
        }
    }

    private void Render()
    {
        using (DrawingContext drawing = _liquid.RenderOpen())
        {
            if (_liquidPresence > 0 && TileLength > 0)
            {
                DrawLiquid(drawing);
            }
        }

        using (DrawingContext drawing = _digits.RenderOpen())
        {
            if (_digitsPresence > 0 && TileLength > 0)
            {
                DrawNumber(drawing, Tile(0), _oldHours, _hours, _hourFace!, _hourBrush, _hourAdvances);
                DrawNumber(drawing, Tile(1), _oldMinutes, _minutes, _minuteFace!, _minuteBrush, _minuteAdvances);
            }
        }
    }

    /// <summary>Damlanın ekrandaki yeri; ortası adanın ortasından ayrılma kadar uzakta.</summary>
    private Rect Tile(int index)
    {
        double offset = (TileLength + _gap) / 2 * (index == 0 ? -1 : 1);
        return _isTall
            ? new Rect((ActualWidth - _thickness) / 2, ActualHeight / 2 + offset - TileLength / 2, _thickness, TileLength)
            : new Rect(ActualWidth / 2 + offset - TileLength / 2, (ActualHeight - _thickness) / 2, TileLength, _thickness);
    }

    private void DrawNumber(DrawingContext drawing, Rect tile, string oldText, string text, Typeface face, Brush brush, double[] advances)
    {
        double scale = GrowFrom + (1 - GrowFrom) * _digitsPresence;
        Point center = new(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2);

        drawing.PushOpacity(_digitsPresence);
        drawing.PushClip(new RectangleGeometry(tile, _radius, _radius));
        drawing.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));

        double left = center.X - NumberWidth(text, advances) / 2;
        double oldLeft = center.X - NumberWidth(oldText, advances) / 2;
        double elapsed = _isAnimating ? _tick.Elapsed.TotalMilliseconds : double.MaxValue;
        for (int i = 0; i < text.Length; i++)
        {
            // Sağdaki önce döner; aynı kalan rakam yerinde durur.
            int fromRight = text.Length - 1 - i;
            bool same = text.Length == oldText.Length && text[i] == oldText[i];
            double roll = same || !_isAnimating ? 1 : Math.Clamp((elapsed - fromRight * RollStaggerMs) / RollMs, 0, 1);
            double eased = 1 - Math.Pow(1 - roll, 3);

            if (roll < 1 && i < oldText.Length)
            {
                DrawDigit(drawing, oldText[i], oldLeft, center.Y - eased * tile.Height * RollDistance, 1 - eased, face, brush);
            }

            DrawDigit(drawing, text[i], left, center.Y + (1 - eased) * tile.Height * RollDistance, eased, face, brush);

            left += Advance(text[i], advances) + _digitSpacing;
            if (i < oldText.Length)
            {
                oldLeft += Advance(oldText[i], advances) + _digitSpacing;
            }
        }

        drawing.Pop();
        drawing.Pop();
        drawing.Pop();
    }

    /// <summary>Rakamlar eşit genişlikte değil (1 dar): sabit hücrede 1'in yanı boş kalıyor, çubuktaki saatle de farklı duruyordu.</summary>
    private double[] Advances(Typeface face, Brush brush)
    {
        return Enumerable.Range(0, 10)
            .Select(digit => Text(digit.ToString(CultureInfo.InvariantCulture), face, brush).WidthIncludingTrailingWhitespace)
            .ToArray();
    }

    private double NumberWidth(string text, double[] advances)
    {
        return text.Sum(digit => Advance(digit, advances)) + Math.Max(0, text.Length - 1) * _digitSpacing;
    }

    private static double Advance(char digit, double[] advances)
    {
        return advances[Math.Max(0, CharUnicodeInfo.GetDecimalDigitValue(digit))];
    }

    private void DrawDigit(DrawingContext drawing, char digit, double left, double centerY, double opacity, Typeface face, Brush brush)
    {
        if (opacity <= 0)
        {
            return;
        }

        drawing.PushOpacity(opacity);
        drawing.DrawText(Text(digit.ToString(), face, brush), new Point(left, centerY - _baseline));
        drawing.Pop();
    }

    private void DrawLiquid(DrawingContext drawing)
    {
        Rect tile = Tile(1);
        double elapsed = _isAnimating ? _tick.Elapsed.TotalMilliseconds : LiquidMs;
        double settle = Math.Clamp(elapsed / LiquidMs, 0, 1);
        double level = _oldLevel + (_level - _oldLevel) * (1 - Math.Pow(1 - settle, 3));

        // Ayrılma bitince dolar. Dakika değişince de, yerine otururken de aynı dalga: başta güçlü, gittikçe söner.
        double fill = 1 - Math.Pow(1 - _liquidPresence, 2);
        // Dururken de düz çizgi değil, hafif kavisli: yoksa sıvı değil kartın ikiye bölünmüşü gibi okunuyor.
        double wave = RestWave + (1 - settle) * (1 - settle) + Math.Sin(Math.PI * _liquidPresence) * 0.7;
        double amplitude = _thickness * WaveRatio * wave;
        double phase = elapsed / 110 + _liquidPresence * 5;
        double surface = tile.Bottom - tile.Height * level * fill;

        var fillShape = new StreamGeometry();
        var line = new StreamGeometry();
        using (StreamGeometryContext body = fillShape.Open())
        using (StreamGeometryContext top = line.Open())
        {
            Point start = new(tile.Left, SurfaceAt(tile.Left, surface, amplitude, phase));
            body.BeginFigure(start, true, true);
            top.BeginFigure(start, false, false);
            double step = amplitude > 0.05 ? WaveStep : tile.Width;
            for (double x = tile.Left + step; x < tile.Right + step; x += step)
            {
                var point = new Point(Math.Min(x, tile.Right), SurfaceAt(x, surface, amplitude, phase));
                body.LineTo(point, false, false);
                top.LineTo(point, true, false);
            }
            body.LineTo(new Point(tile.Right, tile.Bottom), false, false);
            body.LineTo(new Point(tile.Left, tile.Bottom), false, false);
        }
        fillShape.Freeze();
        line.Freeze();

        drawing.PushClip(new RectangleGeometry(tile, _radius, _radius));
        drawing.DrawGeometry(_liquidBrush, null, fillShape);
        drawing.DrawGeometry(null, _surfacePen, line);
        drawing.Pop();
    }

    private double SurfaceAt(double x, double surface, double amplitude, double phase)
    {
        double k = 2 * Math.PI / (TileLength * 0.9);
        return surface + amplitude * (Math.Sin(x * k + phase) + 0.4 * Math.Sin(x * k * 1.8 - phase * 0.7));
    }

    private FormattedText Text(string text, Typeface face, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            face,
            _fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static SolidColorBrush Frozen(byte alpha)
    {
        Color text = ThemeManager.TextColor;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, text.R, text.G, text.B));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(Frozen((byte)(255 * SurfaceAlpha)), 1);
        pen.Freeze();
        return pen;
    }

    /// <summary>Parlak zeminde de okunsun; adadaki saatle aynı.</summary>
    private static DropShadowEffect FrozenShadow()
    {
        var shadow = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.6 };
        shadow.Freeze();
        return shadow;
    }
}

/// <summary>
/// Tek parça adadan iki damlaya giden şekil. Önce üstte ve altta çentik açılıp boyun incelir, kopunca uçlar
/// geri çekilip yuvarlak köşeye döner (metaball). Dikey çubukta eksenler yer değiştirir.
/// </summary>
internal static class DropShape
{
    /// <summary>Ayrılma ilerlemesinin bu noktasında boyun kopar.</summary>
    private const double SnapAt = 0.55;
    private const double CornerControl = 0.45;
    private const double GapOvershoot = 2.4;

    /// <summary>Kopana dek aralık yok; kopunca yerini biraz geçip geri gelir.</summary>
    public static double GapAt(double split, double restGap)
    {
        if (split <= SnapAt)
        {
            return 0;
        }

        double t = (split - SnapAt) / (1 - SnapAt) - 1;
        return restGap * (t * t * ((GapOvershoot + 1) * t + GapOvershoot) + 1);
    }

    /// <param name="inset">Şekli içeri alır: kenar çizgisi camın dışına taşmasın.</param>
    public static Geometry Build(double length, double cross, double radius, double split, double gap, double restGap, bool horizontal, double inset)
    {
        var outline = new Outline(length, cross, Math.Min(radius, cross / 2) - inset, inset, horizontal);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            if (split <= 0)
            {
                outline.Solid(context);
            }
            else if (split < SnapAt)
            {
                double t = split / SnapAt;
                outline.Necked(context, t * t, restGap);
            }
            else
            {
                double t = (split - SnapAt) / (1 - SnapAt);
                outline.Separate(context, 1 - (1 - t) * (1 - t) * (1 - t), gap, restGap);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Uzun eksen a, kısa eksen b; çizim hep yatay düşünülür, <see cref="Map"/> dikeye çevirir.</summary>
    private sealed class Outline(double length, double cross, double radius, double inset, bool horizontal)
    {
        private const double NeckNear = 0.55;
        private const double NeckFar = 0.3;

        private readonly double _a0 = inset;
        private readonly double _a1 = length - inset;
        private readonly double _b0 = inset;
        private readonly double _b1 = cross - inset;
        private readonly double _center = length / 2;
        private readonly double _middle = cross / 2;

        public void Solid(StreamGeometryContext context)
        {
            context.BeginFigure(Map(_a0 + radius, _b0), true, true);
            Line(context, _a1 - radius, _b0);
            RightEnd(context);
            Line(context, _a0 + radius, _b1);
            LeftEnd(context);
        }

        /// <summary>Çentikler üstten ve alttan ilerler; ortada ince boyun kalır.</summary>
        public void Necked(StreamGeometryContext context, double depth, double restGap)
        {
            double w = NotchWidth(restGap);
            double d = (_middle - _b0) * depth;

            context.BeginFigure(Map(_a0 + radius, _b0), true, true);
            Line(context, _center - w, _b0);
            Curve(context, _center - w * NeckNear, _b0, _center - w * NeckFar, _b0 + d, _center, _b0 + d);
            Curve(context, _center + w * NeckFar, _b0 + d, _center + w * NeckNear, _b0, _center + w, _b0);
            Line(context, _a1 - radius, _b0);
            RightEnd(context);
            Line(context, _center + w, _b1);
            Curve(context, _center + w * NeckNear, _b1, _center + w * NeckFar, _b1 - d, _center, _b1 - d);
            Curve(context, _center - w * NeckFar, _b1 - d, _center - w * NeckNear, _b1, _center - w, _b1);
            Line(context, _a0 + radius, _b1);
            LeftEnd(context);
        }

        /// <summary>İki ayrı damla. İç kenar, kopmadaki sivri uçtan normal yuvarlak köşeye geçer.</summary>
        public void Separate(StreamGeometryContext context, double relax, double gap, double restGap)
        {
            double w = NotchWidth(restGap);

            // Kopma anı: çentiğin eğrisi ortadan ikiye bölünmüş.
            Point s0 = new(_center - w, _b0);
            Point s1 = new(_center - w * NeckNear, _b0);
            Point s2 = new(_center - w * NeckFar, _middle);
            Point s3 = new(_center, _middle);
            Point s01 = Mid(s0, s1), s12 = Mid(s1, s2), s23 = Mid(s2, s3), s012 = Mid(s01, s12), s123 = Mid(s12, s23), split = Mid(s012, s123);

            // Oturmuş damla: köşe ve düz kenar.
            double inner = _center - gap / 2 - _a0;
            Point t0 = new(inner - radius, _b0);
            Point t1 = new(inner - radius * CornerControl, _b0);
            Point t2 = new(inner, _b0 + radius * CornerControl);
            Point t3 = new(inner, _b0 + radius);
            Point t4 = new(inner, _b0 + radius + (_middle - _b0 - radius) / 3);
            Point t5 = new(inner, _middle - (_middle - _b0 - radius) / 3);
            Point t6 = new(inner, _middle);

            Point[] points =
            [
                Lerp(s0, t0, relax), Lerp(s01, t1, relax), Lerp(s012, t2, relax), Lerp(split, t3, relax),
                Lerp(s123, t4, relax), Lerp(s23, t5, relax), Lerp(s3, t6, relax)
            ];

            Tile(context, points, mirrored: false);
            Tile(context, points, mirrored: true);
        }

        private void Tile(StreamGeometryContext context, Point[] p, bool mirrored)
        {
            double A(double a) => mirrored ? _a0 + _a1 - a : a;
            double Below(double b) => _b0 + _b1 - b;

            context.BeginFigure(Map(A(_a0 + radius), _b0), true, true);
            Line(context, A(p[0].X), p[0].Y);
            Curve(context, A(p[1].X), p[1].Y, A(p[2].X), p[2].Y, A(p[3].X), p[3].Y);
            Curve(context, A(p[4].X), p[4].Y, A(p[5].X), p[5].Y, A(p[6].X), p[6].Y);
            Curve(context, A(p[5].X), Below(p[5].Y), A(p[4].X), Below(p[4].Y), A(p[3].X), Below(p[3].Y));
            Curve(context, A(p[2].X), Below(p[2].Y), A(p[1].X), Below(p[1].Y), A(p[0].X), Below(p[0].Y));
            Line(context, A(_a0 + radius), _b1);
            double k = radius * CornerControl;
            Curve(context, A(_a0 + k), _b1, A(_a0), _b1 - k, A(_a0), _b1 - radius);
            Line(context, A(_a0), _b0 + radius);
            Curve(context, A(_a0), _b0 + k, A(_a0 + k), _b0, A(_a0 + radius), _b0);
        }

        private double NotchWidth(double restGap)
        {
            return Math.Max(0, Math.Min(radius + restGap / 2, _center - _a0 - radius));
        }

        private void RightEnd(StreamGeometryContext context)
        {
            double k = radius * CornerControl;
            Curve(context, _a1 - k, _b0, _a1, _b0 + k, _a1, _b0 + radius);
            Line(context, _a1, _b1 - radius);
            Curve(context, _a1, _b1 - k, _a1 - k, _b1, _a1 - radius, _b1);
        }

        private void LeftEnd(StreamGeometryContext context)
        {
            double k = radius * CornerControl;
            Curve(context, _a0 + k, _b1, _a0, _b1 - k, _a0, _b1 - radius);
            Line(context, _a0, _b0 + radius);
            Curve(context, _a0, _b0 + k, _a0 + k, _b0, _a0 + radius, _b0);
        }

        private void Line(StreamGeometryContext context, double a, double b)
        {
            context.LineTo(Map(a, b), true, true);
        }

        private void Curve(StreamGeometryContext context, double a1, double b1, double a2, double b2, double a3, double b3)
        {
            context.BezierTo(Map(a1, b1), Map(a2, b2), Map(a3, b3), true, true);
        }

        private Point Map(double a, double b) => horizontal ? new Point(a, b) : new Point(b, a);

        private static Point Mid(Point from, Point to) => Lerp(from, to, 0.5);

        private static Point Lerp(Point from, Point to, double t)
        {
            return new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
        }
    }
}
