using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace IslandBar;

/// <summary>Kartın ekrandaki yeri ve huninin boğazı. Pencere piksel, ağ birim cinsinden.</summary>
internal readonly record struct GenieLayout(int X, int Y, int PixelWidth, int PixelHeight, double Width, double Height, double NeckAlong, double NeckCenter, double NeckHalfWidth);

/// <summary>
/// Kart, çubuktaki düğmesinden bir huniyle akarak açılır ve kapanır: macOS'ta Genie, KDE'de Magic Lamp.
/// Kartın görüntüsü bir ağa gerilir, ağ her karede bükülür. <see cref="GenieStage"/> üzerinde, kendi iş parçacığında oynar:
/// açılışta gerçek kart görünmez bekler, kapanışta gerçek pencere hemen kapanabilir.
/// </summary>
internal sealed class GenieEffect
{
    private const int AlongSteps = 48;
    private const int CrossSteps = 8;
    private const int OpenMs = 380;
    private const int CloseMs = 300;
    private const int MinReverseMs = 60;

    /// <summary>Eğrinin bu başlangıcında kart henüz düğmenin içinde ve görünmüyor; ilk kare doğrudan görünür noktadan başlar.</summary>
    private const int OpenSkipMs = 24;

    /// <summary>Kopyanın ekrana çıkması için beklenen kare; arayüz meşgulse kare olayları üst üste gelebildiği için en az bu kadar süre de geçmeli.</summary>
    private const int ShowFrames = 2;
    private const int ShowMinMs = 20;

    private const double BendEnd = 0.5;
    private const double SlideStart = 0.08;
    private const double NeckTravelEnd = 0.2;
    private const double FunnelReach = 0.9;
    private const double FadeTail = 0.08;
    private const double OpenEasePower = 2.4;
    private const double CloseEasePower = 1.05;
    private const double MinNeckHalfWidth = 8;
    private const double DefaultNeckHalfWidth = 16;

    private readonly GenieStage _stage;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly BitmapSource _image;
    private readonly AppBarEdge _edge;
    private readonly bool _visible;
    private readonly Stopwatch _clock = new();
    private readonly Stopwatch _showClock = new();
    private readonly Action? _shown;
    private readonly Action _finished;
    private MeshGeometry3D? _mesh;
    private ImageBrush? _brush;
    private GenieWindow? _window;
    private GenieLayout _layout;
    private double _along;
    private double _cross;
    private bool _closing;
    private bool _cardReady;
    private double _position;
    private double _from;
    private double _durationMs;
    private double _skipMs;
    private bool _isShown;
    private bool _isFinishing;
    private bool _cardShown;
    private int _waitFrames;

    /// <param name="image">Kartın görüntüsü; açılışta önceki hazırlıktan kalmış olabilir, <see cref="CardReady"/> tazeler.</param>
    /// <param name="visible">Yanlışsa ekranda görünmeden oynar: kod ısınsın diye.</param>
    /// <param name="shown">Kapanışta gerçek kartı kapatmak için: bu noktada kartın kopyası üstünde görünür durumda.</param>
    /// <param name="finished">Etki bitti; açılışta gerçek kart artık gösterilebilir. Arayüz iş parçacığında çağrılır.</param>
    public GenieEffect(GenieStage stage, BitmapSource image, GenieLayout layout, AppBarEdge edge, bool opening, bool visible, Action? shown, Action finished)
    {
        _stage = stage;
        _image = image;
        _layout = layout;
        _edge = edge;
        _closing = !opening;
        _cardReady = !opening || !visible;
        _visible = visible;
        _shown = shown;
        _finished = finished;

        _position = opening ? 1 : 0;
        _from = _position;
        _durationMs = opening ? OpenMs : CloseMs;
        _skipMs = opening ? OpenSkipMs : 0;
    }

    private double Progress => Math.Clamp((_clock.Elapsed.TotalMilliseconds + _skipMs) / _durationMs, 0, 1);

    private double PositionAt(double progress)
    {
        return _closing
            ? _from + (1 - _from) * Math.Pow(progress, CloseEasePower)
            : _from * Math.Pow(1 - progress, OpenEasePower);
    }

    /// <summary>Sahne iş parçacığında çalışır.</summary>
    internal void Start()
    {
        try
        {
            _window = _stage.Rent();
            _brush = new ImageBrush(_image) { Stretch = Stretch.Fill };
            RenderOptions.SetCachingHint(_brush, CachingHint.Cache);
            _mesh = new MeshGeometry3D();
            ApplyLayout(_layout);
            BuildMesh();
            _position = PositionAt(Progress);
            Deform(_position);
            _window.Opacity = Fade(_position);
            _window.Present(_mesh, new DiffuseMaterial(_brush), _layout, _visible);

            // Oynarken yeni kartın camına girmesin.
            GlassCapture.AddTransient(_window.Handle);

            // Saat basışla başlar. Açılışta gerçek kart görünmüyor, kopyanın ekrana çıkması beklenmez;
            // kapanışta beklenen süre ilk hareketle kapanır.
            _isShown = _shown == null;
            _clock.Start();
            _showClock.Start();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
        catch (Exception)
        {
            Abort();
        }
    }

    /// <summary>Gerçek kart hazır: en güncel görüntüye geçer, animasyon bitince kart gösterilebilir.</summary>
    public void CardReady(BitmapSource image, GenieLayout layout)
    {
        _stage.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (_closing || _isFinishing || _window == null || _brush == null)
            {
                return;
            }

            _cardReady = true;
            _brush.ImageSource = image;
            if (layout != _layout)
            {
                ApplyLayout(layout);
                _window.Move(layout);
                Deform(_position);
            }
        });
    }

    /// <summary>Açılırken kart kapatıldı: bulunduğu yerden geri döner.</summary>
    public void Reverse()
    {
        _stage.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (_closing || _isFinishing)
            {
                return;
            }

            _closing = true;
            _cardReady = true;
            _from = _position;
            _durationMs = Math.Max(MinReverseMs, CloseMs * (1 - _position));
            _skipMs = 0;
            _clock.Restart();
        });
    }

    /// <summary>
    /// Kartın olduğu gibi görüntüsü; gölge payı dahil. Kapalı Popup'ın içeriği de çizilebilir, ama son açılıştaki
    /// yerleşimiyle: WPF kapalıyken ölçümü yok sayıyor. Fark, kart hazır olunca <see cref="CardReady"/> ile kapanır.
    /// </summary>
    public static BitmapSource Snapshot(FrameworkElement root, DpiScale dpi)
    {
        root.UpdateLayout();

        var image = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        image.Render(root);
        image.Freeze();
        return image;
    }

    /// <summary>Düğme, kartın penceresine göre nerede: huni oraya iner. Arayüz iş parçacığında çağrılır.</summary>
    /// <param name="origin">Kartın sol üst köşesi, ekran pikseli.</param>
    public static GenieLayout Measure(Point origin, double width, double height, FrameworkElement? button, AppBarEdge edge, DpiScale dpi)
    {
        bool vertical = edge is AppBarEdge.Left or AppBarEdge.Right;
        double alongSize = vertical ? width : height;
        double crossSize = vertical ? height : width;
        (double along, double center, double halfWidth) = (alongSize, crossSize / 2, DefaultNeckHalfWidth);

        if (button != null && button.IsVisible)
        {
            // İkisi ayrı pencerede; ortak koordinat ekran.
            Vector offset = button.PointToScreen(new Point()) - origin;
            double left = offset.X / dpi.DpiScaleX;
            double top = offset.Y / dpi.DpiScaleY;
            double right = left + button.ActualWidth;
            double bottom = top + button.ActualHeight;

            // Düğme kartın penceresi dışında kalırsa ağ pencerenin kenarına iner.
            along = edge switch
            {
                AppBarEdge.Top => alongSize - bottom,
                AppBarEdge.Left => alongSize - right,
                AppBarEdge.Right => left,
                _ => top
            };
            center = vertical ? (top + bottom) / 2 : (left + right) / 2;
            halfWidth = Math.Max(MinNeckHalfWidth, (vertical ? button.ActualHeight : button.ActualWidth) / 2);
            along = Math.Clamp(along, 0, alongSize);
            center = Math.Clamp(center, halfWidth, Math.Max(halfWidth, crossSize - halfWidth));
        }

        return new GenieLayout(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            width,
            height,
            along,
            center,
            halfWidth);
    }

    private void ApplyLayout(GenieLayout layout)
    {
        _layout = layout;
        bool vertical = _edge is AppBarEdge.Left or AppBarEdge.Right;
        _along = vertical ? layout.Width : layout.Height;
        _cross = vertical ? layout.Height : layout.Width;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        try
        {
            Tick();
        }
        catch (Exception)
        {
            Abort();
        }
    }

    private void Tick()
    {
        if (!_isShown)
        {
            // Kapanışta gerçek kartı devralmadan önce kopya ekranda olmalı, yoksa bir an boşluk görünür.
            if (!HasReachedScreen())
            {
                return;
            }

            _isShown = true;
            _ui.BeginInvoke(DispatcherPriority.Send, _shown!);
        }

        if (_isFinishing)
        {
            // Açılışta gerçek kart çizilene dek kopya yerinde kalır.
            if (_cardShown && HasReachedScreen())
            {
                Stop();
            }
            return;
        }

        double progress = Progress;
        _position = PositionAt(progress);

        Deform(_position);
        _window!.Opacity = Fade(_position);

        // Animasyon gerçek karttan önce bitebilir (ilk açılış): kopya yerinde bekler.
        if (progress >= 1 && _cardReady)
        {
            _isFinishing = true;
            _ui.BeginInvoke(DispatcherPriority.Send, () =>
            {
                _finished();

                // Yerleşim önceliği işlendiğinde arayüz o kareyi çizmeye göndermiştir.
                _ui.BeginInvoke(DispatcherPriority.Loaded, () => _stage.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    _cardShown = true;
                    _waitFrames = 0;
                    _showClock.Restart();
                }));
            });
        }
    }

    private bool HasReachedScreen()
    {
        return ++_waitFrames >= ShowFrames && _showClock.ElapsedMilliseconds >= ShowMinMs;
    }

    private void Stop()
    {
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (_window != null)
        {
            GlassCapture.RemoveTransient(_window.Handle);
            _window.Release();
            _window = null;
        }
    }

    /// <summary>Animasyon hata verdi: kart yine de açılıp kapanabilsin.</summary>
    private void Abort()
    {
        Stop();
        if (!_isFinishing)
        {
            _isFinishing = true;
            _ui.BeginInvoke(DispatcherPriority.Send, () =>
            {
                if (!_isShown)
                {
                    _shown?.Invoke();
                }
                _finished();
            });
        }
    }

    private void BuildMesh()
    {
        bool vertical = _edge is AppBarEdge.Left or AppBarEdge.Right;
        int columns = vertical ? AlongSteps : CrossSteps;
        int rows = vertical ? CrossSteps : AlongSteps;

        var coordinates = new PointCollection((columns + 1) * (rows + 1));
        var indices = new Int32Collection(columns * rows * 6);
        for (int row = 0; row <= rows; row++)
        {
            for (int column = 0; column <= columns; column++)
            {
                coordinates.Add(new Point((double)column / columns, (double)row / rows));

                if (row < rows && column < columns)
                {
                    int here = row * (columns + 1) + column;
                    int below = here + columns + 1;
                    indices.Add(here);
                    indices.Add(below);
                    indices.Add(here + 1);
                    indices.Add(here + 1);
                    indices.Add(below);
                    indices.Add(below + 1);
                }
            }
        }

        _mesh!.TextureCoordinates = coordinates;
        _mesh.TriangleIndices = indices;
    }

    /// <summary>
    /// 0 tam açık, 1 düğmenin içinde. Önce yanlar boyuna doğru bükülür, sonra kart bükülen yoldan aşağı akar.
    /// Hesap çubuğa uzaklık ve ona dik eksende yapılır, dört kenarda da aynı.
    /// </summary>
    private void Deform(double position)
    {
        // Bükülme hızlı başlar: kapanışın ilk karesinde hareket görünsün.
        double bendAmount = Math.Clamp(position / BendEnd, 0, 1);
        double bend = bendAmount * (2 - bendAmount);
        double slide = SmoothStep(SlideStart, 1, position);
        double neck = Lerp(_along, _layout.NeckAlong, Math.Clamp(position / NeckTravelEnd, 0, 1));
        double neckStart = _layout.NeckCenter - _layout.NeckHalfWidth;
        double neckEnd = _layout.NeckCenter + _layout.NeckHalfWidth;

        PointCollection coordinates = _mesh!.TextureCoordinates;
        var positions = new Point3DCollection(coordinates.Count);
        foreach (Point texture in coordinates)
        {
            (double along, double cross) = _edge switch
            {
                AppBarEdge.Top => ((1 - texture.Y) * _along, texture.X * _cross),
                AppBarEdge.Left => ((1 - texture.X) * _along, texture.Y * _cross),
                AppBarEdge.Right => (texture.X * _along, texture.Y * _cross),
                _ => (texture.Y * _along, texture.X * _cross)
            };

            double a = Math.Min(neck, along + slide * _along);
            double fromNeck = neck > 0 ? (neck - a) / neck : 0;
            double funnel = SmoothStep(0, FunnelReach, fromNeck);
            double start = Lerp(0, Lerp(neckStart, 0, funnel), bend);
            double end = Lerp(_cross, Lerp(neckEnd, _cross, funnel), bend);
            double c = Lerp(start, end, cross / _cross);

            // Dünya koordinatında Y yukarı.
            positions.Add(_edge switch
            {
                AppBarEdge.Top => new Point3D(c, -(_along - a), 0),
                AppBarEdge.Left => new Point3D(_along - a, -c, 0),
                AppBarEdge.Right => new Point3D(a, -c, 0),
                _ => new Point3D(c, -a, 0)
            });
        }
        _mesh.Positions = positions;
    }

    private static double Fade(double position)
    {
        return Math.Clamp((1 - position) / FadeTail, 0, 1);
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }

    private static double SmoothStep(double from, double to, double value)
    {
        double x = Math.Clamp((value - from) / (to - from), 0, 1);
        return x * x * (3 - 2 * x);
    }
}
