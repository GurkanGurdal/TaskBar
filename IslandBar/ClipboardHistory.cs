using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell.Common.Logging;

namespace IslandBar;

/// <summary>Kopyalanmış bir metin; listede tek satır olarak görünür, tam hali ipucunda.</summary>
public sealed class CopiedText(string text, DateTime copiedAt)
{
    private const int PreviewLength = 120;
    private const int ToolTipLength = 600;

    public string Text { get; } = text;

    public DateTime CopiedAt { get; } = copiedAt;

    /// <summary>Satır sonları ve art arda boşluklar tek boşluk: listede tek satır.</summary>
    public string Preview { get; } = Shorten(Regex.Replace(text.Trim(), @"\s+", " "), PreviewLength);

    public string ToolTipText { get; } = Shorten(text.Trim(), ToolTipLength);

    public string TimeLabel => CopiedAt.ToString("HH:mm");

    private static string Shorten(string text, int length)
    {
        return text.Length > length ? text[..length].TrimEnd() + "…" : text;
    }
}

/// <summary>
/// Kopyalanan son metinler (kullanıcının isteği; ekran görüntüsü geçmişinin metin karşılığı). Yalnızca bellekte tutulur,
/// diske yazılmaz (kullanıcının seçimi): IslandBar kapanınca liste boşalır, kopyalanan parola diskte kalmaz. Parola
/// yöneticilerinin işaretlediği içerik alınmaz. Aynı metin yeniden kopyalanınca başa geçer.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxItems = 10;

    private const int WmClipboardUpdate = 0x031D;
    private const int SettleMs = 150;
    private const int ReadAttempts = 5;
    private const int RetryMs = 60;
    private const int MaxLength = 1_000_000;

    private readonly DispatcherTimer _settleTimer = new() { Interval = TimeSpan.FromMilliseconds(SettleMs) };
    private HwndSource? _window;

    private ClipboardHistory()
    {
        _settleTimer.Tick += SettleTimer_Tick;
    }

    public static ClipboardHistory Shared { get; } = new();

    public ObservableCollection<CopiedText> Items { get; } = new();

    /// <summary>Ayar kapalıyken Araçlar kartında bölüm hiç görünmez.</summary>
    public bool IsWatching => _window != null;

    /// <summary>Kapatınca liste de boşalır: kullanıcı tutulmasını istemiyor.</summary>
    public void SetWatching(bool watching)
    {
        if (watching && _window == null)
        {
            // Yalnızca mesaj alan pencere; ekranda hiç yok.
            const int messageOnly = -3;
            _window = new HwndSource(new HwndSourceParameters("IslandBar.CopiedText") { ParentWindow = messageOnly, WindowStyle = 0 });
            _window.AddHook(Window_Message);
            AddClipboardFormatListener(_window.Handle);
        }
        else if (!watching && _window != null)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.Dispose();
            _window = null;
            _settleTimer.Stop();
            Items.Clear();
        }
    }

    public void Remove(CopiedText item)
    {
        Items.Remove(item);
    }

    public void Clear()
    {
        Items.Clear();
    }

    /// <summary>Panoya yeniden koyar; izleyici onu yeni kopya gibi başa alır.</summary>
    public void CopyToClipboard(CopiedText item)
    {
        try
        {
            Clipboard.SetText(item.Text);
        }
        catch (COMException e)
        {
            ShellLogger.Error($"ClipboardHistory: Could not copy: {e.Message}");
        }
    }

    private IntPtr Window_Message(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // Uygulamalar panoya birkaç biçimi art arda yazıyor; durulunca bir kez okunur.
            _settleTimer.Stop();
            _settleTimer.Start();
        }
        return IntPtr.Zero;
    }

    private async void SettleTimer_Tick(object? sender, EventArgs e)
    {
        _settleTimer.Stop();

        // Pano o an başkasında olabilir.
        for (int attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText() || ScreenshotHistory.IsMarkedPrivate())
                {
                    return;
                }

                string text = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaxLength)
                {
                    Add(text);
                }
                return;
            }
            catch (COMException)
            {
                await Task.Delay(RetryMs);
            }
        }
    }

    private void Add(string text)
    {
        if (Items.FirstOrDefault(item => item.Text == text) is { } existing)
        {
            Items.Remove(existing);
        }

        Items.Insert(0, new CopiedText(text, DateTime.Now));
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    [DllImport("user32", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);
}
