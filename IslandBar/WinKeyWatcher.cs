using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Windows tuşuna tek başına basılınca haber verir ve Windows'un kendi Başlat menüsünü açmasını önler.
/// Tuş yutulmaz: Windows menüyü, araya başka tuş girmeden bırakılınca açar, bırakmadan önce etkisiz bir tuş
/// göndermek yeter. Bu yüzden Win+E gibi kısayollar olduğu gibi çalışır. Süreç ölünce kanca da kalkar,
/// Windows tuşu kendiliğinden eski işine döner.
/// Yönetici olarak çalışan pencere öndeyken Windows bu tuşları göstermez; o sırada Windows'un menüsü açılır.
/// </summary>
internal sealed class WinKeyWatcher : IDisposable
{
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
    private readonly KeyboardHook _hook;
    private bool _winDown;
    private bool _combination;

    /// <param name="pressed">Kancanın iş parçacığında çağrılır; hemen dönmeli.</param>
    public WinKeyWatcher(Action pressed)
    {
        _pressed = pressed;
        _hook = new KeyboardHook(nameof(WinKeyWatcher), OnKey);
    }

    /// <summary>Windows'un kendi menüsünü bilerek açarken: hemen ardından gelen Windows tuşuna karışma.</summary>
    public static void AllowNativeStart()
    {
        Interlocked.Exchange(ref _nativeStartUntil, Environment.TickCount64 + NativeStartWindowMs);
    }

    public void Dispose()
    {
        _hook.Dispose();
    }

    private bool OnKey(int key, bool down)
    {
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

        return false;
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

    [DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
}
