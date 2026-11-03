using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;

namespace IslandBar;

/// <summary>
/// IslandBar'ın Windows görev çubuğunda değiştirdiklerini, IslandBar temizlik yapamadan ölse de geri alır.
/// Durum dosyası çalışma boyunca durur; varsa önceki çalışma temiz kapanmamıştır.
/// </summary>
internal static class TaskbarRecovery
{
    public const string InstanceMutexName = @"Local\IslandBar.Instance";

    private const string WatchdogArgument = "--watchdog";

    /// <summary>Bu süre içinde en çok bu kadar yeniden başlatılır: açılışta çöken sürüm sonsuza dek dönmesin.</summary>
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(5);

    /// <summary>MB_OK, MB_ICONWARNING, MB_SETFOREGROUND, MB_TOPMOST: bekçinin penceresi yok, kutu arkada kalmasın.</summary>
    private const uint WarningBoxFlags = 0x00000030 | 0x00010000 | 0x00040000;

    private const uint FirstErrorCode = 0x80000000;
    private const uint TerminatedCode = 0xFFFFFFFF;
    private const uint ControlCCode = 0xC000013A;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "taskbar-state.txt");

    private static readonly string RestartRequestFile = Path.Combine(Path.GetDirectoryName(StateFile)!, "restart-requested");
    private static readonly string RestartHistoryFile = Path.Combine(Path.GetDirectoryName(StateFile)!, "restarts.txt");

    [System.Runtime.InteropServices.DllImport("user32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>Komut satırı bekçi süreci içinse izler, geri alır ve true döner.</summary>
    public static bool RunWatchdogIfRequested()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length != 3 || args[1] != WatchdogArgument || !int.TryParse(args[2], out int processId))
        {
            return false;
        }

        int? exitCode = null;
        try
        {
            using Process process = Process.GetProcessById(processId);

            // Tanıtıcı süreç ölmeden açılmalı: yoksa WaitForExit onu kapatır, çıkış kodu sonradan okunamaz.
            _ = process.Handle;
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Süreç çoktan bitmiş.
        }

        // Bu arada yeni bir IslandBar açıldıysa geri alma işi onun.
        if (Mutex.TryOpenExisting(InstanceMutexName, out Mutex? newInstance))
        {
            newInstance.Dispose();
            return true;
        }

        RestoreIfRecorded();

        // Explorer çubuğu, IslandBar kapandıktan kısa süre sonra yeniden alta indirebiliyor; temiz kapanışta da.
        Thread.Sleep(1500);
        RaiseWindowsTaskbar();

        if (ShouldRestart(exitCode))
        {
            Restart();
        }
        return true;
    }

    /// <summary>Çubuk hata yüzünden kapanıyor; bekçi yeniden başlatsın. Temiz kapanışla ayrılmaz, ikisi de kod 0 ile biter.</summary>
    public static void RequestRestart()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestartRequestFile)!);
            File.WriteAllText(RestartRequestFile, string.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not write {RestartRequestFile}: {e.Message}");
        }
    }

    /// <summary>Önceki çalışmadan kalan istek bu çalışmanın temiz kapanışında yeniden başlatmasın.</summary>
    public static void ClearRestartRequest()
    {
        try
        {
            File.Delete(RestartRequestFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not delete {RestartRequestFile}: {e.Message}");
        }
    }

    private static bool ShouldRestart(int? exitCode)
    {
        bool requested = File.Exists(RestartRequestFile);
        ClearRestartRequest();
        if (requested)
        {
            return true;
        }

        // Yerel çöküşte hiçbir işleyici çalışmaz, iz yalnızca çıkış kodunda kalır. Görev Yöneticisi (1) ve
        // Stop-Process (-1) de buraya gelir: kullanıcı kendisi kapatmış, yeniden açılmamalı.
        uint code = unchecked((uint)(exitCode ?? 0));
        if (code < FirstErrorCode || code is TerminatedCode or ControlCCode)
        {
            return false;
        }

        App.WriteCrashLog($"Süreç 0x{code:X8} koduyla kapandı.");
        return true;
    }

    private static void Restart()
    {
        if (!TryRecordRestart())
        {
            App.WriteCrashLog("Art arda kapandığı için yeniden başlatılmadı.");
            MessageBoxW(IntPtr.Zero,
                "IslandBar kısa sürede art arda kapandığı için yeniden başlatılmadı. Windows görev çubuğu geri getirildi.\n\n" +
                $"Ayrıntı: {App.CrashLogPath}",
                "IslandBar", WarningBoxFlags);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.WriteCrashLog(e);
        }
    }

    /// <summary>Kayıt yazılamıyorsa sınır da uygulanamaz; yeniden başlatılmaz.</summary>
    private static bool TryRecordRestart()
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            List<DateTime> recent = (File.Exists(RestartHistoryFile) ? File.ReadAllLines(RestartHistoryFile) : [])
                .Select(line => long.TryParse(line, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue)
                .Where(time => now - time < RestartWindow)
                .ToList();
            if (recent.Count >= MaxRestarts)
            {
                return false;
            }

            recent.Add(now);
            File.WriteAllLines(RestartHistoryFile, recent.Select(time => time.Ticks.ToString()));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not update {RestartHistoryFile}: {e.Message}");
            return false;
        }
    }

    public static void StartWatchdog()
    {
        // Çubuk bir işin içinde kaldıysa bekçi işin dışında: iş topluca sonlandırılınca geri alacak kimse kalmıyordu.
        if (JobEscape.Start($"\"{Environment.ProcessPath}\" {WatchdogArgument} {Environment.ProcessId}"))
        {
            return;
        }

        var startInfo = new ProcessStartInfo(Environment.ProcessPath!, $"{WatchdogArgument} {Environment.ProcessId}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Bekçi olmadan da çalışılır; zorla öldürülürse sonraki açılış geri alır.
            ShellLogger.Error($"TaskbarRecovery: Could not start watchdog: {e.Message}");
        }
    }

    /// <summary>ShellManager'dan önce çağrılmalı: tepsi servisi başlayınca durum sorguları ona gider.</summary>
    public static void RecordState()
    {
        if (File.Exists(StateFile))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, ((int)new ExplorerHelper().GetTaskbarState()).ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not write {StateFile}: {e.Message}");
        }
    }

    public static void ClearRecord()
    {
        try
        {
            File.Delete(StateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not delete {StateFile}: {e.Message}");
        }
    }

    public static void RestoreIfRecorded()
    {
        var explorerHelper = new ExplorerHelper();

        // Her ihtimale karşı kayıt olmasa da çubuğu göster.
        explorerHelper.SetTaskbarVisibility((int)NativeMethods.SetWindowPosFlags.SWP_SHOWWINDOW);

        string? recorded;
        try
        {
            recorded = File.Exists(StateFile) ? File.ReadAllText(StateFile) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShellLogger.Error($"TaskbarRecovery: Could not read {StateFile}: {e.Message}");
            return;
        }

        // Explorer çalışmıyorsa geri alacak bir şey yok; kayıt sonraki açılışa kalsın.
        IntPtr windowsTray = WindowHelper.FindWindowsTray(IntPtr.Zero);
        if (recorded == null || windowsTray == IntPtr.Zero)
        {
            return;
        }

        explorerHelper.SetTaskbarState(int.TryParse(recorded, out int state) ? (NativeMethods.ABState)state : NativeMethods.ABState.Default);
        RaiseWindowsTaskbar();
        RestoreTaskbarList(windowsTray);

        // Uygulamalar tepsi ikonlarını Windows çubuğuna yeniden eklesin.
        NativeMethods.SendNotifyMessage(NativeMethods.HWND_BROADCAST,
            (uint)NativeMethods.RegisterWindowMessage("TaskbarCreated"), UIntPtr.Zero, IntPtr.Zero);

        ClearRecord();
    }

    /// <summary>Tepsi servisi ve ExplorerHelper Windows çubuklarını en alta indiriyor, kendileri geri almıyor.</summary>
    public static void RaiseWindowsTaskbar()
    {
        Raise(WindowHelper.FindWindowsTray(IntPtr.Zero));

        IntPtr secondaryTray = NativeMethods.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
        while (secondaryTray != IntPtr.Zero)
        {
            Raise(secondaryTray);
            secondaryTray = NativeMethods.FindWindowEx(IntPtr.Zero, secondaryTray, "Shell_SecondaryTrayWnd", null);
        }
    }

    private static void Raise(IntPtr tray)
    {
        if (tray == IntPtr.Zero)
        {
            return;
        }

        // Tek başına HWND_TOPMOST bu durumda etkisiz kalıyor (bayrak değişmiyor); önce kaldırmak gerekiyor.
        const int flags = (int)(NativeMethods.SetWindowPosFlags.SWP_NOMOVE | NativeMethods.SetWindowPosFlags.SWP_NOSIZE | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);
        NativeMethods.SetWindowPos(tray, (IntPtr)NativeMethods.WindowZOrder.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        NativeMethods.SetWindowPos(tray, (IntPtr)NativeMethods.WindowZOrder.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    private static void RestoreTaskbarList(IntPtr windowsTray)
    {
        // Görev servisi ITaskbarList mesajlarını (ilerleme, rozet) kendine yönlendiriyor.
        IntPtr taskList = IntPtr.Zero;
        NativeMethods.EnumChildWindows(windowsTray, (hwnd, _) =>
        {
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != "MSTaskSwWClass")
            {
                return true;
            }

            taskList = hwnd;
            return false;
        }, 0);

        if (taskList != IntPtr.Zero)
        {
            NativeMethods.SetProp(windowsTray, "TaskbandHWND", taskList);
        }
    }
}
