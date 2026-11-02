using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace IslandBar;

/// <summary>Claude Code'dan gelen olay: hangi projede, bitti mi onay mı bekliyor.</summary>
internal sealed record ClaudeNote(string Project, bool IsWaiting)
{
    public string Status => IsWaiting ? "Onay bekliyor" : "Claude bitirdi";
}

/// <summary>
/// Claude Code kancası (kullanıcının seçimi: bildirim paneli yerine yalnızca Claude için şarkı kartı gibi kart).
/// Kanca <c>IslandBar.exe --claude-hook</c> olarak çalışır: Claude'un stdin'e yazdığı olayı çalışan çubuğa adlı boruyla
/// iletir ve çıkar. Çubuk açık değilse hiçbir şey yapmaz. Boru yalnızca bu kullanıcıya ve bu bilgisayara açık
/// (.NET varsayılanı: uzaktan bağlantı reddedilir, yazma yalnızca sahibine).
/// </summary>
internal static class ClaudeHook
{
    public const string Argument = "--claude-hook";
    private const string PipeName = "IslandBar.Claude";
    private const int ConnectTimeoutMs = 500;

    /// <summary>Kanca olarak mı çalıştırıldı? Öyleyse olayı iletir; çağıran hemen çıkmalı.</summary>
    public static bool RunIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Skip(1).Contains(Argument))
        {
            return false;
        }

        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            byte[] message = Encoding.UTF8.GetBytes(input.ReadToEnd());
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(ConnectTimeoutMs);
            pipe.Write(message);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // Çubuk kapalı ya da meşgul: kart çıkmaz, Claude'u bekletmeyiz.
        }
        return true;
    }

    /// <summary>Gelen olayları arka planda dinler; <paramref name="received"/> o iş parçacığında çağrılır.</summary>
    public static void Listen(Action<ClaudeNote> received)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    if (Parse(reader.ReadToEnd()) is { } note)
                    {
                        received(note);
                    }
                }
                catch (IOException)
                {
                    // Kanca yarıda koptu; sıradakini bekle.
                }
            }
        })
        {
            IsBackground = true,
            Name = "ClaudeHook"
        };
        thread.Start();
    }

    /// <summary>
    /// Stop: bitti. Notification: onay bekliyor. Mesajın metni kullanılmaz (kullanıcının isteği: kartta içerik olmasın).
    /// Boşta bekleme bildirimi atlanır: aynı cevap için Stop'tan bir dakika sonra yeniden kart çıkarırdı.
    /// </summary>
    internal static ClaudeNote? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string project = Path.GetFileName(Text(root, "cwd").TrimEnd('\\', '/'));
            if (project.Length == 0)
            {
                project = "Claude";
            }

            switch (Text(root, "hook_event_name"))
            {
                case "Stop":
                    return new ClaudeNote(project, IsWaiting: false);
                case "Notification":
                    return Text(root, "message").Contains("waiting for your input", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : new ClaudeNote(project, IsWaiting: true);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
