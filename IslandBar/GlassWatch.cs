using System.Drawing;

namespace IslandBar;

/// <summary>
/// Camın arkası değişti mi? Camın arkası doğrudan okunamaz (önünde kendisi var), hemen çevresindeki ince şeritlere bakılır.
/// Şeritler küçük hücrelere bölünür, her hücrenin rengi ayrı karşılaştırılır: ortalama, pencerenin küçük bir kısmındaki
/// ya da parlaklığı aynı kalan renk değişimini kaçırıyordu.
/// Görüntü almak camı bir an ekran yakalamadan çıkardığı için, arkada video oynarken de saniyede birden sık alınmaz.
/// </summary>
internal sealed class GlassWatch
{
    private const int MinRefreshMs = 1000;
    private const int MaxIdleMs = 15000;

    /// <summary>Kanal başına, 255 üzerinden. Daha küçüğü imleç ve yazı imleci gibi oynamalarda boşuna tetikleniyor.</summary>
    private const int ChangeThreshold = 8;

    private readonly object _lock = new();
    private Rectangle[]? _strips;
    private int[]? _colors;
    private long _lastRefresh = Environment.TickCount64;
    private bool _pending;

    /// <summary>Çevreye bakmadan yenilensin: camın boyutu değişti.</summary>
    public void Request()
    {
        lock (_lock)
        {
            _pending = true;
        }
    }

    /// <summary>İzlemeye yeniden başlar: cam az önce tazelendi, ilk okuma karşılaştırılmaz.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _strips = null;
            _colors = null;
            _pending = false;
            _lastRefresh = Environment.TickCount64;
        }
    }

    /// <summary>Şeritleri okur, cam şimdi yenilenmeli mi söyler. Ekran okuduğu için arka planda çağrılır.</summary>
    public bool ShouldRefresh(Rectangle[] strips)
    {
        int[] colors = GlassCapture.SampleStrips(strips);

        lock (_lock)
        {
            if (_colors != null && _strips != null && _strips.SequenceEqual(strips) && HasChanged(_colors, colors))
            {
                _pending = true;
            }

            _strips = strips;
            _colors = colors;

            long sinceRefresh = Environment.TickCount64 - _lastRefresh;
            if (sinceRefresh < MaxIdleMs && (!_pending || sinceRefresh < MinRefreshMs))
            {
                return false;
            }

            _pending = false;
            _lastRefresh = Environment.TickCount64;
            return true;
        }
    }

    private static bool HasChanged(int[] before, int[] now)
    {
        if (before.Length != now.Length)
        {
            return true;
        }

        for (int i = 0; i < now.Length; i++)
        {
            if (Math.Abs(before[i] - now[i]) > ChangeThreshold)
            {
                return true;
            }
        }
        return false;
    }
}
