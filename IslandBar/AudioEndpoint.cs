using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>Varsayılan hoparlörün ana sesi (Core Audio). Ses aygıtı yoksa <see cref="TryOpen"/> null döner.</summary>
internal sealed class AudioEndpoint
{
    private const int Render = 0;
    private const int Multimedia = 1;
    private const int ClsctxAll = 23;
    private const int KeySteps = 50;
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

    /// <summary>
    /// Ses tuşunun yaptığı iş, Windows'tan ölçüldü: düzey 50 adımlık ızgaraya yuvarlanıp bir adım gider (%33 yukarı %36,
    /// aşağı %32), yukarı sessizden çıkarır, aşağı çıkarmaz, sıfıra inmek sessize alır.
    /// </summary>
    /// <returns>Bir şey değişti mi? Sınırdaki basış değiştirmez.</returns>
    public bool Press(VolumeKey key)
    {
        bool wasMuted = Muted;
        if (key == VolumeKey.Mute)
        {
            Muted = !wasMuted;
            return true;
        }

        float before = Level;
        int direction = key == VolumeKey.Up ? 1 : -1;
        float level = Math.Clamp((MathF.Round(before * KeySteps, MidpointRounding.AwayFromZero) + direction) / KeySteps, 0, 1);
        Level = level;

        bool muted = key == VolumeKey.Up ? false : wasMuted || level <= 0;
        if (muted != wasMuted)
        {
            Muted = muted;
        }
        return level != before || muted != wasMuted;
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

    /// <summary>
    /// Mikrofondan o an ses alan süreçler: etkin kayıt aygıtlarındaki etkin ses oturumları. Kayıt defterindeki
    /// kullanım kaydı bu Windows'ta güvenilmez (Claude'un mikrofonu hiç yazılmadı, ölçüldü); oturum doğrudan sorulur.
    /// Herhangi bir iş parçacığından çağrılabilir.
    /// </summary>
    public static List<int> CaptureProcessIds()
    {
        const int capture = 1;
        const int activeDevices = 1;
        const int activeSession = 1;
        var ids = new List<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(capture, activeDevices, out IMMDeviceCollection devices) != 0 || devices.GetCount(out uint count) != 0)
            {
                return ids;
            }

            Guid managerId = typeof(IAudioSessionManager2).GUID;
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out IMMDevice device) != 0
                    || device.Activate(ref managerId, ClsctxAll, IntPtr.Zero, out object manager) != 0
                    || ((IAudioSessionManager2)manager).GetSessionEnumerator(out IAudioSessionEnumerator sessions) != 0
                    || sessions.GetCount(out int sessionCount) != 0)
                {
                    continue;
                }

                for (int s = 0; s < sessionCount; s++)
                {
                    if (sessions.GetSession(s, out object session) == 0
                        && session is IAudioSessionControl2 control
                        && control.GetState(out int state) == 0 && state == activeSession
                        && control.GetProcessId(out uint processId) == 0 && processId != 0)
                    {
                        ids.Add((int)processId);
                    }
                }
            }
        }
        catch (COMException)
        {
            // Aygıt tam bu sırada çıkarılmış olabilir; sonraki yoklamada yeniden bakılır.
        }
        catch (InvalidCastException)
        {
        }
        return ids;
    }

    /// <summary>
    /// Bir uygulamanın sesinin ölçeri (söz sahnesinin kenar ekolayzırı için): kanal başına tepe değeri, 0..1.
    /// Uygulamanın ses oturumları bütün etkin çıkış aygıtlarında aranır (Spotify varsayılan aygıttan başka yere
    /// çalabiliyor; yalnızca varsayılan aygıt ölçülünce çubuklar çalarken de kıpırdamadı). Oturum yoksa etkin çıkış
    /// aygıtlarının karışımı. Oturumlar birkaç saniyede bir yeniden aranır.
    /// </summary>
    public static Meter OpenMeter(string processName)
    {
        return new Meter(processName);
    }

    internal sealed class Meter(string processName)
    {
        private const int RefreshMs = 3000;
        private List<IAudioMeterInformation> _meters = [];
        // İlk çağrıda hemen aransın (long.MinValue farkı taşırıyordu, hiç aranmıyordu).
        private long _foundAt = -RefreshMs - 1;

        /// <summary>Bulunan ölçer sayısı ve uygulamanın kendi oturumları mı (tanı için).</summary>
        public (int Count, bool OwnSessions) Source { get; private set; }

        /// <summary>Sol ve sağ, ölçerlerin en yükseği (tek kanalda ikisi aynı); bir şey yoksa 0.</summary>
        public (float Left, float Right) Peaks()
        {
            if (Environment.TickCount64 - _foundAt > RefreshMs)
            {
                _foundAt = Environment.TickCount64;
                Find();
            }

            float left = 0;
            float right = 0;
            foreach (IAudioMeterInformation meter in _meters)
            {
                try
                {
                    if (meter.GetMeteringChannelCount(out uint count) != 0 || count == 0)
                    {
                        continue;
                    }

                    float[] values = new float[count];
                    if (meter.GetChannelsPeakValues(count, values) == 0)
                    {
                        left = Math.Max(left, values[0]);
                        right = Math.Max(right, values[count > 1 ? 1 : 0]);
                    }
                }
                catch (COMException)
                {
                    // Aygıt ya da oturum gitti; sonraki aramada listeden düşer.
                }
            }
            return (left, right);
        }

        private void Find()
        {
            var sessions = new List<IAudioMeterInformation>();
            var devices = new List<IAudioMeterInformation>();
            const int activeDevices = 1;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (enumerator.EnumAudioEndpoints(Render, activeDevices, out IMMDeviceCollection collection) != 0 || collection.GetCount(out uint count) != 0)
                {
                    return;
                }

                Guid managerId = typeof(IAudioSessionManager2).GUID;
                Guid meterId = typeof(IAudioMeterInformation).GUID;
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out IMMDevice device) != 0)
                    {
                        continue;
                    }

                    if (device.Activate(ref meterId, ClsctxAll, IntPtr.Zero, out object deviceMeter) == 0)
                    {
                        devices.Add((IAudioMeterInformation)deviceMeter);
                    }

                    if (device.Activate(ref managerId, ClsctxAll, IntPtr.Zero, out object manager) != 0
                        || ((IAudioSessionManager2)manager).GetSessionEnumerator(out IAudioSessionEnumerator list) != 0
                        || list.GetCount(out int sessionCount) != 0)
                    {
                        continue;
                    }

                    for (int s = 0; s < sessionCount; s++)
                    {
                        if (list.GetSession(s, out object session) == 0 && session is IAudioSessionControl2 control
                            && control.GetProcessId(out uint processId) == 0 && IsProcess(processId) && session is IAudioMeterInformation meter)
                        {
                            sessions.Add(meter);
                        }
                    }
                }
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                // Aygıt tam bu sırada değişti; sonraki aramada.
            }

            _meters = sessions.Count > 0 ? sessions : devices;
            Source = (_meters.Count, sessions.Count > 0);
        }

        private bool IsProcess(uint processId)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] values);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
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
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
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

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam([MarshalAs(UnmanagedType.LPStruct)] Guid grouping, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
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
