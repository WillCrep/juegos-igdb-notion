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

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(json);
    }

    public async Task<JsonDocument> SearchGamesAsync(string name)
    {
        var safeName = name
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");

        var query = $"""
        search "{safeName}";
        fields id,name,platforms.name,version_parent,first_release_date;
        limit 10;
        """;

        return await QueryAsync("games", query);
    }

    public async Task<JsonDocument> GetGameAsync(int id)
    {
        var query = $"""
            fields
              id,
              name,
              summary,
              storyline,
              first_release_date,
              cover.image_id,
              genres.name,
              platforms.name,
              franchise.name,
              franchises.name,
              involved_companies.company.name,
              involved_companies.developer,
              involved_companies.publisher,
              rating,
              aggregated_rating,
              dlcs.id,
              dlcs.name,
              expansions.id,
              expansions.name,
              bundles.id,
              bundles.name,
              bundles.first_release_date,
              bundles.platforms.name,
              bundles.url,
              dlcs.first_release_date,
              dlcs.platforms.name,
              dlcs.url,
              expansions.first_release_date,
              expansions.platforms.name,
              expansions.url,
              standalone_expansions.id,
              standalone_expansions.name,
              standalone_expansions.first_release_date,
              standalone_expansions.platforms.name,
              standalone_expansions.url,
              version_parent,
              version_title,
              screenshots.image_id,
              artworks.image_id,
              url;
            where id = {id};
            """;

        return await QueryAsync("games", query);
    }
}
