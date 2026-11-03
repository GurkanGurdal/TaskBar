using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace IslandBar;

/// <summary>Seçili yazı ve biliniyorsa ekrandaki yeri (piksel).</summary>
internal sealed record SelectedText(string Text, Rect? Bounds);

/// <summary>
/// Başka bir uygulamada seçili yazıyı okur. Önce UI Automation (panoya dokunmaz; tarayıcılar, Word, çoğu metin kutusu);
/// vermezse Ctrl + C: pano yedeklenir, kopya okunur, pano geri konur, Kopyalananlar listesine girmez. Ctrl + C konsolda
/// işi durdurur, Dosya Gezgini'nde dosya kopyalar: bu pencerelerde ve tuş basılıyken gönderilmez.
/// </summary>
internal static class SelectionReader
{
    private const int AutomationTimeoutMs = 600;
    private const int CopyTimeoutMs = 400;
    private const int CopyPollMs = 20;

    /// <summary>Ctrl + C'nin işi bozabileceği pencereler.</summary>
    private static readonly HashSet<string> NoCopyClasses =
    [
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "PuTTY", "VirtualConsoleClass",
        "CabinetWClass", "ExploreWClass", "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    ];

    /// <param name="at">Seçimin başladığı ya da bittiği yer, ekran pikseli.</param>
    public static async Task<SelectedText?> ReadAsync(IntPtr window, System.Drawing.Point at)
    {
        // Bir fazlası: sınırı geçtiği anlaşılsın ("ilk 300 karakter").
        Task<SelectedText?> automation = Task.Run(() => FromAutomation(at, Translator.MaxLength + 1, withBounds: true));
        if (await Task.WhenAny(automation, Task.Delay(AutomationTimeoutMs)) == automation && automation.Result is { } selected)
        {
            return selected;
        }

        return await FromCopyAsync(window);
    }

    /// <summary>
    /// Sürüklerken sayaç için seçimin uzunluğu, yalnızca UI Automation'dan (panoya dokunulmaz); okunamazsa null.
    /// <paramref name="limit"/> karakterden fazlası okunmaz.
    /// </summary>
    /// <param name="at">Sürüklemenin başladığı yer, ekran pikseli.</param>
    public static int? CountSelected(System.Drawing.Point at, int limit)
    {
        return FromAutomation(at, limit, withBounds: false)?.Text.Length;
    }

    /// <summary>
    /// Seçim önce basılan yerdeki yazı öğesinden okunur, olmazsa odaktaki öğeden: Claude gibi uygulamalarda mesajdaki
    /// yazı seçilirken odak yazma kutusunda kalıyor, odaktan okunan seçim boş geliyordu.
    /// </summary>
    private static SelectedText? FromAutomation(System.Drawing.Point? at, int limit, bool withBounds)
    {
        try
        {
            foreach (TextPattern pattern in Patterns(at))
            {
                var text = new StringBuilder();
                Rect? bounds = null;
                foreach (TextPatternRange range in pattern.GetSelection())
                {
                    text.Append(range.GetText(Math.Max(0, limit - text.Length)));
                    if (withBounds)
                    {
                        foreach (Rect line in range.GetBoundingRectangles())
                        {
                            bounds = bounds is { } union ? Rect.Union(union, line) : line;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(text.ToString()))
                {
                    return new SelectedText(text.ToString(), bounds);
                }
            }
            return null;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Noktadaki öğeden yukarı ilk metin kalıbı, sonra odaktaki öğeninki; IslandBar'ın kendi pencereleri hariç.</summary>
    private static IEnumerable<TextPattern> Patterns(System.Drawing.Point? at)
    {
        const int maxParentSteps = 8;
        if (at is { } point && AutomationElement.FromPoint(new Point(point.X, point.Y)) is { } element)
        {
            for (int step = 0; element != null && step < maxParentSteps; step++)
            {
                if (element.Current.ProcessId == Environment.ProcessId)
                {
                    break;
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
                {
                    yield return (TextPattern)pattern;
                    break;
                }
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }

        if (AutomationElement.FocusedElement is { } focused && focused.Current.ProcessId != Environment.ProcessId
            && focused.TryGetCurrentPattern(TextPattern.Pattern, out object focusedPattern))
        {
            yield return (TextPattern)focusedPattern;
        }
    }

    private static async Task<SelectedText?> FromCopyAsync(IntPtr window)
    {
        if (NoCopyClasses.Contains(ClassName(window)) || AnyModifierDown())
        {
            return null;
        }

        IDataObject? saved = SaveClipboard();
        uint sequence = GetClipboardSequenceNumber();
        ClipboardHistory.Shared.IgnoreFor(TimeSpan.FromSeconds(1));
        SendCopy();

        string? copied = null;
        for (int waited = 0; waited < CopyTimeoutMs; waited += CopyPollMs)
        {
            await Task.Delay(CopyPollMs);
            if (GetClipboardSequenceNumber() == sequence)
            {
                continue;
            }

            // Uygulama biçimleri art arda yazıyor; yazının gelmesi için kısa bir an.
            await Task.Delay(CopyPollMs);
            try
            {
                copied = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
            }
            RestoreClipboard(saved);
            break;
        }

        return string.IsNullOrWhiteSpace(copied) ? null : new SelectedText(copied, null);
    }

    /// <summary>Panodaki biçimlerin kopyası; okunamayan biçimler atlanır.</summary>
    private static IDataObject? SaveClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } current)
            {
                return null;
            }

            var copy = new DataObject();
            foreach (string format in current.GetFormats(false))
            {
                try
                {
                    if (current.GetData(format, false) is { } data)
                    {
                        copy.SetData(format, data, false);
                    }
                }
                catch (Exception e) when (e is COMException or OutOfMemoryException or InvalidOperationException)
                {
                }
            }
            return copy.GetFormats(false).Length > 0 ? copy : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void RestoreClipboard(IDataObject? saved)
    {
        try
        {
            if (saved != null)
            {
                Clipboard.SetDataObject(saved, true);
            }
            else
            {
                Clipboard.Clear();
            }
        }
        catch (COMException)
        {
        }
    }

    private static bool AnyModifierDown()
    {
        const int shift = 0x10, control = 0x11, alt = 0x12, leftWin = 0x5B, rightWin = 0x5C;
        return new[] { shift, control, alt, leftWin, rightWin }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    }

    private static void SendCopy()
    {
        const ushort control = 0x11, c = 0x43;
        const uint keyUp = 0x0002;
        Input[] inputs =
        [
            Key(control, 0), Key(c, 0), Key(c, keyUp), Key(control, keyUp)
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort key, uint flags)
    {
        return new Input { Type = 1, Keyboard = new KeyboardInput { Key = key, Flags = flags } };
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    /// <summary>INPUT birliği: en büyük üye fare girdisi, boyut ona göre.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    [DllImport("user32")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
}
