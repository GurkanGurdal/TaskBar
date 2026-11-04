using System.IO;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>Kartta bir dönüştürme: bir ya da birkaç dosya, tek hedef. Durumu yalnızca arayüz iş parçacığında değişir.</summary>
internal sealed class ConversionJob(IReadOnlyList<string> files, ConversionTarget target)
{
    public IReadOnlyList<string> Files { get; } = files;
    public ConversionTarget Target { get; } = target;

    /// <summary>0..1; null: ilerleme bilinmiyor (Office, tek dosya).</summary>
    public double? Progress { get; set; }

    public List<(string Source, string Output)> Converted { get; } = [];
    public int FailedCount { get; set; }
    public string? Problem { get; set; }
}
/// <summary>
/// Dönüştürme kartının işi (kullanıcının kararı): kullanıcının dosyalarına dokunulmaz, yeni dosyalar IslandBar'ın kendi
/// klasörüne (<see cref="Root"/>) yazılır ve kutuda durur; kullanıcı onları kutudan sürükleyip istediği yere taşır.
/// Kutudaki bir dosya yeniden dönüştürülürse eskisi (bizim dosyamız) Geri Dönüşüm Kutusu'na gider.
/// </summary>
internal static class FileConverter
{
    private const int ProgressIntervalMs = 100;

    /// <summary>Dönüşen dosyaların bekleme yeri; her iş kendi alt klasöründe.</summary>
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandBar", "Dönüştürülenler");
    private const uint Delete = 3;
    private const ushort Silent = 0x4;
    private const ushort NoConfirmation = 0x10;
    private const ushort AllowUndo = 0x40;
    private const ushort NoErrorUi = 0x400;

    /// <param name="changed">İlerleme değişti; arayüz iş parçacığında, saniyede en çok on kez.</param>
    public static async Task RunAsync(ConversionJob job, Action changed)
    {
        job.Progress = job.Files.Count == 1 && job.Target.Kind is not (ConversionKind.Media or ConversionKind.PdfPages) ? null : 0;
        changed();
        string folder = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        if (job.Target.Kind == ConversionKind.ImagesPdf)
        {
            await CombineAsync(job, folder, changed);
            return;
        }

        long lastReport = 0;
        for (int i = 0; i < job.Files.Count; i++)
        {
            string file = job.Files[i];
            if (FileConversions.IsAlready(file, job.Target))
            {
                continue;
            }

            int index = i;
            var progress = new Progress<double>(fraction =>
            {
                // Dönüştürücüler sık bildiriyor; sonuncusu düşmesin (halka %81'de kalıyordu).
                job.Progress = (index + fraction) / job.Files.Count;
                long now = Environment.TickCount64;
                if (now - lastReport >= ProgressIntervalMs || fraction >= 0.99)
                {
                    lastReport = now;
                    changed();
                }
            });

            try
            {
                job.Converted.Add((file, await FileConversions.ConvertAsync(file, job.Target, folder, progress, CancellationToken.None)));
            }
            catch (Exception e)
            {
                job.FailedCount++;
                job.Problem = Reason(e, job.Target);
                ManagedShell.Common.Logging.ShellLogger.Warning($"FileConverter: {file} → {job.Target.Extension}: {e}");
            }

            if (job.Progress != null)
            {
                job.Progress = (i + 1) / (double)job.Files.Count;
                changed();
            }
        }

        // Kutudaki eski hali bizim dosyamızdı (yeniden dönüştürüldü); kullanıcının dosyası yerinde kalır.
        Recycle(job.Converted.Select(pair => pair.Source).Where(IsOurs));
        RemoveEmptyFolders();
    }

    /// <summary>Bütün resimler tek PDF'te: her kaynak aynı dosyaya dönüşmüş sayılır.</summary>
    private static async Task CombineAsync(ConversionJob job, string folder, Action changed)
    {
        job.Progress = 0;
        var progress = new Progress<double>(fraction =>
        {
            job.Progress = fraction;
            changed();
        });

        try
        {
            string output = await FileConversions.CombineImagesAsync(job.Files, folder, progress);
            job.Converted.AddRange(job.Files.Select(file => (file, output)));
        }
        catch (Exception e)
        {
            job.FailedCount = job.Files.Count;
            job.Problem = Reason(e, job.Target);
            ManagedShell.Common.Logging.ShellLogger.Warning($"FileConverter: {job.Files.Count} resim → tek PDF: {e}");
        }

        Recycle(job.Converted.Select(pair => pair.Source).Where(IsOurs));
        RemoveEmptyFolders();
    }

    /// <summary>Dosya IslandBar'ın bekleme klasöründe mi (kutudan alınmamış bir dönüşüm sonucu).</summary>
    public static bool IsOurs(string path)
    {
        return Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Boşalan iş klasörleri kaldırılır (dosyalar kutudan taşındı ya da silindi).</summary>
    public static void RemoveEmptyFolders()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (string folder in Directory.EnumerateDirectories(Root))
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kullanımda olan klasör sonra kaldırılır.
        }
    }

    /// <summary>Kalıcı silme değil, Geri Dönüşüm Kutusu: kutuda bırakılan dosyalar (kullanıcının seçimi) geri alınabilir.</summary>
    public static void Recycle(IEnumerable<string> files)
    {
        List<string> list = files.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var operation = new FileOperation
        {
            Function = Delete,
            From = string.Join('\0', list) + '\0',
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorUi)
        };
        int result = SHFileOperation(ref operation);
        if (result != 0)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"FileConverter: dosyalar silinemedi ({result})");
        }
    }

    /// <summary>Kısa, kullanıcının anlayacağı neden; ayrıntı kayıtta.</summary>
    private static string Reason(Exception e, ConversionTarget target)
    {
        string app = target.Kind switch
        {
            ConversionKind.Word => "Word",
            ConversionKind.Excel => "Excel",
            ConversionKind.PowerPoint => "PowerPoint",
            _ => ""
        };
        return e switch
        {
            ConversionException => e.Message,
            UnauthorizedAccessException => "klasöre yazılamıyor",
            IOException => "dosya kullanımda",
            COMException { HResult: unchecked((int)0x80040154) } => "Office bulunamadı",
            COMException when app != "" => $"{app} dosyayı açamadı",
            _ => "biçim okunamadı"
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        public bool AnyAborted;
        public IntPtr NameMappings;
        public string? Title;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref FileOperation operation);
}
