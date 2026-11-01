using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>
/// Eşleşmiş Bluetooth ses aygıtına bağlanma ve bağlantıyı kesme. Windows'ta aygıta bağlanmak için genel bir API yok;
/// ses aygıtlarında sürücünün kendi komutu kullanılır (KSPROPSETID_BtAudio). Klavye, fare, kumanda gibi aygıtlarda bu yol yoktur.
/// Çağrılar sürücüyü bekletebilir, arayüz iş parçacığında çalıştırma.
/// </summary>
internal static class BluetoothAudio
{
    private const int All = 2;
    private const int AllStates = 0xF;
    private const int ClsctxAll = 23;
    private const int ClsidVariant = 72;
    private const int ReconnectProperty = 0;
    private const int DisconnectProperty = 1;
    private const int PropertyGet = 1;
    private const int PropertyBasicSupport = 0x200;

    private static readonly Guid BtAudioProperties = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    private static readonly Guid TopologyInterface = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid KsControlInterface = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly PropertyKey ContainerIdKey = new() { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };

    /// <summary>Bu kapsayıcıdaki aygıt sürücünün komutunu tanıyor mu? Yalnızca sorar, bağlantıya dokunmaz.</summary>
    public static bool IsSupported(Guid container)
    {
        foreach (IKsControl control in Filters(container))
        {
            var property = new KsProperty { Set = BtAudioProperties, Id = ReconnectProperty, Flags = PropertyBasicSupport };
            IntPtr access = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                if (control.Property(ref property, Marshal.SizeOf<KsProperty>(), access, sizeof(uint), out _) == 0)
                {
                    return true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(access);
            }
        }
        return false;
    }

    /// <summary>Başarı yalnızca denendiğini gösterir; sonucu aygıtın bağlantı durumundan anla.</summary>
    public static bool Connect(Guid container) => Send(container, ReconnectProperty);

    public static bool Disconnect(Guid container) => Send(container, DisconnectProperty);

    private static bool Send(Guid container, int id)
    {
        bool sent = false;
        foreach (IKsControl control in Filters(container))
        {
            var property = new KsProperty { Set = BtAudioProperties, Id = id, Flags = PropertyGet };
            sent |= control.Property(ref property, Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _) == 0;
        }
        return sent;
    }

    /// <summary>Aygıtın ses uçlarının (hoparlör, kulaklık) bağlı olduğu sürücü süzgeçleri; her biri bir kez.</summary>
    private static List<IKsControl> Filters(Guid container)
    {
        var filters = new List<IKsControl>();
        var seen = new HashSet<string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new AudioEndpoint.MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(All, AllStates, out IMMDeviceCollection endpoints) != 0 || endpoints.GetCount(out uint count) != 0)
            {
                return filters;
            }

            for (uint i = 0; i < count; i++)
            {
                if (endpoints.Item(i, out IMMDevice endpoint) == 0
                    && ContainerOf(endpoint) == container
                    && FilterIdOf(endpoint) is { } filterId
                    && seen.Add(filterId)
                    && enumerator.GetDevice(filterId, out IMMDevice filter) == 0)
                {
                    Guid iid = KsControlInterface;
                    if (filter.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object control) == 0)
                    {
                        filters.Add((IKsControl)control);
                    }
                }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // Ses hizmeti yoksa ya da beklenen arayüz çıkmazsa bu aygıt yönetilemez sayılır; uygulamayı düşürmesin.
        }
        return filters;
    }

    private static Guid? ContainerOf(IMMDevice endpoint)
    {
        if (endpoint.OpenPropertyStore(0, out IPropertyStore store) != 0)
        {
            return null;
        }

        PropertyKey key = ContainerIdKey;
        if (store.GetValue(ref key, out PropVariant value) != 0)
        {
            return null;
        }

        try
        {
            return value.Type == ClsidVariant ? Marshal.PtrToStructure<Guid>(value.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static string? FilterIdOf(IMMDevice endpoint)
    {
        Guid iid = TopologyInterface;
        if (endpoint.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object instance) != 0
            || ((IDeviceTopology)instance).GetConnector(0, out IConnector connector) != 0
            || connector.GetDeviceIdConnectedTo(out IntPtr text) != 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [DllImport("ole32")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public int Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public int Id;
        public int Flags;
    }

    /// <summary>Yöntem sırası COM tablosuyla aynı olmalı; kullanılmayanlar yer tutar.</summary>
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo(out IntPtr id);
        [PreserveSig] int GetDeviceIdConnectedTo(out IntPtr id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int Property(ref KsProperty property, int propertyLength, IntPtr data, int dataLength, out int returned);
    }
}
