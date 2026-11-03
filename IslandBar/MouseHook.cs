using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Düşük düzey fare kancası, kendi iş parçacığında (<see cref="KeyboardHook"/> gibi): arayüz meşgulken yavaş kalan
/// kancayı Windows sessizce kaldırır. Yalnızca izler, hiçbir olayı yutmaz. Hareket yalnızca sol tuş basılıyken iletilir.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public const int LeftDown = 0x201;
    public const int LeftUp = 0x202;
    public const int Move = 0x200;
    public const int RightDown = 0x204;
    public const int MiddleDown = 0x207;
    public const int Wheel = 0x20A;

    private const int MouseHookId = 14;
    private const int WmQuit = 0x12;
    private const int MoveIntervalMs = 15;

    private readonly Action<int, System.Drawing.Point> _handler;
    private readonly HookProc _hookProc;
    private readonly Thread _thread;
    private uint _threadId;
    private bool _leftDown;
    private long _lastMove;

    /// <param name="handler">Fare iletisi ve ekran pikseli. Kancanın iş parçacığında çağrılır, hemen dönmeli.</param>
    public MouseHook(string name, Action<int, System.Drawing.Point> handler)
    {
        _handler = handler;
        _hookProc = HookCallback;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(MouseHookId, _hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"{_thread.Name}: hook failed: {new Win32Exception().Message}");
            return;
        }

        while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
        {
            DispatchMessage(ref message);
        }

        UnhookWindowsHookEx(hook);
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (int)message is LeftDown or LeftUp)
        {
            _leftDown = (int)message == LeftDown;
        }

        if (code >= 0 && (int)message == Move)
        {
            // Sürüklerken arayüze saniyede en çok ~60 kez.
            long now = Environment.TickCount64;
            if (!_leftDown || now - _lastMove < MoveIntervalMs)
            {
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            }
            _lastMove = now;
        }

        if (code >= 0 && (int)message is LeftDown or LeftUp or RightDown or MiddleDown or Wheel or Move)
        {
            _handler((int)message, new System.Drawing.Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4)));
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32")]
    private static extern bool PostThreadMessage(uint thread, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
