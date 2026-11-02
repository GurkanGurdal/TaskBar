namespace IslandBar;

internal enum VolumeKey
{
    Up,
    Down,
    Mute
}

/// <summary>
/// Klavyenin ses tuşlarını yutar: Windows tuşu hiç görmez, kendi göstergesini de açmaz; sesi değiştirmek bize kalır.
/// Windows'un göstergesini gizlemenin belgeli yolu yok, onu bulup gizleyen araçlar sürüm değişince bozuluyor. Süreç
/// ölünce kanca kalkar, tuşlar ve Windows'un göstergesi kendiliğinden geri gelir. Yönetici olarak çalışan pencere
/// öndeyken ya da ses tuş olarak gelmeyip aygıtın yazılımından değişince Windows'unki çıkar.
/// </summary>
internal sealed class VolumeKeyWatcher : IDisposable
{
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;

    private readonly Action<VolumeKey> _pressed;
    private readonly KeyboardHook _hook;
    private bool _muteDown;

    /// <param name="pressed">Kancanın iş parçacığında çağrılır; hemen dönmeli.</param>
    public VolumeKeyWatcher(Action<VolumeKey> pressed)
    {
        _pressed = pressed;
        _hook = new KeyboardHook(nameof(VolumeKeyWatcher), OnKey);
    }

    public void Dispose()
    {
        _hook.Dispose();
    }

    private bool OnKey(int key, bool down)
    {
        switch (key)
        {
            case VkVolumeUp:
                if (down)
                {
                    _pressed(VolumeKey.Up);
                }
                return true;

            case VkVolumeDown:
                if (down)
                {
                    _pressed(VolumeKey.Down);
                }
                return true;

            case VkVolumeMute:
                // Basılı tutunca tekrar gelir; sessize alma açılıp kapanmasın.
                if (down && !_muteDown)
                {
                    _pressed(VolumeKey.Mute);
                }
                _muteDown = down;
                return true;

            default:
                return false;
        }
    }
}
