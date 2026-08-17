using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

public sealed class TrophiesHunterClient
{
    private const string BaseUrl = "https://trophieshunter.com";
    private readonly HttpClient http;

    public TrophiesHunterClient(HttpClient http)
    {
        this.http = http;
        this.http.BaseAddress = new Uri(BaseUrl);
        this.http.Timeout = TimeSpan.FromSeconds(30);
        this.http.DefaultRequestHeaders.UserAgent.Clear();
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        this.http.DefaultRequestHeaders.Accept.Clear();
        this.http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        this.http.DefaultRequestHeaders.AcceptLanguage.Clear();
        this.http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-ES,es;q=0.9,en-US;q=0.8,en;q=0.7");
        this.http.DefaultRequestHeaders.Referrer = new Uri($"{BaseUrl}/es/");
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        this.http.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
    }

    public async Task<IReadOnlyList<TrophiesHunterSearchResult>> SearchAsync(string gameName, string? platform, CancellationToken cancellationToken = default)
    {
        var slug = Slugify(gameName);
        if (string.IsNullOrWhiteSpace(slug)) return [];

        var results = new List<TrophiesHunterSearchResult>();
        foreach (var suffix in PlatformSuffixes(platform))
        {
            var url = $"{BaseUrl}/games/{slug}-{suffix}";
            var page = await TryGetDocumentAsync(url, cancellationToken);
            if (page is null) continue;
            var parsed = ParseSet(page, url, gameName, platform);
            if (parsed is not null && results.All(x => !string.Equals(x.Url, parsed.Url, StringComparison.OrdinalIgnoreCase)))
                results.Add(parsed);
        }
        return results;
    }

    public async Task<TrophiesHunterAchievementSet> ScrapeSetAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        var normalizedUrl = NormalizeSourceUrl(sourceUrl);
        if (!IsAllowedUrl(normalizedUrl)) throw new ArgumentException("La URL debe pertenecer a trophieshunter.com.");
        var page = await GetDocumentAsync(normalizedUrl, cancellationToken);
        var links = page.DocumentNode.SelectNodes("//a[@href]")?.Select(a => a.GetAttributeValue("href", ""))
            .Select(ToAbsoluteUrl).Where(x => x is not null && IsAchievementUrl(x!)).Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        var set = ParseSet(page, normalizedUrl, null, null) ?? new TrophiesHunterSearchResult(normalizedUrl, "Logros importados", null, 0, []);
        var achievements = new List<TrophiesHunterAchievement>();
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var achievementPage = await TryGetDocumentAsync(link, cancellationToken);
            if (achievementPage is not null)
            {
                var parsed = ParseAchievement(achievementPage, link);
                if (parsed is not null) achievements.Add(parsed);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return new TrophiesHunterAchievementSet(normalizedUrl, set.Name, set.Platform, achievements);
    }

    private async Task<HtmlDocument?> TryGetDocumentAsync(string url, CancellationToken cancellationToken)
    {
        try { return await GetDocumentAsync(url, cancellationToken); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private async Task<HtmlDocument> GetDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("TrophiesHunter rechazó la solicitud (403). Usa la URL pública localizada del juego, por ejemplo https://trophieshunter.com/es/games/jump-force-steam, y no una URL de cuenta.");
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("TrophiesHunter no devolvió una página HTML.");
        var document = new HtmlDocument();
        document.LoadHtml(await response.Content.ReadAsStringAsync(cancellationToken));
        return document;
    }

    private static TrophiesHunterSearchResult? ParseSet(HtmlDocument document, string url, string? fallbackName, string? fallbackPlatform)
    {
        var text = Clean(document.DocumentNode.InnerText);
        var title = Clean(document.DocumentNode.SelectSingleNode("//h1")?.InnerText ?? fallbackName ?? "Logros");
        var platform = DetectPlatform(text) ?? fallbackPlatform;
        var count = Regex.Match(text, @"(?<!\d)(\d{1,4})\s+Achievements?", RegexOptions.IgnoreCase).Groups[1].Value;
        var links = document.DocumentNode.SelectNodes("//a[@href]")?.Select(a => a.GetAttributeValue("href", ""))
            .Select(ToAbsoluteUrl).Where(x => x is not null && IsAchievementUrl(x!)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (links.Count == 0 && string.IsNullOrWhiteSpace(fallbackName)) return null;
        return new TrophiesHunterSearchResult(url, title, platform, int.TryParse(count, out var n) ? n : links.Count, links);
    }

    private static TrophiesHunterAchievement? ParseAchievement(HtmlDocument document, string url)
    {
        var heading = document.DocumentNode.SelectSingleNode("//h1") ?? document.DocumentNode.SelectSingleNode("//h2");
        var name = Clean(heading?.InnerText ?? "");
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = Clean(document.DocumentNode.InnerText);
        var pointsMatch = Regex.Match(text, @"(?<rarity>\d+(?:[\.,]\d+)?)%\s+(?<points>\d+)\s+Points?", RegexOptions.IgnoreCase);
        decimal? rarity = decimal.TryParse(pointsMatch.Groups["rarity"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null;
        int? points = int.TryParse(pointsMatch.Groups["points"].Value, out var p) ? p : null;
        var description = Clean(heading?.ParentNode?.SelectSingleNode(".//h3")?.InnerText ?? "");
        if (description == name) description = null;
        return new TrophiesHunterAchievement(url, name, description, points, rarity);
    }

    private static IEnumerable<string> PlatformSuffixes(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform)) return ["steam", "pc", "ps5", "ps4", "psvita", "ps3", "playstation-2", "playstation-portable", "playstation", "xbox", "pc-xbox", "switch", "game-boy-advance", "game-boy-color", "game-boy", "nintendo-ds", "nintendo-3ds", "nintendo-64", "gamecube", "snes", "genesis-mega-drive", "dreamcast", "saturn", "master-system", "game-gear", "arcade"];
        var p = platform.ToLowerInvariant();
        if (p.Contains("playstation 5") || p == "ps5") return ["ps5"];
        if (p.Contains("playstation 4") || p == "ps4") return ["ps4"];
        if (p.Contains("playstation 3") || p == "ps3") return ["ps3"];
        if (p.Contains("playstation 2") || p == "ps2") return ["playstation-2"];
        if (p.Contains("playstation vita") || p.Contains("ps vita") || p.Contains("psvita")) return ["psvita"];
        if (p.Contains("playstation portable") || p == "psp") return ["playstation-portable", "psp"];
        if (p is "playstation" or "playstation 1" or "ps1") return ["playstation"];
        if (p.Contains("xbox")) return ["xbox", "pc-xbox"];
        if (p.Contains("switch")) return ["switch"];
        if (p.Contains("game boy advance") || p == "gba") return ["game-boy-advance"];
        if (p.Contains("game boy color") || p == "gbc") return ["game-boy-color"];
        if (p is "game boy" or "gb") return ["game-boy"];
        if (p.Contains("nintendo ds") || p == "nds") return ["nintendo-ds"];
        if (p.Contains("nintendo 3ds") || p == "3ds") return ["nintendo-3ds"];
        if (p.Contains("nintendo 64") || p == "n64") return ["nintendo-64"];
        if (p.Contains("gamecube")) return ["gamecube"];
        if (p.Contains("super nintendo") || p.Contains("snes")) return ["snes"];
        if (p.Contains("sega genesis") || p.Contains("mega drive")) return ["genesis-mega-drive"];
        if (p.Contains("dreamcast")) return ["dreamcast"];
        if (p.Contains("saturn")) return ["saturn"];
        if (p.Contains("master system")) return ["master-system"];
        if (p.Contains("game gear")) return ["game-gear"];
        if (p.Contains("arcade")) return ["arcade"];
        if (p.Contains("steam")) return ["steam"];
        return ["pc", "steam"];
    }

    private static bool IsAllowedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals("trophieshunter.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".trophieshunter.com", StringComparison.OrdinalIgnoreCase));
    private static string NormalizeSourceUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri)) return sourceUrl;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var gamesIndex = Array.FindIndex(segments, segment => segment.Equals("games", StringComparison.OrdinalIgnoreCase));
        if (gamesIndex < 0 || gamesIndex + 1 >= segments.Length) return uri.ToString();

        // Account pages such as /es/users/.../accounts/.../games/foo are not
        // reliable scraping targets. Resolve them to the public game page.
        var gameSlug = Uri.UnescapeDataString(segments[gamesIndex + 1]);
        var language = segments.Length > 0 && Regex.IsMatch(segments[0], "^[a-z]{2}$", RegexOptions.IgnoreCase)
            ? $"/{segments[0].ToLowerInvariant()}"
            : string.Empty;
        return $"{BaseUrl}{language}/games/{Uri.EscapeDataString(gameSlug)}";
    }
    private static bool IsAchievementUrl(string url) => IsAllowedUrl(url) && new Uri(url).AbsolutePath.Contains("/achievements/", StringComparison.OrdinalIgnoreCase);
    private static string? ToAbsoluteUrl(string href) => Uri.TryCreate(new Uri(BaseUrl), href, out var uri) && IsAllowedUrl(uri.ToString()) ? uri.ToString() : null;
    private static string? DetectPlatform(string text) => new[] { "PlayStation 5", "PlayStation 4", "Xbox", "Nintendo Switch", "Steam", "PC" }.FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static string Clean(string value) => Regex.Replace(HtmlEntity.DeEntitize(value ?? ""), @"\s+", " ").Trim();
    private static string Slugify(string value) => Regex.Replace(value.Normalize(System.Text.NormalizationForm.FormD).ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", "-").Trim('-');
}

public sealed record TrophiesHunterSearchResult(string Url, string Name, string? Platform, int AchievementCount, IReadOnlyList<string> AchievementUrls);
public sealed record TrophiesHunterAchievementSet(string Url, string? Name, string? Platform, IReadOnlyList<TrophiesHunterAchievement> Achievements);
public sealed record TrophiesHunterAchievement(string ExternalId, string Name, string? Description, int? Points, decimal? RarityPercent, string? Tier = null, bool IsHidden = false, bool IsMissable = false, string? ImageUrl = null, string? ScoreType = null, bool IsRare = false);
