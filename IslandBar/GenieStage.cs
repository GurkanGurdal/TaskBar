using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Huni animasyonunun sahnesi: kendi iş parçacığı ve önceden açılmış pencereler. Gerçek kartın yerleşimi ve
/// görüntüsü arayüz iş parçacığını bekletir; animasyon bunlardan bağımsız, basışın hemen ardından başlar.
/// </summary>
internal sealed class GenieStage : IDisposable
{
    private const int WindowsPerStage = 2;

    /// <summary>Tüm çubuklar için tek iş parçacığı; aynı anda en çok birkaç animasyon oynar.</summary>
    private static readonly Lazy<Dispatcher> StageThread = new(StartThread);

    private readonly List<GenieWindow> _windows = new();
    private readonly int _homeX;
    private readonly int _homeY;

    /// <param name="screen">Pencereler bu ekranın DPI'siyle açılsın diye konumları orada.</param>
    public GenieStage(System.Drawing.Rectangle screen)
    {
        _homeX = screen.X + screen.Width / 2;
        _homeY = screen.Y + screen.Height / 2;

        Dispatcher.BeginInvoke(() =>
        {
            for (int i = 0; i < WindowsPerStage; i++)
            {
                _windows.Add(new GenieWindow(_homeX, _homeY));
            }
        });
    }

    public Dispatcher Dispatcher => StageThread.Value;

    public void Play(GenieEffect effect)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Send, effect.Start);
    }

    /// <summary>Boşta olan pencereyi verir. Sahne iş parçacığında çağrılır.</summary>
    internal GenieWindow Rent()
    {
        GenieWindow? window = _windows.FirstOrDefault(item => !item.InUse);
        if (window == null)
        {
            window = new GenieWindow(_homeX, _homeY);
            _windows.Add(window);
        }

        window.InUse = true;
        return window;
    }

    public void Dispose()
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (GenieWindow window in _windows)
            {
                window.Dispose();
            }
            _windows.Clear();
        });
    }

    private static Dispatcher StartThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            // Süs: buradaki bir hata görev çubuğunu düşürmemeli.
            dispatcher.UnhandledException += (_, e) => e.Handled = true;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "GenieStage",
            Priority = ThreadPriority.AboveNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>
/// Saydam, tıklamaları geçiren, sürekli açık ama cloak ile gizli pencere: açmak yalnızca cloak'u kaldırmak.
/// Köküne doğrudan <see cref="Viewport3DVisual"/> konur; yerleşim geçişi olmadığı için her kare ucuz.
/// </summary>
internal sealed class GenieWindow : IDisposable
{
    private const int WindowStylePopup = unchecked((int)0x80000000);
    private const int ExStyleTopmost = 0x8;
    private const int ExStyleTransparent = 0x20;
    private const int ExStyleToolWindow = 0x80;
    private const int ExStyleNoActivate = 0x8000000;
    private const int ShowNoActivate = 4;
    private const int DwmCloak = 13;
    private const uint SwpNoActivate = 0x10;

    private static readonly IntPtr TopMost = new(-1);

    private readonly HwndSource _source;
    private readonly Viewport3DVisual _visual = new();
    private readonly GeometryModel3D _model = new();
    private readonly OrthographicCamera _camera = new() { LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0) };

    public GenieWindow(int x, int y)
    {
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Colors.White));
        scene.Children.Add(_model);
        _visual.Children.Add(new ModelVisual3D { Content = scene });
        _visual.Camera = _camera;
        _visual.Opacity = 0;

        _source = new HwndSource(new HwndSourceParameters("IslandBar.Genie")
        {
            WindowStyle = WindowStylePopup,
            ExtendedWindowStyle = ExStyleTopmost | ExStyleTransparent | ExStyleToolWindow | ExStyleNoActivate,
            UsesPerPixelOpacity = true,
            TreatAsInputRoot = false,
            PositionX = x,
            PositionY = y,
            Width = 1,
            Height = 1
        });
        _source.CompositionTarget!.BackgroundColor = Colors.Transparent;
        _source.RootVisual = _visual;

        SetCloak(true);
        ShowWindow(Handle, ShowNoActivate);
    }

    public IntPtr Handle => _source.Handle;

    public bool InUse { get; set; }

    public double Opacity
    {
        set => _visual.Opacity = value;
    }

    public void Present(MeshGeometry3D mesh, Material material, GenieLayout layout, bool visible)
    {
        _model.Geometry = mesh;
        _model.Material = material;
        Move(layout);

        if (visible)
        {
            SetCloak(false);
        }
    }

    public void Move(GenieLayout layout)
    {
        _visual.Viewport = new Rect(0, 0, layout.Width, layout.Height);
        _camera.Position = new Point3D(layout.Width / 2, -layout.Height / 2, 10);
        _camera.Width = layout.Width;
        SetWindowPos(Handle, TopMost, layout.X, layout.Y, layout.PixelWidth, layout.PixelHeight, SwpNoActivate);
    }

    /// <summary>Boş kare de çizilir: bir sonraki açılışta eski görüntü bir an görünmesin.</summary>
    public void Release()
    {
        SetCloak(true);
        _visual.Opacity = 0;
        _model.Geometry = null;
        _model.Material = null;
        InUse = false;
    }

    public void Dispose()
    {
        _source.Dispose();
    }

    private void SetCloak(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(Handle, DwmCloak, ref value, sizeof(int));
    }

    [DllImport("dwmapi")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
