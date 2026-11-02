using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IslandBar;

/// <summary>
/// IslandBar'ın Claude Code kancasını kullanıcının <c>~/.claude/settings.json</c> dosyasına ekler ya da çıkarır
/// (ayar açılınca / kapanınca; kullanıcının isteği: yayınlanınca her bilgisayarda kendiliğinden kurulsun). Dosyadaki öteki
/// ayarlara ve kancalara dokunulmaz, yalnızca komutunda <see cref="ClaudeHook.Argument"/> geçen IslandBar girdileri
/// değişir. Claude Code kurulu değilse (klasör yoksa) ya da dosya bozuksa hiçbir şey yazılmaz.
/// </summary>
internal static class ClaudeHookSetup
{
    private static readonly string[] Events = ["Stop", "Notification"];

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Ayara göre kancayı kurar ya da kaldırır. Kurulu kanca başka bir exe'yi gösteriyorsa bu exe'ye çevrilir.</summary>
    public static void Sync(bool enabled)
    {
        try
        {
            Apply(SettingsPath, enabled ? Environment.ProcessPath : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dosya o an kilitli olabilir; bir sonraki açılışta yeniden denenir.
        }
    }

    /// <param name="exePath">Kancanın çalıştıracağı exe; null ise IslandBar girdileri silinir.</param>
    /// <returns>Dosya değişti mi.</returns>
    internal static bool Apply(string settingsPath, string? exePath)
    {
        string? folder = Path.GetDirectoryName(settingsPath);
        if (folder == null || !Directory.Exists(folder))
        {
            return false;
        }

        string original = File.Exists(settingsPath) ? File.ReadAllText(settingsPath, Encoding.UTF8) : "";
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(original) ? new JsonObject()
                : JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                  ?? throw new JsonException();
        }
        catch (JsonException)
        {
            // Kullanıcının bozuk dosyasını ezmeyiz.
            return false;
        }

        JsonObject? hooks = root["hooks"] as JsonObject;
        if (hooks == null && exePath == null)
        {
            return false;
        }
        hooks ??= new JsonObject();

        foreach (string name in Events)
        {
            JsonArray entries = hooks[name] as JsonArray ?? new JsonArray();
            foreach (JsonNode? entry in entries.Where(IsOurs).ToList())
            {
                entries.Remove(entry);
            }

            if (exePath != null)
            {
                entries.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        // Ters eğik çizgi kabukta kaçış sayılır; Windows düz eğik çizgili yolu da açar.
                        ["command"] = $"\"{exePath.Replace('\\', '/')}\" {ClaudeHook.Argument}",
                        ["async"] = true
                    })
                });
            }

            if (entries.Count > 0)
            {
                hooks[name] = entries;
            }
            else
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count > 0)
        {
            root["hooks"] = hooks;
        }
        else
        {
            root.Remove("hooks");
        }

        string updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (Normalize(updated) == Normalize(original))
        {
            return false;
        }

        // Yarım yazılmış dosya Claude Code'un tüm ayarlarını devre dışı bırakır: önce yanına yazılır, sonra yer değiştirir.
        string temporary = settingsPath + ".islandbar.tmp";
        File.WriteAllText(temporary, updated + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, settingsPath, overwrite: true);
        return true;
    }

    /// <summary>Girdinin kancalarından biri IslandBar'ın komutu mu.</summary>
    private static bool IsOurs(JsonNode? entry)
    {
        return entry?["hooks"] is JsonArray list
            && list.Any(hook => hook?["command"]?.GetValue<string>() is { } command
                && command.Contains(ClaudeHook.Argument, StringComparison.Ordinal)
                && command.Contains("IslandBar", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? "" : JsonNode.Parse(json)?.ToJsonString() ?? "";
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
