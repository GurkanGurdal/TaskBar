using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GrupBar;

/// <summary>
/// Windows tuşuna tek başına basılınca haber verir ve Windows'un kendi Başlat menüsünü açmasını önler.
/// Tuş yutulmaz: Windows menüyü, araya başka tuş girmeden bırakılınca açar, bırakmadan önce etkisiz bir tuş
/// göndermek yeter. Bu yüzden Win+E gibi kısayollar olduğu gibi çalışır. Süreç ölünce kanca da kalkar,
/// Windows tuşu kendiliğinden eski işine döner.
/// Yönetici olarak çalışan pencere öndeyken Windows bu tuşları göstermez; o sırada Windows'un menüsü açılır.
/// </summary>
internal sealed class WinKeyWatcher : IDisposable
{
    private const int KeyboardHook = 13;
    private const int WmKeyDown = 0x100;
    private const int WmSysKeyDown = 0x104;
    private const int WmQuit = 0x12;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;

    /// <summary>Atanmamış tuş kodu; Windows 10 1909'dan beri vk07 oyun çubuğunu açıyor, onu kullanma.</summary>
    private const ushort VkMask = 0xE8;
    private const uint KeyEventUp = 2;
    private const int NativeStartWindowMs = 500;

    private static long _nativeStartUntil;

    private readonly Action _pressed;
    private readonly HookProc _hookProc;
    private readonly Thread _thread;
    private uint _threadId;
    private bool _winDown;
    private bool _combination;

    /// <param name="pressed">Kancanın iş parçacığında çağrılır; hemen dönmeli.</param>
    public WinKeyWatcher(Action pressed)
    {
        _pressed = pressed;
        _hookProc = HookCallback;

        // Kendi iş parçacığı: arayüz meşgulken yavaş kalan kancayı Windows sessizce kaldırır.
        _thread = new Thread(Run) { IsBackground = true, Name = "WinKeyWatcher" };
        _thread.Start();
    }

    /// <summary>Windows'un kendi menüsünü bilerek açarken: hemen ardından gelen Windows tuşuna karışma.</summary>
    public static void AllowNativeStart()
    {
        Interlocked.Exchange(ref _nativeStartUntil, Environment.TickCount64 + NativeStartWindowMs);
    }

    public void Dispose()
    {
        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        IntPtr hook = SetWindowsHookEx(KeyboardHook, _hookProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            ManagedShell.Common.Logging.ShellLogger.Error($"WinKeyWatcher: hook failed: {new Win32Exception().Message}");
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
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            bool down = (int)message is WmKeyDown or WmSysKeyDown;

            if (key is VkLeftWin or VkRightWin)
            {
                if (down)
                {
                    if (!_winDown)
                    {
                        _winDown = true;
                        _combination = false;
                    }
                }
                else if (_winDown)
                {
                    _winDown = false;
                    if (!_combination && !IsModifierDown() && Environment.TickCount64 >= Interlocked.Read(ref _nativeStartUntil))
                    {
                        SendMaskKey();
                        _pressed();
                    }
                }
            }
            else if (down && _winDown && key != VkMask)
            {
                _combination = true;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private static bool IsModifierDown()
    {
        return GetAsyncKeyState(VkShift) < 0 || GetAsyncKeyState(VkControl) < 0 || GetAsyncKeyState(VkAlt) < 0;
    }

    private static void SendMaskKey()
    {
        keybd_event((byte)VkMask, 0, 0, IntPtr.Zero);
        keybd_event((byte)VkMask, 0, KeyEventUp, IntPtr.Zero);
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

    [DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
