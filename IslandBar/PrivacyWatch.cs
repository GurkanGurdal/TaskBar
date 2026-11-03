using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IslandBar;

/// <summary>
/// Kamerayı ve mikrofonu o an kullanan uygulamalar (kullanıcının isteği; iPhone'daki gösterge gibi, seçilen yer hızlı
/// ayarlar düğmesi). Kamera: Windows'un izin kaydı, kayıt defterinde kullanım sürerken <c>LastUsedTimeStop</c> 0;
/// paketli uygulamalar doğrudan, masaüstü uygulamaları <c>NonPackaged</c> altında, yolları # ile yazılmış. Bu kayıt her
/// uygulamada tutulmuyor (Claude'un mikrofonu hiç yazılmadı), mikrofon ayrıca ses oturumlarından sorulur
/// (<see cref="AudioEndpoint.CaptureProcessIds"/>). Saniyede bir bakılır; değişince arayüz iş parçacığında bildirilir.
/// </summary>
public sealed class PrivacyWatch
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const int IntervalMs = 1000;

    private readonly Dispatcher _dispatcher;
    private readonly Timer _timer;
    private string _last = string.Empty;

    private PrivacyWatch()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new Timer(_ => Poll(), null, 0, IntervalMs);
    }

    public static PrivacyWatch Shared { get; } = new();

    /// <summary>Kamerayı ve mikrofonu kullananların adları; arayüz iş parçacığında.</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? Changed;

    public IReadOnlyList<string> Camera { get; private set; } = [];

    public IReadOnlyList<string> Microphone { get; private set; } = [];

    private void Poll()
    {
        List<string> camera = InUse("webcam");
        List<string> microphone = InUse("microphone").Union(AudioEndpoint.CaptureProcessIds().Select(ProcessName)).Distinct().ToList();
        string state = string.Join("|", camera) + "/" + string.Join("|", microphone);
        if (state == _last)
        {
            return;
        }

        _last = state;
        _dispatcher.BeginInvoke(() =>
        {
            Camera = camera;
            Microphone = microphone;
            Changed?.Invoke(camera, microphone);
        });
    }

    private static List<string> InUse(string capability)
    {
        var names = new List<string>();
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
            if (root == null)
            {
                return names;
            }

            foreach (string app in root.GetSubKeyNames())
            {
                if (app == "NonPackaged")
                {
                    using RegistryKey? desktop = root.OpenSubKey(app);
                    foreach (string path in desktop?.GetSubKeyNames() ?? [])
                    {
                        using RegistryKey? key = desktop!.OpenSubKey(path);
                        if (IsInUse(key))
                        {
                            names.Add(DesktopName(path.Replace('#', '\\')));
                        }
                    }
                }
                else
                {
                    using RegistryKey? key = root.OpenSubKey(app);
                    if (IsInUse(key))
                    {
                        // Paket adı: "Claude_pzs8sxrjxfjjc" -> "Claude"; yayıncı öneki varsa (Microsoft.WindowsCamera) son parça.
                        string name = app.Split('_')[0];
                        names.Add(name[(name.LastIndexOf('.') + 1)..]);
                    }
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Okunamadıysa gösterge boş kalır.
        }
        return names.Distinct().ToList();
    }

    private static bool IsInUse(RegistryKey? key)
    {
        return key?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && key.GetValue("LastUsedTimeStart") is long start && start > 0;
    }

    /// <summary>Ses oturumunun süreci; adı exe açıklamasından (Claude, Google Chrome).</summary>
    private static string ProcessName(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.MainModule?.FileName is { } path ? DesktopName(path) : process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "Bir uygulama";
        }
    }

    /// <summary>Exe'nin açıklaması (Google Chrome), yoksa dosya adı.</summary>
    private static string DesktopName(string path)
    {
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description)
            {
                return description;
            }
        }
        catch (FileNotFoundException)
        {
        }
        return Path.GetFileNameWithoutExtension(path);
    }
}
