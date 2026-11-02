using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Düşük düzey klavye kancası, kendi iş parçacığında: arayüz meşgulken yavaş kalan kancayı Windows sessizce kaldırır.
/// Süreç ölünce kanca da kalkar, tuşlar kendiliğinden eski işine döner.
/// Yönetici olarak çalışan pencere öndeyken Windows tuşları buraya göstermez.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int KeyboardHookId = 13;
    private const int WmKeyDown = 0x100;
    private const int WmSysKeyDown = 0x104;
    private const int WmQuit = 0x12;
    private static readonly IntPtr Swallowed = new(1);

    private readonly Func<int, bool, bool> _handler;
    private readonly HookProc _hookProc;
    private readonly Thread _thread;
    private uint _threadId;

    /// <param name="handler">Tuş kodu ve basılı mı; tuş yutulacaksa true. Kancanın iş parçacığında çağrılır, hemen dönmeli.</param>
    public KeyboardHook(string name, Func<int, bool, bool> handler)
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
        IntPtr hook = SetWindowsHookEx(KeyboardHookId, _hookProc, GetModuleHandle(null), 0);
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
        if (code >= 0 && _handler(Marshal.ReadInt32(data), (int)message is WmKeyDown or WmSysKeyDown))
        {
            return Swallowed;
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
