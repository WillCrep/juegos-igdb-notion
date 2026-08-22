using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

public sealed class IgdbClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;

    public IgdbClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public async Task<string> GetAccessTokenAsync()
    {
        var clientId = _configuration["IGDB_CLIENT_ID"]
            ?? throw new InvalidOperationException("IGDB_CLIENT_ID no está configurado");
        var clientSecret = _configuration["IGDB_CLIENT_SECRET"]
            ?? throw new InvalidOperationException("IGDB_CLIENT_SECRET no está configurado");

        var url =
            "https://id.twitch.tv/oauth2/token" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&client_secret={Uri.EscapeDataString(clientSecret)}" +
            "&grant_type=client_credentials";

        using var response = await _http.PostAsync(url, null);
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(json);

        return document.RootElement
        .GetProperty("access_token")
        .GetString()
        ?? throw new InvalidOperationException("IGDB no devolvió access_token");
    }

    public async Task<JsonDocument> QueryAsync(string endpoint, string query)
    {
        var clientId = _configuration["IGDB_CLIENT_ID"]
            ?? throw new InvalidOperationException("IGDB_CLIENT_ID no está configurado");

        var token = await GetAccessTokenAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.igdb.com/v4/{endpoint}"
        );

        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );

        request.Content = new StringContent(
            query,
            Encoding.UTF8,
            "text/plain"
        );

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"IGDB rechazÃ³ la consulta ({(int)response.StatusCode}): {json}");
        }

        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument> SearchGamesAsync(string name)
    {
        var safeName = name
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");

        var query = $"""
        search "{safeName}";
        fields id,name,cover.image_id,platforms.name,version_parent,first_release_date;
        limit 10;
        """;

        return await QueryAsync("games", query);
    }

    public async Task<JsonDocument> GetGameAsync(int id)
    {
        var query = $"""
            fields id,name,summary,storyline,first_release_date,cover.image_id,genres.name,platforms.name,
              collection.id,collection.name,collection.url,collection.games.id,collection.games.name,collections,
              franchise.id,franchise.name,franchise.url,franchises.id,franchises.name,franchises.url,
              involved_companies.company.name,involved_companies.developer,involved_companies.publisher,
              rating,aggregated_rating,
              dlcs.id,dlcs.name,dlcs.first_release_date,dlcs.platforms.name,dlcs.url,
              expansions.id,expansions.name,expansions.first_release_date,expansions.platforms.name,expansions.url,
              standalone_expansions.id,standalone_expansions.name,standalone_expansions.first_release_date,standalone_expansions.platforms.name,standalone_expansions.url,
              bundles.id,bundles.name,bundles.first_release_date,bundles.platforms.name,bundles.url,
              episodes.id,episodes.name,episodes.first_release_date,episodes.platforms.name,episodes.url,
              seasons.id,seasons.name,seasons.first_release_date,seasons.platforms.name,seasons.url,
              packs.id,packs.name,packs.first_release_date,packs.platforms.name,packs.url,
              version_parent,version_title,screenshots.image_id,artworks.image_id,videos.video_id,videos.name,url;
            where id = {id};
            """;

        return await QueryAsync("games", query);
    }

    public async Task<JsonDocument> GetCollectionsAsync(IEnumerable<int> ids)
    {
        var values = ids.Distinct().ToArray();
        if (values.Length == 0) return JsonDocument.Parse("[]");
        return await QueryAsync("collections", $"fields id,name,url,type,games; where id = ({string.Join(',', values)});");
    }

    public async Task<JsonDocument> GetFranchisesAsync(IEnumerable<int> ids)
    {
        var values = ids.Distinct().ToArray();
        if (values.Length == 0) return JsonDocument.Parse("[]");
        return await QueryAsync("franchises", $"fields id,name,url,games; where id = ({string.Join(',', values)});");
    }

    public async Task<JsonDocument> GetRelatedGamesAsync(IEnumerable<int> ids)
    {
        var values = ids.Distinct().ToArray();
        if (values.Length == 0) return JsonDocument.Parse("[]");
        return await QueryAsync("games", $"fields id,name,url,summary,first_release_date,cover.image_id,platforms.name,parent_game; where id = ({string.Join(',', values)});");
    }

    public async Task<JsonDocument> GetCollectionForGameAsync(int gameId)
    {
        var membershipQuery = $"""
            fields collection;
            where game = {gameId};
            """;

        using var memberships = await QueryAsync(
            "collection_memberships",
            membershipQuery);

        var collectionIds = memberships.RootElement
            .EnumerateArray()
            .Where(item =>
                item.TryGetProperty("collection", out var collection) &&
                collection.ValueKind == JsonValueKind.Number &&
                collection.TryGetInt32(out _))
            .Select(item => item.GetProperty("collection").GetInt32())
            .Distinct()
            .ToList();

        if (collectionIds.Count == 0)
        {
            return JsonDocument.Parse("[]");
        }

        var collectionsQuery = $"""
            fields
              id,
              name,
              url,
              games.id,
              games.name,
              games.first_release_date;
            where id = ({string.Join(",", collectionIds)});
            """;

        return await QueryAsync("collections", collectionsQuery);
    }
}
