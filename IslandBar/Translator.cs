using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManagedShell.Common.Logging;

namespace IslandBar;

/// <summary>Bir çeviri: metin ve dil yönü; çevrilemediyse <see cref="Problem"/> kullanıcıya söylenecek neden.</summary>
public sealed record Translation(string Text, string From, string To, string? Problem = null)
{
    /// <summary>Seçim <see cref="Translator.MaxLength"/> karakteri geçti, başı çevrildi.</summary>
    public bool Cut { get; init; }

    public string Direction => $"{Translator.LanguageName(From)} → {Translator.LanguageName(To)}" + (Cut ? $" · ilk {Translator.MaxLength} karakter" : "");

    public static Translation Failed(string problem) => new("", "", "", problem);
}

/// <summary>
/// MyMemory'nin anahtarsız, resmî ve ücretsiz ucu (kullanıcının seçimi; Google'ın anahtarsız ucu tarayıcı olmayan
/// istekleri geri çeviriyor). Günde yaklaşık 5 bin karakter, istek başına 500 bayt: uzun seçim cümle sınırlarından
/// parçalanır. Yön otomatik: Türkçe değilse Türkçeye, Türkçeyse İngilizceye (kullanıcının seçimi); kaynak Türkçeyse
/// servis "iki ayrı dil seçin" der, o zaman İngilizceye çevrilir.
/// </summary>
public static class Translator
{
    /// <summary>
    /// Balona sığan kadar (kullanıcının isteği: kaydırma yerine sınır). Sürüklerken sayaç gösterir, fazlası seçilirse
    /// başı çevrilir.
    /// </summary>
    public const int MaxLength = 300;

    private const string Endpoint = "https://api.mymemory.translated.net/get";
    private const int MaxChunkBytes = 450;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["tr"] = "Türkçe",
        ["en"] = "İngilizce",
        ["de"] = "Almanca",
        ["fr"] = "Fransızca",
        ["es"] = "İspanyolca",
        ["it"] = "İtalyanca",
        ["pt"] = "Portekizce",
        ["nl"] = "Felemenkçe",
        ["ru"] = "Rusça",
        ["uk"] = "Ukraynaca",
        ["pl"] = "Lehçe",
        ["ar"] = "Arapça",
        ["fa"] = "Farsça",
        ["el"] = "Yunanca",
        ["bg"] = "Bulgarca",
        ["az"] = "Azerbaycanca",
        ["ja"] = "Japonca",
        ["ko"] = "Korece",
        ["zh"] = "Çince"
    };

    public static string LanguageName(string code)
    {
        return Names.TryGetValue(code, out string? name) ? name : code.ToUpperInvariant();
    }

    /// <summary>Kip açılınca bağlantı önceden kurulur: ilk çeviri de hemen gelsin (ilk istek iki saniyeyi geçiyordu).</summary>
    public static async void Warm()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Endpoint);
            using HttpResponseMessage response = await Http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
    }

    public static async Task<Translation> TranslateAsync(string text, CancellationToken cancel)
    {
        bool cut = text.Length > MaxLength;
        if (cut)
        {
            text = text[..MaxLength];
        }

        try
        {
            var result = new StringBuilder();
            string from = "autodetect", to = "tr";
            foreach (string chunk in Chunks(text))
            {
                Reply reply = await RequestAsync(chunk, from, to, cancel);
                if (reply.SameLanguage && to == "tr")
                {
                    // Kaynak Türkçe: baştan İngilizceye.
                    return await TranslateAsync(text, "tr", "en", cancel) with { Cut = cut };
                }
                if (reply.Problem != null)
                {
                    return Translation.Failed(reply.Problem);
                }

                from = reply.Detected ?? from;
                result.Append(result.Length > 0 ? " " : "").Append(reply.Text);
            }
            return new Translation(result.ToString(), from, to) { Cut = cut };
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
            || e is TaskCanceledException && !cancel.IsCancellationRequested)
        {
            ShellLogger.Warning($"Translator: {e.Message}");
            return Translation.Failed("Çevrilemedi · bağlantıyı denetle");
        }
    }

    private static async Task<Translation> TranslateAsync(string text, string from, string to, CancellationToken cancel)
    {
        var result = new StringBuilder();
        foreach (string chunk in Chunks(text))
        {
            Reply reply = await RequestAsync(chunk, from, to, cancel);
            if (reply.Problem != null || reply.SameLanguage)
            {
                return Translation.Failed(reply.Problem ?? "Çevrilemedi");
            }
            result.Append(result.Length > 0 ? " " : "").Append(reply.Text);
        }
        return new Translation(result.ToString(), from, to);
    }

    private sealed record Reply(string Text, string? Detected, bool SameLanguage, string? Problem);

    private static async Task<Reply> RequestAsync(string text, string from, string to, CancellationToken cancel)
    {
        string url = $"{Endpoint}?langpair={from}%7C{to}&q={Uri.EscapeDataString(text)}";
        using HttpResponseMessage response = await Http.GetAsync(url, cancel);
        if ((int)response.StatusCode == 429)
        {
            return new Reply("", null, false, "Günlük çeviri sınırı doldu");
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        JsonElement root = json.RootElement;
        JsonElement data = root.GetProperty("responseData");
        string translated = data.GetProperty("translatedText").GetString() ?? "";

        // Durum bazen sayı, bazen yazı.
        JsonElement status = root.GetProperty("responseStatus");
        bool ok = status.ValueKind == JsonValueKind.Number ? status.GetInt32() == 200 : status.GetString() == "200";
        if (root.TryGetProperty("quotaFinished", out JsonElement quota) && quota.ValueKind == JsonValueKind.True)
        {
            return new Reply("", null, false, "Günlük çeviri sınırı doldu");
        }
        if (!ok)
        {
            bool same = translated.Contains("DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase);
            ShellLogger.Warning($"Translator: {status} {translated}");
            return new Reply("", null, same, same ? null : "Çevrilemedi");
        }

        string? detected = data.TryGetProperty("detectedLanguage", out JsonElement language) && language.ValueKind == JsonValueKind.String
            ? language.GetString()!.Split('-')[0].ToLowerInvariant()
            : null;
        return new Reply(translated.Trim(), detected, detected == to, null);
    }

    /// <summary>İstek sınırına sığan parçalar, kelime sınırından.</summary>
    private static IEnumerable<string> Chunks(string text)
    {
        var chunk = new StringBuilder();
        foreach (string piece in Regex.Split(text.Trim(), @"(?<=\s)"))
        {
            if (chunk.Length > 0 && Encoding.UTF8.GetByteCount(chunk + piece) > MaxChunkBytes)
            {
                yield return chunk.ToString().Trim();
                chunk.Clear();
            }
            chunk.Append(piece);
        }

        if (chunk.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }
}
