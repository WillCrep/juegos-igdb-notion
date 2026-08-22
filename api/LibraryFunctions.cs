using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.AspNetCore.WebUtilities;

public sealed class LibraryFunctions
{
    private readonly LibraryService library;
    private readonly IgdbLibraryService igdbLibrary;
    private readonly AchievementImportService achievementImport;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public LibraryFunctions(LibraryService library, IgdbLibraryService igdbLibrary, AchievementImportService achievementImport) { this.library = library; this.igdbLibrary = igdbLibrary; this.achievementImport = achievementImport; }

    [Function("IgdbSearch")]
    public Task<HttpResponseData> IgdbSearch([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "igdb/search")] HttpRequestData request)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.Url.Query);
        if (query.TryGetValue("id", out var idValue) && int.TryParse(idValue.ToString(), out var igdbId) && igdbId > 0)
            return Execute(request, () => igdbLibrary.SearchAsync(string.Empty, igdbId));
        var text = query.TryGetValue("q", out var value) ? value.ToString().Trim() : string.Empty;
        if (text.Length < 2) return Json(request, HttpStatusCode.BadRequest, new { error = "Escribe al menos dos caracteres." });
        return Execute(request, () => igdbLibrary.SearchAsync(text));
    }

    [Function("ImportIgdbGame")]
    public async Task<HttpResponseData> ImportIgdbGame([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games/import")] HttpRequestData request)
    {
        var body = await JsonSerializer.DeserializeAsync<IgdbImportRequest>(request.Body, JsonOptions);
        if (body?.IgdbId is null or <= 0) return await Json(request, HttpStatusCode.BadRequest, new { error = "IGDB ID inválido." });
        return await Execute(request, async () => new { id = await igdbLibrary.ImportAsync(body!.IgdbId!.Value), igdbId = body.IgdbId }, HttpStatusCode.Created);
    }

    [Function("RefreshIgdbGame")]
    public Task<HttpResponseData> RefreshIgdbGame([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games/{id:int}/igdb/refresh")] HttpRequestData request, int id)
        => Execute(request, async () => new { id = await igdbLibrary.RefreshAsync(id), refreshed = true });

    [Function("LibraryStats")]
    public Task<HttpResponseData> Stats([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/stats")] HttpRequestData request) => Execute(request, () => library.GetStatsAsync());

    [Function("LibraryGames")]
    public Task<HttpResponseData> Games([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/games")] HttpRequestData request)
    {
        var query = QueryHelpers.ParseQuery(request.Url.Query);
        return Execute(request, () => library.GetGamesAsync(query.TryGetValue("search", out var search) ? search.ToString() : null, query.TryGetValue("status", out var status) ? status.ToString() : null, int.TryParse(query.TryGetValue("page", out var pageValue) ? pageValue.ToString() : null, out var page) ? page : 1, int.TryParse(query.TryGetValue("pageSize", out var sizeValue) ? sizeValue.ToString() : null, out var size) ? size : 24));
    }

    [Function("LibraryGame")]
    public Task<HttpResponseData> Game([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/games/{id:int}")] HttpRequestData request, int id) => Execute(request, () => library.GetGameAsync(id));

    [Function("CreateLibraryGame")]
    public async Task<HttpResponseData> CreateGame([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games")] HttpRequestData request)
    {
        var body = await JsonSerializer.DeserializeAsync<CreateGameRequest>(request.Body, JsonOptions);
        if (body is null || string.IsNullOrWhiteSpace(body.Name)) return await Json(request, HttpStatusCode.BadRequest, new { error = "El nombre del juego es obligatorio." });
        return await Execute(request, async () => new { id = await library.CreateGameAsync(body) }, HttpStatusCode.Created);
    }

    [Function("UpdateLibraryGame")]
    public async Task<HttpResponseData> UpdateGame([HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "library/games/{id:int}")] HttpRequestData request, int id)
    {
        var body = await JsonSerializer.DeserializeAsync<UpdateGameRequest>(request.Body, JsonOptions);
        return await Execute(request, async () => new { updated = await library.UpdateGameAsync(id, body ?? new(null, null, null, null)) });
    }

    [Function("LibraryCatalogOptions")]
    public Task<HttpResponseData> CatalogOptions([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/catalog/{type}")] HttpRequestData request, string type) => Execute(request, async () => new { items = await library.GetCatalogOptionsAsync(type.Equals("platforms", StringComparison.OrdinalIgnoreCase) ? "Platforms" : type.Equals("stores", StringComparison.OrdinalIgnoreCase) ? "Stores" : "Invalid") });

    [Function("LibrarySeries")]
    public Task<HttpResponseData> Series([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/series")] HttpRequestData request) => Execute(request, () => library.GetSeriesAsync());

    [Function("LibrarySeriesDetails")]
    public Task<HttpResponseData> SeriesDetails([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/series/{id:int}")] HttpRequestData request, int id) => Execute(request, () => library.GetSeriesDetailsAsync(id));

    [Function("AddCollectionEntry")]
    public async Task<HttpResponseData> AddCollection([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games/{id:int}/collection")] HttpRequestData request, int id)
    {
        var body = await JsonSerializer.DeserializeAsync<CollectionEntryRequest>(request.Body, JsonOptions);
        if (body is null || body.PlatformId <= 0 || body.StoreId <= 0) return await Json(request, HttpStatusCode.BadRequest, new { error = "Plataforma y tienda son obligatorias." });
        return await Execute(request, async () => new { id = await library.AddCollectionEntryAsync(id, body) }, HttpStatusCode.Created);
    }

    [Function("DeleteCollectionEntry")]
    public Task<HttpResponseData> DeleteCollection([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "library/collection/{id:int}")] HttpRequestData request, int id) => Execute(request, async () => new { deleted = await library.DeleteCollectionEntryAsync(id) });

    [Function("SetCollectionDlc")]
    public async Task<HttpResponseData> SetCollectionDlc([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/collection/{entryId:int}/dlcs/{dlcId:int}")] HttpRequestData request, int entryId, int dlcId)
    {
        var body = await JsonSerializer.DeserializeAsync<DlcOwnershipRequest>(request.Body, JsonOptions);
        return await Execute(request, async () => new { updated = await library.SetDlcOnCollectionAsync(entryId, dlcId, body?.Owned ?? true) });
    }

    [Function("DeleteCollectionDlc")]
    public Task<HttpResponseData> DeleteCollectionDlc([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "library/collection/{entryId:int}/dlcs/{dlcId:int}")] HttpRequestData request, int entryId, int dlcId) => Execute(request, async () => new { deleted = await library.RemoveDlcFromCollectionAsync(entryId, dlcId) });

    [Function("LibraryCollection")]
    public Task<HttpResponseData> Collection([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/collection")] HttpRequestData request)
    {
        var query = QueryHelpers.ParseQuery(request.Url.Query);
        return Execute(request, () => library.GetCollectionAsync(query.TryGetValue("search", out var search) ? search.ToString().Trim() : null));
    }

    [Function("GameAchievements")]
    public Task<HttpResponseData> Achievements([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/games/{id:int}/achievements")] HttpRequestData request, int id)
    {
        var query = QueryHelpers.ParseQuery(request.Url.Query);
        return Execute(request, () => library.GetAchievementsAsync(id, int.TryParse(query.TryGetValue("platformId", out var platformValue) ? platformValue.ToString() : null, out var platformId) ? platformId : null, int.TryParse(query.TryGetValue("storeId", out var storeValue) ? storeValue.ToString() : null, out var storeId) ? storeId : null));
    }

    [Function("LibraryAchievements")]
    public Task<HttpResponseData> AllAchievements([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "library/achievements")] HttpRequestData request)
    {
        var query = QueryHelpers.ParseQuery(request.Url.Query);
        int? ParseInt(string key) => int.TryParse(query.TryGetValue(key, out var value) ? value.ToString() : null, out var parsed) ? parsed : null;
        bool? ParseBool(string key) => bool.TryParse(query.TryGetValue(key, out var value) ? value.ToString() : null, out var parsed) ? parsed : null;
        var search = query.TryGetValue("search", out var searchValue) ? searchValue.ToString().Trim() : null;
        return Execute(request, () => library.GetAllAchievementsAsync(string.IsNullOrWhiteSpace(search) ? null : search, ParseInt("gameId"), ParseInt("platformId"), ParseInt("storeId"), ParseBool("unlocked")));
    }

    [Function("UpdateAchievement")]
    public async Task<HttpResponseData> UpdateAchievement([HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "library/achievements/{id:int}")] HttpRequestData request, int id)
    {
        var body = await JsonSerializer.DeserializeAsync<AchievementUpdate>(request.Body, JsonOptions);
        if (body is null) return await Json(request, HttpStatusCode.BadRequest, new { error = "Cuerpo inválido." });
        return await Execute(request, async () => new { updated = await library.SetAchievementAsync(id, body.Unlocked) });
    }

    [Function("SearchTrophiesHunter")]
    public Task<HttpResponseData> SearchTrophiesHunter([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "achievement-sources/trophieshunter/search")] HttpRequestData request)
    {
        var query = QueryHelpers.ParseQuery(request.Url.Query);
        var game = query.TryGetValue("game", out var gameValue) ? gameValue.ToString().Trim() : "";
        var platform = query.TryGetValue("platform", out var platformValue) ? platformValue.ToString().Trim() : "";
        if (game.Length < 2) return Json(request, HttpStatusCode.BadRequest, new { error = "Indica el nombre del juego." });
        return Execute(request, () => achievementImport.SearchAsync(game, platform));
    }

    [Function("ImportTrophiesHunterAchievements")]
    public async Task<HttpResponseData> ImportTrophiesHunterAchievements([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games/{id:int}/achievements/import")] HttpRequestData request, int id)
    {
        var body = await JsonSerializer.DeserializeAsync<AchievementImportRequest>(request.Body, JsonOptions);
        if (body is null || body.PlatformId <= 0 || string.IsNullOrWhiteSpace(body.SourceUrl)) return await Json(request, HttpStatusCode.BadRequest, new { error = "Plataforma y URL de TrophiesHunter son obligatorias." });
        return await Execute(request, () => achievementImport.ImportAsync(id, body.PlatformId, body.StoreId, body.SourceUrl), HttpStatusCode.Created);
    }

    [Function("ImportAchievementJson")]
    public async Task<HttpResponseData> ImportAchievementJson([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "library/games/{id:int}/achievements/import-json")] HttpRequestData request, int id)
    {
        var body = await JsonSerializer.DeserializeAsync<AchievementJsonImportRequest>(request.Body, JsonOptions);
        if (body is null || body.PlatformId <= 0 || body.Achievements is null || body.Achievements.Count == 0)
            return await Json(request, HttpStatusCode.BadRequest, new { error = "El JSON debe incluir plataforma y al menos un logro." });
        return await Execute(request, () => achievementImport.ImportJsonAsync(id, body), HttpStatusCode.Created);
    }

    [Function("DeleteLibraryGame")]
    public Task<HttpResponseData> DeleteGame([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "library/games/{id:int}")] HttpRequestData request, int id)
        => Execute(request, async () => new { deleted = await library.DeleteGameAsync(id) });

    private static async Task<HttpResponseData> Execute<T>(HttpRequestData request, Func<Task<T>> action, HttpStatusCode success = HttpStatusCode.OK)
    {
        try { return await Json(request, success, await action()); }
        catch (InvalidOperationException ex) { return await Json(request, HttpStatusCode.ServiceUnavailable, new { error = ex.Message }); }
        catch (Exception ex) { return await Json(request, HttpStatusCode.InternalServerError, new { error = ex.Message }); }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData request, HttpStatusCode status, object? value)
    {
        var response = request.CreateResponse(status); await response.WriteAsJsonAsync(value); return response;
    }
}

public sealed record AchievementUpdate(bool Unlocked);
public sealed record DlcOwnershipRequest(bool Owned);
public sealed record IgdbImportRequest(int? IgdbId);
