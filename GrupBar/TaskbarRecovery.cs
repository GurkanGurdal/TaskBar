using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;

namespace GrupBar;

/// <summary>
/// GrupBar'ın Windows görev çubuğunda değiştirdiklerini, GrupBar temizlik yapamadan ölse de geri alır.
/// Durum dosyası çalışma boyunca durur; varsa önceki çalışma temiz kapanmamıştır.
/// </summary>
internal static class TaskbarRecovery
{
    public const string InstanceMutexName = @"Local\GrupBar.Instance";

    private const string WatchdogArgument = "--watchdog";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrupBar", "taskbar-state.txt");

    /// <summary>Komut satırı bekçi süreci içinse izler, geri alır ve true döner.</summary>
    public static bool RunWatchdogIfRequested()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length != 3 || args[1] != WatchdogArgument || !int.TryParse(args[2], out int processId))
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch (ArgumentException)
        {
            // Süreç çoktan bitmiş.
        }

        // Bu arada yeni bir GrupBar açıldıysa geri alma işi onun.
        if (Mutex.TryOpenExisting(InstanceMutexName, out Mutex? newInstance))
        {
            newInstance.Dispose();
            return true;
        }

        RestoreIfRecorded();

        // Explorer çubuğu, GrupBar kapandıktan kısa süre sonra yeniden alta indirebiliyor; temiz kapanışta da.
        Thread.Sleep(1500);
        RaiseWindowsTaskbar();
        return true;
    }

    public static void StartWatchdog()
    {
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
