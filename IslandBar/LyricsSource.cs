using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IslandBar;

internal sealed record LyricLine(TimeSpan At, string Text);

/// <summary>Bir şarkının sözleri; zamanlı satırlar yoksa <see cref="Problem"/> kullanıcıya söylenecek neden.</summary>
internal sealed record Lyrics(IReadOnlyList<LyricLine> Lines, string? Problem)
{
    public static Lyrics Failed(string problem) => new([], problem);

    /// <summary>Bağlantı sorunu ya da servis meşgul: birazdan yeniden denenir (kalıcı değil).</summary>
    public bool Transient { get; init; }
}

/// <summary>
/// Şarkı sözleri LRCLIB'den (https://lrclib.net): ücretsiz, açık, anahtarsız; satırları zaman damgalı (LRC). Spotify'ın
/// kendi sözleri lisanslı ve iç ucunda, oturum anahtarı ister: kullanılmaz (kurallarına aykırı). Servis istemcinin
/// kendini tanıtmasını istiyor; çok sık istekte 429 ile beklemesini söyler, o şarkı için bir daha sorulmaz.
/// Gönderilen yalnızca şarkının adı, sanatçısı, albümü ve süresi.
/// </summary>
internal static partial class LyricsSource
{
    private const string Endpoint = "https://lrclib.net/api/";
    private const int CacheSize = 64;
    private const double DurationSlackSeconds = 3;

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, Lyrics> Cache = [];
    private static readonly Queue<string> CacheOrder = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string version = typeof(LyricsSource).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"IslandBar/{version} (+https://github.com/GurkanGurdal/TaskBar)");
        return client;
    }

    public static async Task<Lyrics> FindAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        string key = $"{title}\n{artist}";
        if (Cache.TryGetValue(key, out Lyrics? cached))
        {
            return cached;
        }

        Lyrics found;
        try
        {
            found = await ExactAsync(title, artist, album, duration, token) ?? await SearchAsync(title, artist, duration, token)
                ?? Lyrics.Failed("Bu şarkının sözleri bulunamadı");
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Lyrics.Failed("Söz servisi şimdi meşgul") with { Transient = true };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !token.IsCancellationRequested)
        {
            ManagedShell.Common.Logging.ShellLogger.Warning($"LyricsSource: {e.Message}");
            return Lyrics.Failed("Sözlere ulaşılamadı") with { Transient = true };
        }

        Remember(key, found);
        return found;
    }

    /// <summary>Ad, sanatçı, albüm ve süreyle (±2 sn) tam eşleşme.</summary>
    private static async Task<Lyrics?> ExactAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        var query = new List<string> { $"track_name={Uri.EscapeDataString(title)}", $"artist_name={Uri.EscapeDataString(artist)}" };
        if (!string.IsNullOrWhiteSpace(album))
        {
            query.Add($"album_name={Uri.EscapeDataString(album)}");
        }
        if (duration > TimeSpan.Zero)
        {
            query.Add($"duration={(int)Math.Round(duration.TotalSeconds)}");
        }

        using HttpResponseMessage response = await Http.GetAsync(Endpoint + "get?" + string.Join('&', query), token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return FromRecord(document.RootElement);
    }

    /// <summary>Spotify birden çok sanatçıyı virgülle yazıyor, tam eşleşme kaçıyor: ilk sanatçıyla arayıp süresi tutanı al.</summary>
    private static async Task<Lyrics?> SearchAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        string first = artist.Split([',', '&'], 2)[0].Trim();
        string url = $"{Endpoint}search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(first)}";
        using HttpResponseMessage response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));

        JsonElement? best = null;
        foreach (JsonElement record in document.RootElement.EnumerateArray())
        {
            bool fits = duration <= TimeSpan.Zero || record.TryGetProperty("duration", out JsonElement length)
                && length.ValueKind == JsonValueKind.Number && Math.Abs(length.GetDouble() - duration.TotalSeconds) <= DurationSlackSeconds;
            if (!fits)
            {
                continue;
            }

            best ??= record;
            if (record.TryGetProperty("syncedLyrics", out JsonElement synced) && synced.ValueKind == JsonValueKind.String)
            {
                best = record;
                break;
            }
        }
        return best is { } chosen ? FromRecord(chosen) : null;
    }

    private static Lyrics FromRecord(JsonElement record)
    {
        if (record.TryGetProperty("instrumental", out JsonElement instrumental) && instrumental.ValueKind == JsonValueKind.True)
        {
            return Lyrics.Failed("Enstrümantal");
        }

        if (record.TryGetProperty("syncedLyrics", out JsonElement synced) && synced.ValueKind == JsonValueKind.String
            && Parse(synced.GetString()!) is { Count: > 0 } lines)
        {
            return new Lyrics(lines, null);
        }

        return Lyrics.Failed("Bu şarkının zamanlı sözleri yok");
    }

    /// <summary>LRC: "[mm:ss.xx] metin"; bir satırda birden çok zaman olabilir. Boş satırlar ara sayılır (söz yok).</summary>
    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            MatchCollection stamps = Stamp().Matches(raw);
            if (stamps.Count == 0)
            {
                continue;
            }

            string text = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match stamp in stamps)
            {
                int minutes = int.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + seconds), text));
            }
        }
        lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lines;
    }

    private static void Remember(string key, Lyrics lyrics)
    {
        // Bağlantı sorunu kalıcı değil: yalnızca cevaplar saklanır.
        if (Cache.TryAdd(key, lyrics))
        {
            CacheOrder.Enqueue(key);
            if (CacheOrder.Count > CacheSize)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]")]
    private static partial Regex Stamp();
}
