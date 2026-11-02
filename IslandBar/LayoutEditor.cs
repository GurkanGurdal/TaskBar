using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Çalışma düzeni oluşturma modu (kullanıcının tasarımı): her ekranın kenarında çerçeve çizilir, üstte "Düzenleme modu"
/// kartı durur. Kullanıcı uygulamaları açıp dizer; kaydedince ekranda görünen uygulamalar ve yerleri bir gruba yazılır.
/// Aynı anda bir tane olur.
/// </summary>
internal static class LayoutEditor
{
    private const int CountIntervalMs = 400;
    private const int MessageHoldMs = 2500;
    private const int SavedHoldMs = 1800;

    private static readonly List<LayoutFrame> Frames = new();
    private static LayoutBanner? _banner;
    private static DispatcherTimer? _countTimer;
    private static object? _owner;
    private static long _messageUntil;

    public static bool IsActive => _banner != null;

    /// <param name="owner">Modu açan çubuk; kapanırsa mod da biter.</param>
    /// <param name="count">Şu an düzene girecek uygulama sayısı.</param>
    /// <param name="save">Düzeni yazar, grubun adını döner; düzene girecek pencere yoksa null.</param>
    public static void Start(object owner, bool bannerAtBottom, Func<int> count, Func<string?> save)
    {
        if (IsActive)
        {
            return;
        }

        _owner = owner;
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            var frame = new LayoutFrame(screen.Bounds);
            Frames.Add(frame);
            frame.Draw();
        }

        var banner = new LayoutBanner(bannerAtBottom);
        _banner = banner;
        banner.ShowCount(count());
        banner.CancelRequested += Stop;
        banner.SaveRequested += async () =>
        {
            if (save() is not { } name)
            {
                _messageUntil = Environment.TickCount64 + MessageHoldMs;
                banner.ShowNothingToSave();
                return;
            }

            _countTimer?.Stop();
            banner.ShowSaved(name);
            await Task.Delay(SavedHoldMs);
            if (_banner == banner)
            {
                Stop();
            }
        };
        banner.Appear();

        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CountIntervalMs) };
        _countTimer.Tick += (_, _) =>
        {
            if (Environment.TickCount64 >= _messageUntil)
            {
                banner.ShowCount(count());
            }
        };
        _countTimer.Start();
    }

    public static void StopFor(object owner)
    {
        if (_owner == owner)
        {
            Stop();
        }
    }

    public static void Stop()
    {
        if (_banner == null)
        {
            return;
        }

        _countTimer?.Stop();
        _countTimer = null;
        _banner.Disappear();
        _banner = null;
        foreach (LayoutFrame frame in Frames)
        {
            frame.Erase();
        }
        Frames.Clear();
        _owner = null;
    }
}

/// <summary>
/// Bir ekranın kenarındaki çerçeve: üst ortadan iki yana çizilip alt ortada birleşir, sonra durur (sürekli animasyon
/// yok). Tıklamaları geçirir, etkinleşmez. Ekranı tam kaplayan pencere tam ekran uygulaması sayılabileceği için
/// (çubuk gizlenir) her kenardan bir piksel kısa.
/// </summary>
internal sealed class LayoutFrame
{
    private const int WindowStylePopup = unchecked((int)0x80000000);
    private const int ExStyleTopmost = 0x8;
    private const int ExStyleTransparent = 0x20;
    private const int ExStyleToolWindow = 0x80;
    private const int ExStyleNoActivate = 0x8000000;
    private const int ShowNoActivate = 4;

    private const double Inset = 6;
    private const double Radius = 14;
    private const double StrokeThickness = 4;
    private const int DrawMs = 700;
    private const int EraseMs = 260;

    private readonly HwndSource _source;
    private readonly Path[] _halves;
    private readonly double _hidden;

    public LayoutFrame(System.Drawing.Rectangle bounds)
    {
        _source = new HwndSource(new HwndSourceParameters("IslandBar.LayoutFrame")
        {
            WindowStyle = WindowStylePopup,
            ExtendedWindowStyle = ExStyleTopmost | ExStyleTransparent | ExStyleToolWindow | ExStyleNoActivate,
            UsesPerPixelOpacity = true,
            TreatAsInputRoot = false,
            PositionX = bounds.X + 1,
            PositionY = bounds.Y + 1,
            Width = bounds.Width - 2,
            Height = bounds.Height - 2
        });
        _source.CompositionTarget!.BackgroundColor = Colors.Transparent;

        Matrix fromDevice = _source.CompositionTarget.TransformFromDevice;
        double width = (bounds.Width - 2) * fromDevice.M11;
        double height = (bounds.Height - 2) * fromDevice.M22;

        // Kesik çizginin tek parçası yarım çevre kadar: ofset onu baştan sona kaydırınca çizgi büyür ya da geri çekilir.
        double length = width - 2 * Inset - 2 * Radius + Math.PI * Radius + height - 2 * Inset - 2 * Radius;
        _hidden = length / StrokeThickness;
        _halves = [Half(width, height, clockwise: true), Half(width, height, clockwise: false)];

        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Effect = new DropShadowEffect
            {
                Color = (Color)Application.Current.Resources["ShadowColor"],
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.45
            }
        };
        foreach (Path half in _halves)
        {
            half.StrokeDashArray = new DoubleCollection { _hidden, _hidden };
            half.StrokeDashOffset = _hidden;
            canvas.Children.Add(half);
        }
        _source.RootVisual = canvas;
        ShowWindow(_source.Handle, ShowNoActivate);
    }

    public void Draw()
    {
        Animate(0, DrawMs, EasingMode.EaseInOut, then: null);
    }

    public void Erase()
    {
        Animate(_hidden, EraseMs, EasingMode.EaseIn, then: _source.Dispose);
    }

    private void Animate(double offset, int milliseconds, EasingMode mode, Action? then)
    {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? milliseconds : 0);
        for (int i = 0; i < _halves.Length; i++)
        {
            var animation = new DoubleAnimation(offset, duration) { EasingFunction = new CubicEase { EasingMode = mode } };
            if (i == 0 && then != null)
            {
                animation.Completed += (_, _) => then();
            }
            _halves[i].BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
        }
    }

    /// <summary>Üst ortadan başlayıp köşelerden dönerek alt ortaya inen yarım çerçeve.</summary>
    private static Path Half(double width, double height, bool clockwise)
    {
        double center = width / 2;
        double side = clockwise ? width - Inset : Inset;
        double inward = clockwise ? -Radius : Radius;
        SweepDirection sweep = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        var corner = new Size(Radius, Radius);
        double top = Inset;
        double bottom = height - Inset;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(center, top), isFilled: false, isClosed: false);
            context.LineTo(new Point(side + inward, top), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new Point(side, top + Radius), corner, 0, false, sweep, isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(side, bottom - Radius), isStroked: true, isSmoothJoin: true);
            context.ArcTo(new Point(side + inward, bottom), corner, 0, false, sweep, isStroked: true, isSmoothJoin: true);
            context.LineTo(new Point(center, bottom), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();

        return new Path
        {
            Data = geometry,
            Stroke = (Brush)Application.Current.Resources["AccentFill"],
            StrokeThickness = StrokeThickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            StrokeDashCap = PenLineCap.Flat
        };
    }

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
