using System.Runtime.InteropServices;

namespace GrupBar;

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
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out IMMDevice device) != 0)
            {
                return null;
            }

            Guid iid = VolumeInterface;
            return device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object volume) == 0
                ? new AudioEndpoint((IAudioEndpointVolume)volume)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
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
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
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
