using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Araçlar kartından başlatılan geri sayım (kullanıcının isteği). Çalışırken saat kalan süreyi gösterir (kullanıcının
/// seçimi: saat geri sayıma döner), bitince şarkı kartının yerinde "Süre doldu" kartı çıkar. Kalan süre bitiş anından
/// hesaplanır: zamanlayıcı geç tetiklense de sayı kaymaz. Yalnızca bellekte, IslandBar kapanınca biter.
/// </summary>
public sealed class FocusTimer
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _endsAt;

    private FocusTimer()
    {
        _tick.Tick += (_, _) =>
        {
            if (Remaining <= TimeSpan.Zero)
            {
                Stop();
                Finished?.Invoke(Total);
            }
            else
            {
                Changed?.Invoke();
            }
        };
    }

    public static FocusTimer Shared { get; } = new();

    /// <summary>Başladı, durdu ya da kalan süre değişti.</summary>
    public event Action? Changed;

    /// <summary>Süre doldu; bildirilen başlangıç süresi.</summary>
    public event Action<TimeSpan>? Finished;

    public bool IsRunning => _tick.IsEnabled;

    public TimeSpan Total { get; private set; }

    public TimeSpan Remaining => IsRunning ? Max(_endsAt - DateTime.Now, TimeSpan.Zero) : TimeSpan.Zero;

    public DateTime EndsAt => _endsAt;

    /// <summary>0 başta, 1 bitişte.</summary>
    public double Progress => IsRunning && Total > TimeSpan.Zero ? 1 - Remaining / Total : 0;

    public void Start(TimeSpan duration)
    {
        Total = duration;
        _endsAt = DateTime.Now + duration;
        _tick.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        _tick.Stop();
        Changed?.Invoke();
    }

    /// <summary>Saatte gösterilecek iki parça: bir saatten uzunsa saat ve dakika, değilse dakika ve saniye.</summary>
    public (string Left, string Right) Display()
    {
        // Yukarı yuvarlanır: "0:00" bitişte görünür, başta tam süre görünür.
        TimeSpan left = TimeSpan.FromSeconds(Math.Ceiling(Remaining.TotalSeconds));
        return left.TotalHours >= 1
            ? (((int)left.TotalHours).ToString(), left.Minutes.ToString("00"))
            : (((int)left.TotalMinutes).ToString("00"), left.Seconds.ToString("00"));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
