using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Tanı: arayüz iş parçacığının kare aralıkları (<c>--frame-log=dosya</c> ile açılır, normalde kapalı). Her karede
/// <see cref="CompositionTarget.Rendering"/> çalışır, bu yüzden açıkken WPF sürekli çizer; yalnızca ölçüm için.
/// 10 saniyede bir dosyaya kare sayısı, 25 / 50 / 100 ms'yi aşan aralıklar ve en uzunu yazılır.
/// </summary>
internal static class FrameWatch
{
    private const string Argument = "--frame-log=";
    private const int ReportSeconds = 10;

    private static readonly Stopwatch Clock = new();
    private static string? _file;
    private static TimeSpan _lastFrame;
    private static int _frames;
    private static int _over25;
    private static int _over50;
    private static int _over100;
    private static double _longest;
    private static readonly List<string> Long = [];

    public static void StartIfRequested(Dispatcher dispatcher, string[] args)
    {
        _file = args.FirstOrDefault(arg => arg.StartsWith(Argument, StringComparison.Ordinal))?[Argument.Length..];
        if (string.IsNullOrEmpty(_file))
        {
            return;
        }

        Clock.Start();
        CompositionTarget.Rendering += Rendering;
        var report = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(ReportSeconds) };
        report.Tick += (_, _) => Report();
        report.Start();
        Write($"başladı, süreç {Environment.ProcessId}");
    }

    private static void Rendering(object? sender, EventArgs e)
    {
        TimeSpan now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame)
        {
            // Aynı kare için ikinci çağrı.
            return;
        }

        if (_lastFrame != TimeSpan.Zero)
        {
            double gap = (now - _lastFrame).TotalMilliseconds;
            _frames++;
            _over25 += gap > 25 ? 1 : 0;
            _over50 += gap > 50 ? 1 : 0;
            _over100 += gap > 100 ? 1 : 0;
            _longest = Math.Max(_longest, gap);
            if (gap > 50 && Long.Count < 20)
            {
                Long.Add($"{Clock.Elapsed.TotalSeconds:0.0}s:{gap:0}");
            }
        }
        _lastFrame = now;
    }

    private static void Report()
    {
        Write($"{_frames} kare, >25 ms {_over25}, >50 ms {_over50}, >100 ms {_over100}, en uzun {_longest:0} ms {string.Join(' ', Long)}");
        _frames = _over25 = _over50 = _over100 = 0;
        _longest = 0;
        Long.Clear();
    }

    private static void Write(string line)
    {
        try
        {
            File.AppendAllText(_file!, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
