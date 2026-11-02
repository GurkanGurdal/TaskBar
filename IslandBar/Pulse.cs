using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Arka plan iş parçacığından gelen düzenli dürtü; <see cref="DispatcherTimer"/> yerine. Çubuk kenarda açılmadığında
/// kayıt, arayüz iş parçacığı boştayken 70 ms'lik zamanlayıcının 0,4-1,5 sn geç geldiğini gösterdi: WPF zamanlayıcıyı
/// görünmez mesaj penceresinin WM_TIMER'ına bağlar, Windows görünmeyen pencerelerin zamanlayıcılarını birleştirip
/// geciktirebilir. Uyku ve gönderilen işler bundan etkilenmez. Önceki dürtü işlenmeden yenisi gönderilmez, arayüz
/// meşgulken iş birikmez.
/// </summary>
internal sealed class Pulse : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly int _intervalMs;
    private readonly Action _tick;
    private volatile bool _stopped;
    private int _pending;
    private long _postedAt;

    /// <summary>Gönderilen dürtü bu kadar alınmazsa arayüzün neyi beklediği sorulur.</summary>
    private const int ProbeAfterMs = 250;

    public Pulse(Dispatcher dispatcher, int intervalMs, Action tick)
    {
        _dispatcher = dispatcher;
        _intervalMs = intervalMs;
        _tick = tick;
        new Thread(Run) { IsBackground = true, Name = nameof(Pulse) }.Start();
    }

    public void Dispose()
    {
        _stopped = true;
    }

    private void Run()
    {
        while (!_stopped)
        {
            Thread.Sleep(_intervalMs);
            if (Interlocked.Exchange(ref _pending, 1) == 0)
            {
                Interlocked.Exchange(ref _postedAt, Environment.TickCount64);
                _dispatcher.BeginInvoke(DispatcherPriority.Input, Tick);
            }
            else if (Environment.TickCount64 - Interlocked.Read(ref _postedAt) is var late and >= ProbeAfterMs)
            {
                UiStallWatch.ProbeStall(late);
            }
        }
    }

    private void Tick()
    {
        Volatile.Write(ref _pending, 0);
        if (!_stopped)
        {
            _tick();
        }
    }
}
