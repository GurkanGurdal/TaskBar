using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IslandBar;

/// <summary>
/// Başlatanın işinin (job) dışında çalışma. Terminal, IDE ya da Claude gibi bir ajan başlattığı süreçleri bir işin içine
/// koyar, kapanırken işi topluca sonlandırır: çubuk ve bekçi birlikte, hiçbir işleyici çalışmadan ölür, kayıt düşmez,
/// Windows çubuğu geri gelmez, çubuk yeniden başlamaz (kullanıcı yaşadı: çubuk Claude Code'un kabuğundan yeniden
/// başlatılmıştı, Claude uygulaması kapanınca gitti). İşten ayrılma bayrağı (<c>CREATE_BREAKAWAY_FROM_JOB</c>) yalnızca
/// en içteki işten çıkarıyor, ölçüldü: süreç Claude uygulamasının dış işinde kalıyordu. Bu yüzden çubuğu Gezgin başlatır,
/// Windows'la ya da kısayoldan açılmış gibi (masaüstünün kabuk görünümünün <c>ShellExecute</c>'u; Gezgin'e başka hiçbir
/// şey yapılmaz).
/// </summary>
internal static class JobEscape
{
    private const uint BreakawayFromJob = 0x01000000;
    private const string RelaunchedVariable = "ISLANDBAR_OUT_OF_JOB";
    private const int DesktopLocation = 0;
    private const int DesktopClass = 8;
    private const int NeedDispatch = 1;
    private const uint Background = 0;
    private const int ShowNormal = 1;

    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserId = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid DispatchId = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// Çubuğu Gezgin açmadıysa (terminal, IDE, ajan) kendini Gezgin'e aynı argümanlarla yeniden başlattırır; true ise bu
    /// süreç hemen çıkmalı. Olmazsa en azından en içteki işten ayrılarak başlar; o da olmazsa burada sürer.
    /// Debug derlemesinde yapılmaz: hata ayıklayıcı yeni kopyaya bağlı olmaz.
    /// </summary>
    public static bool RelaunchOutside()
    {
#if DEBUG
        return false;
#else
        // İşten ayrılarak başlamış kopya: işaret çubuğun açtığı uygulamalara geçmesin.
        if (Environment.GetEnvironmentVariable(RelaunchedVariable) != null)
        {
            Environment.SetEnvironmentVariable(RelaunchedVariable, null);
            return false;
        }

        if (ParentName() is "explorer" or null)
        {
            return false;
        }

        // Environment.CommandLine exe'yi değil IslandBar.dll'i verir; Windows onu eski .NET ile açıp düşürüyordu.
        string arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(argument => $"\"{argument}\""));
        if (ShellExecuteByExplorer(Environment.ProcessPath!, arguments))
        {
            return true;
        }

        Environment.SetEnvironmentVariable(RelaunchedVariable, "1");
        bool started = Start($"\"{Environment.ProcessPath}\" {arguments}");
        if (!started)
        {
            Environment.SetEnvironmentVariable(RelaunchedVariable, null);
        }
        return started;
#endif
    }

    /// <summary>Süreci en içteki işin dışında başlatır (bekçi çubukla birlikte ölmesin); iş izin vermiyorsa false.</summary>
    public static bool Start(string commandLine)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, BreakawayFromJob, IntPtr.Zero,
            Environment.CurrentDirectory, ref startup, out ProcessInformation process))
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"JobEscape: could not break away: {new Win32Exception().Message}");
            return false;
        }

        CloseHandle(process.Thread);
        CloseHandle(process.Process);
        return true;
    }

    /// <summary>Masaüstünün kabuk görünümünden: süreci Gezgin oluşturur, bizim işimizden bağımsız.</summary>
    private static bool ShellExecuteByExplorer(string path, string arguments)
    {
        try
        {
            var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass)!)!;
            object location = DesktopLocation;
            object? root = null;
            if (windows.FindWindowSW(ref location, ref root, DesktopClass, out _, NeedDispatch) is not IServiceProvider desktop)
            {
                return false;
            }

            Guid service = TopLevelBrowser;
            Guid id = ShellBrowserId;
            if (desktop.QueryService(ref service, ref id, out object found) != 0 || found is not IShellBrowser browser
                || browser.QueryActiveShellView(out IShellView view) != 0)
            {
                return false;
            }

            Guid dispatch = DispatchId;
            if (view.GetItemObject(Background, ref dispatch, out object folderView) != 0)
            {
                return false;
            }

            dynamic shell = ((dynamic)folderView).Application;
            shell.ShellExecute(path, arguments, Environment.CurrentDirectory, "open", ShowNormal);
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"JobEscape: explorer could not start the bar: {e.Message}");
            return false;
        }
    }

    private static string? ParentName()
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
            {
                return null;
            }

            using Process parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            return parent.ProcessName.ToLowerInvariant();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Üst süreç çoktan bitmiş.
            return null;
        }
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object Item([MarshalAs(UnmanagedType.Struct)] object index);

        // Sıra arayüzdeki gibi; kullanılmayanlar yer tutar.
        void _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object? root,
            int windowClass, out int window, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();

        [PreserveSig]
        int GetItemObject(uint item, ref Guid id, [MarshalAs(UnmanagedType.IDispatch)] out object result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int Columns;
        public int Rows;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);
}
