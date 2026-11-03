using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace IslandBar;

/// <summary>
/// Araç pencerelerinin (tekerlek, araç kartı, Dönüştür, çeviri balonu, sayaç) camı adayla ve şarkı kartıyla aynı
/// kurallarla (kullanıcının isteği: arkası değişince onlarınki gibi değişsin; önce yalnızca açılışta bir kez alınıyordu).
/// 200 ms'de bir pencerenin çevresindeki ince şeritler arka planda okunur (<see cref="GlassWatch"/>); arkası değişince,
/// en sık saniyede bir, değişmese de 15 saniyede bir cam yeniden alınır, ton yumuşak geçer. Pencerenin kartları sırayla
/// alınır: her biri pencereyi yakalamadan bir an hariç tutup bırakıyor, aynı anda olunca birbirini bozar.
/// Camsız temada çalışmaz.
/// </summary>
internal sealed class GlassWindowWatch
{
    private const int IntervalMs = 200;
    private const double StripWidth = 4;

    private readonly Window _window;
    private readonly Func<IEnumerable<GlassCard>> _cards;
    private readonly AppBarEdge? _barSide;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(IntervalMs) };
    private readonly GlassWatch _watch = new();
    private readonly SemaphoreSlim _capturing = new(1, 1);
    private bool _busy;

    /// <param name="cards">O an pencerede görünen kartlar; görünmeyenler atlanır.</param>
    /// <param name="barSide">Pencere adanın yanındaysa o kenar okunmaz (kartlardaki gibi; orada adanın kendisi var).</param>
    public GlassWindowWatch(Window window, Func<IEnumerable<GlassCard>> cards, AppBarEdge? barSide = null)
    {
        _window = window;
        _cards = cards;
        _barSide = barSide;
        if (!ThemeManager.UsesGlass)
        {
            return;
        }

        _timer.Tick += Timer_Tick;
        window.ContentRendered += (_, _) => _timer.Start();
        window.Closed += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Pencerenin kendi tazelemesi (kart büyüdü, yazısı değişti) izleyicininkiyle sıraya girer: görünür kart pencereyi
    /// hariç tutuyor, aynı anda iki kart olunca biri kendi penceresini görüntüye katıyordu.
    /// </summary>
    public async Task RefreshAsync(GlassCard card, bool isHidden)
    {
        await _capturing.WaitAsync();
        try
        {
            await card.RefreshGlassAsync(animateTint: !isHidden, isHidden);
        }
        finally
        {
            _capturing.Release();
        }
        _watch.Reset();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_busy || PresentationSource.FromVisual(_window) is not HwndSource source || !GetWindowRect(source.Handle, out Rect32 bounds))
        {
            return;
        }

        System.Drawing.Rectangle[] strips = Strips(bounds, source.CompositionTarget.TransformToDevice.M11);
        if (strips.Length == 0)
        {
            return;
        }

        _busy = true;
        try
        {
            // Ekranı okumak birkaç milisaniye sürer; arayüzü bekletmesin.
            if (!await Task.Run(() => _watch.ShouldRefresh(strips)))
            {
                return;
            }

            foreach (GlassCard card in _cards().Where(card => card.IsVisible && card.Opacity > 0).ToList())
            {
                await _capturing.WaitAsync();
                try
                {
                    await card.RefreshGlassAsync(animateTint: true);
                }
                finally
                {
                    _capturing.Release();
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Oturum kilitliyken ekran okunamıyor.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Pencerenin dört kenarının hemen dışı (pencere gölge payını içeriyor), ekrana kırpılmış.</summary>
    private System.Drawing.Rectangle[] Strips(Rect32 bounds, double scale)
    {
        var area = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        int width = (int)Math.Round(StripWidth * scale);
        System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromRectangle(area).Bounds;
        var strips = new List<System.Drawing.Rectangle>();
        foreach (AppBarEdge side in new[] { AppBarEdge.Left, AppBarEdge.Top, AppBarEdge.Right, AppBarEdge.Bottom })
        {
            if (side == _barSide)
            {
                continue;
            }

            System.Drawing.Rectangle strip = side switch
            {
                AppBarEdge.Top => new System.Drawing.Rectangle(area.Left, area.Top - width, area.Width, width),
                AppBarEdge.Bottom => new System.Drawing.Rectangle(area.Left, area.Bottom, area.Width, width),
                AppBarEdge.Left => new System.Drawing.Rectangle(area.Left - width, area.Top, width, area.Height),
                _ => new System.Drawing.Rectangle(area.Right, area.Top, width, area.Height)
            };
            strip.Intersect(screen);

            // Henüz ölçülmemiş şerit okunamaz (çubukta uygulamayı kapatıyordu).
            if (strip.Width > 0 && strip.Height > 0)
            {
                strips.Add(strip);
            }
        }
        return strips.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
}
