using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Tanı: arayüz iş parçacığında uzun süren işleri adıyla <see cref="AutoHideLog"/>'a yazar. Çubuk kenarda açılmadığında
/// kayıt 400-1000 ms donmalar gösterdi; imleç ve kayma da bu iş parçacığında, donarken çubuk tepki veremez.
/// </summary>
internal static class UiStallWatch
{
    private const double SlowMs = 150;

    /// <summary>Kuyruktaki işler kısa kaldığı hâlde dürtü 0,5 sn gecikiyordu: pencere mesajı başına süre de ölçülür.</summary>
    private const double SlowMessageMs = 200;
    private const int FirstRegisteredMessage = 0xC000;

    private static long _messageStart;
    private static MSG _message;
    private static int _uiThread;
    private static long _lastProbe;

    private const int ProbeIntervalMs = 2000;
    private const int MaxChain = 16;
    private const int ThreadNode = 8;

    private static readonly ConditionalWeakTable<DispatcherOperation, StrongBox<long>> Started = new();

    /// <summary>İşin kendisi; WPF dışarı açmıyor, yalnızca tanı için okunur.</summary>
    private static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Attach(Dispatcher dispatcher)
    {
        _uiThread = GetCurrentThreadId();

        // Mesaj işlenmeden önce ve sıra boşalıp beklemeye geçerken: arası mesajın (ve onun tetiklediği işlerin) süresi.
        ComponentDispatcher.ThreadPreprocessMessage += (ref MSG message, ref bool handled) =>
        {
            FinishMessage();
            _message = message;
            _messageStart = Stopwatch.GetTimestamp();
        };
        ComponentDispatcher.ThreadIdle += (_, _) => FinishMessage();

        dispatcher.Hooks.OperationStarted += (_, e) => Started.AddOrUpdate(e.Operation, new StrongBox<long>(Stopwatch.GetTimestamp()));
        dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (Started.TryGetValue(e.Operation, out StrongBox<long>? start))
            {
                Started.Remove(e.Operation);
                double ms = Stopwatch.GetElapsedTime(start.Value).TotalMilliseconds;
                if (ms >= SlowMs)
                {
                    AutoHideLog.Write($"arayüz {ms:0} ms meşgul: {Describe(e.Operation)}");
                }
            }
        };
    }

    /// <summary>
    /// Arayüz iş parçacığı dürtüyü almıyor: o an neyi beklediğini Windows'un bekleme zincirinden (Wait Chain Traversal)
    /// sorar ve yazar. Mesaj döngüsünün içinde başka sürece giden bir çağrıyı bekliyorsa iki ölçüm de onu görmüyordu.
    /// Dürtü iş parçacığından çağrılır; sık kayıt olmasın diye en çok iki saniyede bir.
    /// </summary>
    public static void ProbeStall(long lateMs)
    {
        long now = Environment.TickCount64;
        if (_uiThread == 0 || now - Interlocked.Read(ref _lastProbe) < ProbeIntervalMs)
        {
            return;
        }
        Interlocked.Exchange(ref _lastProbe, now);
        AutoHideLog.Write($"arayüz {lateMs} ms yanıt vermiyor, bekliyor: {WaitChain(_uiThread)}");
    }

    private static string WaitChain(int threadId)
    {
        IntPtr session = OpenThreadWaitChainSession(0, IntPtr.Zero);
        if (session == IntPtr.Zero)
        {
            return "zincir okunamadı";
        }

        try
        {
            int count = MaxChain;
            var nodes = new WaitNode[MaxChain];
            const int allFlags = 7;
            if (!GetThreadWaitChain(session, IntPtr.Zero, allFlags, threadId, ref count, nodes, out _))
            {
                return $"zincir okunamadı ({Marshal.GetLastWin32Error()})";
            }

            string[] types = ["?", "kritik bölüm", "SendMessage", "mutex", "ALPC", "COM", "iş parçacığı bekleme", "süreç bekleme", "iş parçacığı", "COM etkinleştirme", "bilinmeyen", "soket", "SMB"];
            string[] states = ["?", "erişim yok", "çalışıyor", "bloklu", "yalnız süreç", "yalnız süreç", "sahipsiz", "sahipli", "?"];
            var chain = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                WaitNode node = nodes[i];
                string type = node.ObjectType >= 0 && node.ObjectType < types.Length ? types[node.ObjectType] : node.ObjectType.ToString();
                string state = node.ObjectStatus >= 0 && node.ObjectStatus < states.Length ? states[node.ObjectStatus] : node.ObjectStatus.ToString();
                chain.Append(node.ObjectType == ThreadNode
                    ? $"[{ProcessName(node.ProcessId)} {node.ProcessId}/{node.ThreadId} {state}] "
                    : $"-> {type} ({state}) ");
            }
            return chain.ToString().TrimEnd();
        }
        finally
        {
            CloseThreadWaitChainSession(session);
        }
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "?";
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 280)]
    private struct WaitNode
    {
        [FieldOffset(0)] public int ObjectType;
        [FieldOffset(4)] public int ObjectStatus;
        [FieldOffset(8)] public int ProcessId;
        [FieldOffset(12)] public int ThreadId;
    }

    [DllImport("advapi32", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(int flags, IntPtr callback);

    [DllImport("advapi32")]
    private static extern void CloseThreadWaitChainSession(IntPtr session);

    [DllImport("advapi32", SetLastError = true)]
    private static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, int flags, int threadId, ref int count, [Out] WaitNode[] nodes, out bool isCycle);

    [DllImport("kernel32")]
    private static extern int GetCurrentThreadId();

    private static void FinishMessage()
    {
        if (_messageStart == 0)
        {
            return;
        }

        double ms = Stopwatch.GetElapsedTime(_messageStart).TotalMilliseconds;
        _messageStart = 0;
        if (ms >= SlowMessageMs)
        {
            AutoHideLog.Write($"mesaj {ms:0} ms: {Describe(_message)}");
        }
    }

    private static string Describe(MSG message)
    {
        var text = new StringBuilder(256);
        string window = "iş parçacığı";
        if (message.hwnd != IntPtr.Zero)
        {
            GetClassName(message.hwnd, text, text.Capacity);
            window = text.ToString();
            text.Clear();
            GetWindowText(message.hwnd, text, text.Capacity);
            window += $" '{text}'";
            text.Clear();
        }

        // Kayıtlı mesajların adı pano biçimleriyle aynı tablodan okunur.
        string name = message.message >= FirstRegisteredMessage && GetClipboardFormatName((uint)message.message, text, text.Capacity) > 0
            ? text.ToString()
            : $"0x{message.message:X4}";
        return $"{name} wParam=0x{message.wParam.ToInt64():X} pencere={window}";
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int length);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder name, int length);

    private static string Describe(DispatcherOperation operation)
    {
        if (MethodField?.GetValue(operation) is not Delegate method)
        {
            return operation.Priority.ToString();
        }

        string target = method.Target?.GetType().FullName ?? method.Method.DeclaringType?.FullName ?? "?";
        return $"{target}.{method.Method.Name} ({operation.Priority})";
    }
}
