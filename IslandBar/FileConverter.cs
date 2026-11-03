using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IslandBar;

/// <summary>Kartta bir dönüştürme: bir ya da birkaç dosya, tek hedef. Durumu yalnızca arayüz iş parçacığında değişir.</summary>
internal sealed class ConversionJob(IReadOnlyList<string> files, ConversionTarget target, bool keepOriginals)
{
    public IReadOnlyList<string> Files { get; } = files;
    public ConversionTarget Target { get; } = target;

    /// <summary>Kullanıcıya soruldu; hayır denirse dönüşen dosyaların orijinali Geri Dönüşüm Kutusu'na gider.</summary>
    public bool KeepOriginals { get; } = keepOriginals;

    /// <summary>0..1; null: ilerleme bilinmiyor (Office, tek dosya).</summary>
    public double? Progress { get; set; }

    public List<(string Source, string Output)> Converted { get; } = [];
    public int FailedCount { get; set; }
    public string? Problem { get; set; }
}

/// <summary>Dönüştürme kartının işi: dosyalar sırayla, sonra istenirse orijinaller silinir.</summary>
internal static class FileConverter
{
    private const int ProgressIntervalMs = 100;
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
                job.Converted.Add((file, await FileConversions.ConvertAsync(file, job.Target, progress, CancellationToken.None)));
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

        if (!job.KeepOriginals && job.Converted.Count > 0)
        {
            Recycle(job.Converted.Select(pair => pair.Source));
        }
    }

    /// <summary>Gezgin'de seçili gösterir (o klasörün açık penceresi varsa o kullanılır).</summary>
    public static void Reveal(string path)
    {
        IntPtr item = ILCreateFromPathW(path);
        if (item == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            return;
        }

        try
        {
            SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0);
        }
        finally
        {
            ILFree(item);
        }
    }

    /// <summary>Kalıcı silme değil, Geri Dönüşüm Kutusu (kullanıcının seçimi): yanlışlıkla "Hayır" denirse geri alınır.</summary>
    private static void Recycle(IEnumerable<string> files)
    {
        var operation = new FileOperation
        {
            Function = Delete,
            From = string.Join('\0', files) + '\0',
            Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorUi)
        };
        int result = SHFileOperation(ref operation);
        if (result != 0)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"FileConverter: orijinaller silinemedi ({result})");
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

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32")]
    private static extern void ILFree(IntPtr item);

    [DllImport("shell32")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
}
