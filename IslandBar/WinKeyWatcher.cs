using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Windows tuşuna tek başına basılınca haber verir ve Windows'un kendi Başlat menüsünü açmasını önler.
/// Tuş yutulmaz: Windows menüyü, araya başka tuş girmeden bırakılınca açar, bırakmadan önce etkisiz bir tuş
/// göndermek yeter. Bu yüzden Win+E gibi kısayollar olduğu gibi çalışır. Süreç ölünce kanca da kalkar,
/// Windows tuşu kendiliğinden eski işine döner.
/// Yönetici olarak çalışan pencere öndeyken Windows bu tuşları göstermez; o sırada Windows'un menüsü açılır.
/// Win+1…9, 0 da buradan: Windows bunları gizli çubuğunun kendi sabitlemelerine gönderirdi. Rakam yutulur; Windows
/// arada tuş görmediği için bırakılırken yine etkisiz tuş gönderilir, yoksa kendi menüsünü açar.
/// </summary>
internal sealed class WinKeyWatcher : IDisposable
{
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkZero = 0x30;
    private const int VkNine = 0x39;

    /// <summary>Atanmamış tuş kodu; Windows 10 1909'dan beri vk07 oyun çubuğunu açıyor, onu kullanma.</summary>
    private const ushort VkMask = 0xE8;
    private const uint KeyEventUp = 2;
    private const int NativeStartWindowMs = 500;

    private static long _nativeStartUntil;

    private readonly Action? _pressed;
    private readonly Action<int, bool>? _number;
    private readonly KeyboardHook _hook;
    private readonly HashSet<int> _swallowedDigits = new();
    private bool _winDown;
    private bool _combination;
    private bool _numbered;

    /// <param name="pressed">Windows tuşu tek başına; null ise Windows'un menüsüne karışılmaz.</param>
    /// <param name="number">Win+rakam: sıra (1 için 0, 0 için 9) ve Shift basılı mı; null ise rakamlar Windows'a gider.</param>
    /// <remarks>İkisi de kancanın iş parçacığında çağrılır; hemen dönmeli.</remarks>
    public WinKeyWatcher(Action? pressed, Action<int, bool>? number)
    {
        _pressed = pressed;
        _number = number;
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
        // Windows tuşunun bırakılışı kancaya hiç gelmeyebilir (o an kilit ekranı, UAC ya da yönetici olarak çalışan
        // pencere öndeydi): o zaman "basılı" sanılıyor, tek başına basılan 1 Win+1 gibi yutulup çubuktaki ilk
        // uygulamayı açıp kapatıyordu (kullanıcı yaşadı). Gerçek durum her tuşta sorulur.
        if (_winDown && key is not (VkLeftWin or VkRightWin) && !IsDown(VkLeftWin) && !IsDown(VkRightWin))
        {
            _winDown = false;
        }

        if (key is VkLeftWin or VkRightWin)
        {
            if (down)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _combination = false;
                    _numbered = false;
                }
            }
            else if (_winDown)
            {
                _winDown = false;
                if (_numbered)
                {
                    SendMaskKey();
                }
                else if (_pressed != null && !_combination && !IsModifierDown() && Environment.TickCount64 >= Interlocked.Read(ref _nativeStartUntil))
                {
                    SendMaskKey();
                    _pressed();
                }
            }
        }
        else if (key is >= VkZero and <= VkNine && (down ? _winDown && _number != null && !IsDown(VkAlt) : _swallowedDigits.Contains(key)))
        {
            // Basılı tutulan rakamın tekrarları sayılmaz; bırakılışı da yutulur, Win önce bırakılmış olsa bile.
            if (!down)
            {
                _swallowedDigits.Remove(key);
            }
            else if (_swallowedDigits.Add(key))
            {
                _combination = true;
                _numbered = true;
                _number!(key == VkZero ? 9 : key - VkZero - 1, IsDown(VkShift));
            }
            return true;
        }
        else if (down && _winDown && key != VkMask)
        {
            _combination = true;
        }

        return false;
    }

    private static bool IsModifierDown()
    {
        return IsDown(VkShift) || IsDown(VkControl) || IsDown(VkAlt);
    }

    private static bool IsDown(int key)
    {
        return GetAsyncKeyState(key) < 0;
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
