using System.Windows;
using System.Windows.Media;

namespace GrupBar;

/// <summary>
/// 24 saatlik güneş kadranı: öğle üstte, gece yarısı altta. Yay gece yarısından bu yana geçen süreyi çizer;
/// sabah sol yandan yükselir, akşam sağdan iner. Rengi günün saatine, ortası gündüz güneş, gece ay.
/// Dakikada bir yeniden çizilir, sürekli animasyon yok.
/// </summary>
public sealed class DayDial : FrameworkElement
{
    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(DateTime), typeof(DayDial),
        new FrameworkPropertyMetadata(DateTime.MinValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double SunriseHour = 6;
    private const double SunsetHour = 18.5;

    // Saate göre gökyüzü: gece çivit, şafak şeftali, gündüz gök mavisi, akşam mercan ve mor.
    private static readonly (double Hour, Color Color)[] Sky =
    {
        (0, Color.FromRgb(0x8C, 0x9B, 0xFF)),
        (5, Color.FromRgb(0x8C, 0x9B, 0xFF)),
        (6.5, Color.FromRgb(0xFF, 0x9F, 0x7A)),
        (8, Color.FromRgb(0xFF, 0xD2, 0x7A)),
        (10, Color.FromRgb(0x6F, 0xD0, 0xFF)),
        (16, Color.FromRgb(0x6F, 0xD0, 0xFF)),
        (17.5, Color.FromRgb(0xFF, 0xC5, 0x6B)),
        (19, Color.FromRgb(0xFF, 0x8A, 0x6B)),
        (20.5, Color.FromRgb(0xB4, 0x8C, 0xFF)),
        (24, Color.FromRgb(0x8C, 0x9B, 0xFF))
    };

    private static readonly Pen TrackPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))), 2));
    private static readonly Brush SunBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x6B)));
    private static readonly Brush MoonBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE7, 0xFF)));

    public DateTime Time
    {
        get => (DateTime)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - 2.5;
        double hour = Time.TimeOfDay.TotalHours;
        Color sky = SkyAt(hour);

        drawing.DrawEllipse(null, TrackPen, center, radius, radius);

        var skyBrush = new SolidColorBrush(sky);
        var arcPen = new Pen(skyBrush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        Point tip = PointOn(center, radius, hour);
        drawing.DrawGeometry(null, arcPen, Arc(center, radius, hour, tip));

        // Şu anın işareti: hafif hâleli beyaz nokta.
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x66, sky.R, sky.G, sky.B)), null, tip, 3.6, 3.6);
        drawing.DrawEllipse(Brushes.White, null, tip, 2, 2);

        double inner = Math.Max(2, radius * 0.42);
        if (hour is >= SunriseHour and < SunsetHour)
        {
            drawing.DrawEllipse(SunBrush, null, center, inner, inner);
        }
        else
        {
            drawing.DrawGeometry(MoonBrush, null, Moon(center, inner * 1.1));
        }
    }

    /// <summary>Saate karşılık gelen nokta; 0 saat altta, saat yönünde döner.</summary>
    private static Point PointOn(Point center, double radius, double hour)
    {
        double angle = (hour / 24 * 360 + 180) * Math.PI / 180;
        return new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
    }

    private static Geometry Arc(Point center, double radius, double hour, Point tip)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(PointOn(center, radius, 0), false, false);
            context.ArcTo(tip, new Size(radius, radius), 0, hour > 12, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Moon(Point center, double radius)
    {
        var disc = new EllipseGeometry(center, radius, radius);
        var bite = new EllipseGeometry(new Point(center.X + radius * 0.55, center.Y - radius * 0.3), radius * 0.85, radius * 0.85);
        return new CombinedGeometry(GeometryCombineMode.Exclude, disc, bite);
    }

    private static Color SkyAt(double hour)
    {
        for (int i = 1; i < Sky.Length; i++)
        {
            if (hour <= Sky[i].Hour)
            {
                (double fromHour, Color from) = Sky[i - 1];
                (double toHour, Color to) = Sky[i];
                double t = (hour - fromHour) / (toHour - fromHour);
                return Color.FromRgb(
                    (byte)(from.R + (to.R - from.R) * t),
                    (byte)(from.G + (to.G - from.G) * t),
                    (byte)(from.B + (to.B - from.B) * t));
            }
        }
        return Sky[^1].Color;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
