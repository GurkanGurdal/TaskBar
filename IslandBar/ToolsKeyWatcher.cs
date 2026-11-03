using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Alt + S: IslandBar araçlarının tekerleği (kullanıcının seçimi; iki tuşlu, tek elle, sistemde boş, ölçüldü). Basılı
/// tutulurken tekerlek açık kalır, tuş bırakılınca imlecin gittiği yöndeki araç açılır. S yutulur, uygulamalara gitmez.
/// Alt yalnız bırakılırsa Windows açık pencerenin menü çubuğunu seçer: S yutulduğu için araya tuş girmemiş sayılır,
/// bu yüzden Alt bırakılmadan önce etkisiz bir tuş gönderilir (Windows tuşundaki yöntemin aynısı).
/// </summary>
internal sealed class ToolsKeyWatcher : IDisposable
{
    private const int VkS = 0x53;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkLeftAlt = 0xA4;
    private const int VkRightAlt = 0xA5;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;

    /// <summary>Atanmamış tuş kodu (<see cref="WinKeyWatcher"/> ile aynı).</summary>
    private const byte VkMask = 0xE8;
    private const uint KeyEventUp = 2;

    private readonly Action _pressed;
    private readonly Action _released;
    private readonly KeyboardHook _hook;
    private bool _held;
    private bool _altUsed;

    /// <param name="pressed">Alt + S basıldı (tekrarlar sayılmaz).</param>
    /// <param name="released">S ya da Alt bırakıldı.</param>
    /// <remarks>İkisi de kancanın iş parçacığında çağrılır; hemen dönmeli.</remarks>
    public ToolsKeyWatcher(Action pressed, Action released)
    {
        _pressed = pressed;
        _released = released;
        _hook = new KeyboardHook(nameof(ToolsKeyWatcher), OnKey);
    }

    public void Dispose()
    {
        _hook.Dispose();
    }

    private bool OnKey(int key, bool down)
    {
        if (key == VkS)
        {
            if (down)
            {
                if (_held)
                {
                    return true;
                }

                if (IsDown(VkAlt) && !IsDown(VkControl) && !IsDown(VkShift) && !IsDown(VkLeftWin) && !IsDown(VkRightWin))
                {
                    _held = true;
                    _altUsed = true;
                    _pressed();
                    return true;
                }
                return false;
            }

            if (_held)
            {
                _held = false;
                _released();
                return true;
            }
            return false;
        }

        if (!down && key is VkLeftAlt or VkRightAlt or VkAlt && _altUsed)
        {
            _altUsed = false;
            SendMaskKey();
            if (_held)
            {
                // S hâlâ basılı; bırakılışı yine yutulsun ama seçim şimdi.
                _released();
            }
        }

        return false;
    }

    private static bool IsDown(int key)
    {
        return GetAsyncKeyState(key) < 0;
    }

    private static void SendMaskKey()
    {
        keybd_event(VkMask, 0, 0, IntPtr.Zero);
        keybd_event(VkMask, 0, KeyEventUp, IntPtr.Zero);
    }

    [DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
}
