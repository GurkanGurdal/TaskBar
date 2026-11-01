using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>Varsayılan hoparlörün ana sesi (Core Audio). Ses aygıtı yoksa <see cref="TryOpen"/> null döner.</summary>
internal sealed class AudioEndpoint
{
    private const int Render = 0;
    private const int Multimedia = 1;
    private const int ClsctxAll = 23;
    private static readonly Guid VolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly IAudioEndpointVolume _volume;

    private AudioEndpoint(IAudioEndpointVolume volume)
    {
        _volume = volume;
    }

    /// <summary>0 ile 1 arasında.</summary>
    public float Level
    {
        get
        {
            Marshal.ThrowExceptionForHR(_volume.GetMasterVolumeLevelScalar(out float level));
            return level;
        }
        set => Marshal.ThrowExceptionForHR(_volume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 1), Guid.Empty));
    }

    public bool Muted
    {
        get
        {
            Marshal.ThrowExceptionForHR(_volume.GetMute(out bool muted));
            return muted;
        }
        set => Marshal.ThrowExceptionForHR(_volume.SetMute(value, Guid.Empty));
    }

    public static AudioEndpoint? TryOpen()
    {
        try
        {
            IAudioEndpointVolume? volume = OpenVolume((IMMDeviceEnumerator)new MMDeviceEnumerator());
            return volume != null ? new AudioEndpoint(volume) : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Ses düzeyi, sessize alma ya da varsayılan aygıt değişince arayüz iş parçacığında haber verir.</summary>
    public static IDisposable? Watch(Action changed)
    {
        try
        {
            return new Watcher(changed);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IAudioEndpointVolume? OpenVolume(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out IMMDevice device) != 0)
        {
            return null;
        }

        Guid iid = VolumeInterface;
        return device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object volume) == 0 ? (IAudioEndpointVolume)volume : null;
    }

    /// <summary>
    /// Windows bu arayüzleri kendi iş parçacıklarından çağırır ve çağrı içinde abonelik değiştirilmesini yasaklar;
    /// iş arayüz iş parçacığına aktarılır.
    /// </summary>
    private sealed class Watcher : IDisposable, IMMNotificationClient, IAudioEndpointVolumeCallback
    {
        private readonly Action _changed;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        private IAudioEndpointVolume? _volume;
        private bool _disposed;

        public Watcher(Action changed)
        {
            _changed = changed;
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this));
            AttachToDefaultDevice();
        }

        public void Dispose()
        {
            _disposed = true;
            _volume?.UnregisterControlChangeNotify(this);
            _volume = null;
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }

        private void AttachToDefaultDevice()
        {
            _volume?.UnregisterControlChangeNotify(this);
            _volume = OpenVolume(_enumerator);
            _volume?.RegisterControlChangeNotify(this);
        }

        private void Notify(bool deviceChanged)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (deviceChanged)
                {
                    AttachToDefaultDevice();
                }
                _changed();
            });
        }

        public int OnNotify(IntPtr data)
        {
            Notify(deviceChanged: false);
            return 0;
        }

        public int OnDefaultDeviceChanged(int flow, int role, string? deviceId)
        {
            if (flow == Render && role == Multimedia)
            {
                Notify(deviceChanged: true);
            }
            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public int Id;
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    /// <summary>Süreçte tek tür olmalı: Windows aynı nesneyi döndürür, ikinci bir tür dönüşümde hata verir.</summary>
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    /// <summary>Yöntem sırası COM tablosuyla aynı olmalı; kullanılmayanlar yer tutar.</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
