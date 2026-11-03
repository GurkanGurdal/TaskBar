using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Çeviri kipi (Alt + S tekerleğindeki Çeviri): açıkken herhangi bir uygulamada fareyle seçilen yazı (sürükleyerek,
/// çift ya da üç tıkla) çevrilir, seçimin altında balon açılır. Kullanıcı kapatana kadar açık kalır (kullanıcının
/// seçimi); açık olduğunu şarkı kartının yerindeki "Çeviri modu" kartı gösterir, onun Kapat düğmesi ya da tekerlekteki
/// dilim kapatır.
/// Pencere sürüklenip taşındıysa seçim sayılmaz. Uygulama yeniden başlayınca kapalı başlar.
/// </summary>
internal sealed class TranslateMode
{
    private const int ReadDelayMs = 60;
    private const int DragDistance = 5;
    private static readonly TimeSpan HintDuration = TimeSpan.FromSeconds(2.5);

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private MouseHook? _hook;
    private TranslationBubble? _bubble;
    private CancellationTokenSource? _pending;
    private System.Drawing.Point _downAt;
    private DateTime _downTime;
    private int _clicks;
    private IntPtr _downWindow;
    private Rect32 _downWindowRect;

    private TranslateMode()
    {
    }

    public static TranslateMode Shared { get; } = new();

    public bool IsOn => _hook != null;

    public event Action<bool>? Changed;

    /// <summary>Tekerlekten: açar ya da kapatır; durumu çubuktaki kip kartı gösterir.</summary>
    public void Toggle()
    {
        SetOn(!IsOn);
    }

    public void SetOn(bool on)
    {
        if (on == IsOn)
        {
            return;
        }

        if (on)
        {
            _hook = new MouseHook("IslandBar.Translate", (message, point) => _dispatcher.BeginInvoke(() => Mouse_Event(message, point)));
            Translator.Warm();
        }
        else
        {
            _hook!.Dispose();
            _hook = null;
            _pending?.Cancel();
            _bubble?.Dismiss();
        }
        Changed?.Invoke(on);
    }

    private void Mouse_Event(int message, System.Drawing.Point point)
    {
        if (!IsOn)
        {
            return;
        }

        bool inBubble = _bubble?.Contains(point) == true;
        if (message != MouseHook.LeftUp && !inBubble)
        {
            _pending?.Cancel();
            _bubble?.Dismiss();
        }

        if (message == MouseHook.LeftDown && !inBubble)
        {
            // Çift ve üç tık: Windows'un çift tık süresi ve alanı içinde art arda basışlar.
            System.Drawing.Size near = System.Windows.Forms.SystemInformation.DoubleClickSize;
            bool repeat = (DateTime.UtcNow - _downTime).TotalMilliseconds <= System.Windows.Forms.SystemInformation.DoubleClickTime
                && Math.Abs(point.X - _downAt.X) <= near.Width / 2 && Math.Abs(point.Y - _downAt.Y) <= near.Height / 2;
            _clicks = repeat ? _clicks + 1 : 1;
            _downAt = point;
            _downTime = DateTime.UtcNow;
            _downWindow = GetAncestor(WindowFromPoint(point), RootWindow);
            GetWindowRect(_downWindow, out _downWindowRect);
        }
        else if (message == MouseHook.LeftUp && !inBubble && _downWindow != IntPtr.Zero)
        {
            bool dragged = Math.Abs(point.X - _downAt.X) > DragDistance || Math.Abs(point.Y - _downAt.Y) > DragDistance;
            if ((dragged || _clicks >= 2) && !IsOwnWindow(_downWindow) && GetWindowRect(_downWindow, out Rect32 now) && now.Equals(_downWindowRect))
            {
                TranslateSelection(point);
            }
        }
    }

    private async void TranslateSelection(System.Drawing.Point cursor)
    {
        _pending?.Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;

        try
        {
            // Uygulama seçimi bırakılan tuşla bitiriyor.
            await Task.Delay(ReadDelayMs, pending.Token);
            if (await SelectionReader.ReadAsync(GetForegroundWindow()) is not { } selected || !selected.Text.Any(char.IsLetter))
            {
                return;
            }

            pending.Token.ThrowIfCancellationRequested();
            Translation translation = await Translator.TranslateAsync(selected.Text.Trim(), pending.Token);
            if (pending.IsCancellationRequested || !IsOn)
            {
                return;
            }

            Rect anchor = selected.Bounds ?? new Rect(cursor.X, cursor.Y, 0, 12);
            Show(translation.Problem == null
                ? TranslationBubble.ForTranslation(anchor, translation)
                : TranslationBubble.ForHint(anchor, translation.Problem, HintDuration));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(TranslationBubble bubble)
    {
        _bubble?.Dismiss();
        _bubble = bubble;
        bubble.Closed += (_, _) =>
        {
            if (_bubble == bubble)
            {
                _bubble = null;
            }
        };
        bubble.Show();
    }

    private static bool IsOwnWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint process);
        return process == Environment.ProcessId;
    }

    private const uint RootWindow = 2;

    [StructLayout(LayoutKind.Sequential)]
    private record struct Rect32(int Left, int Top, int Right, int Bottom);

    [DllImport("user32")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [DllImport("user32")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32")]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
