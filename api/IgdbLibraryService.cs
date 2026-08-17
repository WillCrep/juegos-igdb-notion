using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;

public sealed class IgdbLibraryService
{
    private readonly IgdbClient igdb;
    private readonly string? connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
    public IgdbLibraryService(IgdbClient igdb) => this.igdb = igdb;

    public async Task<IReadOnlyList<IgdbSearchResult>> SearchAsync(string text, int? igdbId = null)
    {
        using var document = igdbId is > 0
            ? await igdb.GetGameAsync(igdbId.Value)
            : await igdb.SearchGamesAsync(text);
        return document.RootElement.EnumerateArray().Select(game => new IgdbSearchResult(
            game.GetProperty("id").GetInt32(), game.GetProperty("name").GetString() ?? "Sin nombre",
            game.TryGetProperty("cover", out var cover) && cover.TryGetProperty("image_id", out var image) ? CoverUrl(image.GetString()) : null,
            game.TryGetProperty("first_release_date", out var date) && date.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(date.GetInt64()).Year : null,
            game.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array ? platforms.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").Where(x => x.Length > 0).ToArray() : []
        )).ToList();
    }

    public async Task<int> ImportAsync(int igdbId)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("SQL_CONNECTION_STRING no está configurada.");
        using var document = await igdb.GetGameAsync(igdbId);
        var game = document.RootElement.EnumerateArray().FirstOrDefault();
        if (game.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("IGDB no devolvió el juego seleccionado.");
        using var collectionsDocument = await igdb.GetCollectionsAsync(GetReferenceIds(game, "collections"));
        using var collectionGamesDocument = await igdb.GetRelatedGamesAsync(collectionsDocument.RootElement.ValueKind == JsonValueKind.Array ? collectionsDocument.RootElement.EnumerateArray().SelectMany(x => GetReferenceIds(x, "games")) : []);
        using var relatedGamesDocument = await igdb.GetRelatedGamesAsync(GetReferenceIds(game, "dlcs").Concat(GetReferenceIds(game, "expansions")).Concat(GetReferenceIds(game, "standalone_expansions")).Concat(GetReferenceIds(game, "bundles")));
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var name = game.GetProperty("name").GetString() ?? "Sin nombre";
            var cover = game.TryGetProperty("cover", out var coverValue) && coverValue.TryGetProperty("image_id", out var image) ? CoverUrl(image.GetString()) : null;
            var summary = game.TryGetProperty("summary", out var summaryValue) ? summaryValue.GetString() : null;
            var url = game.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            DateTime? release = game.TryGetProperty("first_release_date", out var releaseValue) && releaseValue.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(releaseValue.GetInt64()).UtcDateTime.Date : null;
            decimal? rating = game.TryGetProperty("rating", out var ratingValue) && ratingValue.ValueKind == JsonValueKind.Number ? ratingValue.GetDecimal() : null;
            int gameId;
            await using (var find = new SqlCommand("SELECT Id FROM dbo.Games WHERE IgdbId=@id", connection, transaction)) { find.Parameters.AddWithValue("@id", igdbId); var found = await find.ExecuteScalarAsync(); if (found is null) { await using var insert = new SqlCommand("INSERT INTO dbo.Games(IgdbId,Name,Summary,CoverUrl,IgdbUrl,ReleaseDate,IgdbRating,Status,LastIgdbSyncAt) OUTPUT INSERTED.Id VALUES(@id,@name,@summary,@cover,@url,@release,@rating,N'Nuevo',SYSUTCDATETIME())", connection, transaction); Add(insert, igdbId, name, summary, cover, url, release, rating); gameId = Convert.ToInt32(await insert.ExecuteScalarAsync()); } else gameId = Convert.ToInt32(found); }
            await using (var update = new SqlCommand("UPDATE dbo.Games SET Name=@name,Summary=@summary,CoverUrl=COALESCE(@cover,CoverUrl),IgdbUrl=@url,ReleaseDate=@release,IgdbRating=@rating,LastIgdbSyncAt=SYSUTCDATETIME(),UpdatedAt=SYSUTCDATETIME() WHERE Id=@gameId", connection, transaction)) { update.Parameters.AddWithValue("@gameId", gameId); Add(update, igdbId, name, summary, cover, url, release, rating, false); await update.ExecuteNonQueryAsync(); }
            if (game.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array) foreach (var platform in platforms.EnumerateArray()) { var platformName = platform.GetProperty("name").GetString(); if (string.IsNullOrWhiteSpace(platformName)) continue; await using var upsert = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.Platforms WHERE Name=@name) INSERT dbo.Platforms(Name) VALUES(@name); SELECT Id FROM dbo.Platforms WHERE Name=@name", connection, transaction); upsert.Parameters.AddWithValue("@name", platformName); var platformId = Convert.ToInt32(await upsert.ExecuteScalarAsync()); await using var link = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.GamePlatforms WHERE GameId=@gameId AND PlatformId=@platformId) INSERT dbo.GamePlatforms(GameId,PlatformId) VALUES(@gameId,@platformId)", connection, transaction); link.Parameters.AddWithValue("@gameId", gameId); link.Parameters.AddWithValue("@platformId", platformId); await link.ExecuteNonQueryAsync(); }
            if (game.TryGetProperty("collection", out var collection) && collection.ValueKind == JsonValueKind.Object && collection.TryGetProperty("id", out var collectionId)) { var collectionName = collection.TryGetProperty("name", out var cn) ? cn.GetString() ?? "Serie" : "Serie"; await using var series = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.Series WHERE IgdbId=@id) INSERT dbo.Series(IgdbId,Name,IgdbUrl,LastIgdbSyncAt) VALUES(@id,@name,@url,SYSUTCDATETIME()); UPDATE dbo.Series SET Name=@name,IgdbUrl=@url,LastIgdbSyncAt=SYSUTCDATETIME() WHERE IgdbId=@id; SELECT Id FROM dbo.Series WHERE IgdbId=@id", connection, transaction); series.Parameters.AddWithValue("@id", collectionId.GetInt32()); series.Parameters.AddWithValue("@name", collectionName); series.Parameters.AddWithValue("@url", collection.TryGetProperty("url", out var cu) ? (object?)cu.GetString() ?? DBNull.Value : DBNull.Value); var seriesId = Convert.ToInt32(await series.ExecuteScalarAsync()); await using var link = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.GameSeries WHERE GameId=@gameId AND SeriesId=@seriesId) INSERT dbo.GameSeries(GameId,SeriesId) VALUES(@gameId,@seriesId)", connection, transaction); link.Parameters.AddWithValue("@gameId", gameId); link.Parameters.AddWithValue("@seriesId", seriesId); await link.ExecuteNonQueryAsync(); }
            await SyncResolvedCollectionsAsync(connection, transaction, gameId, collectionsDocument.RootElement);
            await SyncCollectionGamesAsync(connection, transaction, collectionGamesDocument.RootElement, collectionsDocument.RootElement);
            await ReconcileGameSeriesAsync(connection, transaction, gameId, GetReferenceIds(game, "collection").Concat(GetReferenceIds(game, "collections")));
            await UpdateExtendedMetadataAsync(connection, transaction, gameId, game);
            await SyncResolvedRelatedGamesAsync(connection, transaction, gameId, game, relatedGamesDocument.RootElement);
            await transaction.CommitAsync(); return gameId;
        }
        catch { await transaction.RollbackAsync(); throw; }
    }

    private static IEnumerable<int> GetReferenceIds(JsonElement game, string property)
    {
        if (!game.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var single)) return [single];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var number) ? number : x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var nestedObject) && nestedObject.TryGetInt32(out var objectId) ? objectId : (int?)null).Where(x => x.HasValue).Select(x => x!.Value);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var nested) && nested.TryGetInt32(out var nestedId)) return [nestedId];
        return [];
    }

    private static async Task SyncResolvedCollectionsAsync(SqlConnection c, SqlTransaction t, int gameId, JsonElement collections)
    {
        if (collections.ValueKind != JsonValueKind.Array) return;
        foreach (var item in collections.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var igdbId) || !item.TryGetProperty("name", out var name)) continue;
            await UpsertSeriesAsync(c, t, gameId, igdbId, name.GetString() ?? "Serie", item.TryGetProperty("url", out var url) ? url.GetString() : null);
        }
    }

    private static async Task SyncCollectionGamesAsync(SqlConnection c, SqlTransaction t, JsonElement games, JsonElement collections)
    {
        if (games.ValueKind != JsonValueKind.Array || collections.ValueKind != JsonValueKind.Array) return;
        foreach (var collection in collections.EnumerateArray())
        {
            if (!collection.TryGetProperty("id", out var collectionId) || !collectionId.TryGetInt32(out var igdbSeriesId)) continue;
            var seriesId = await GetLocalSeriesIdAsync(c, t, igdbSeriesId);
            if (seriesId is null) continue;
            foreach (var item in games.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var igdbId)) continue;
                await UpsertSeriesTitleAsync(c, t, seriesId.Value, item, igdbId);
            }
        }
    }

    private static async Task<int?> GetLocalSeriesIdAsync(SqlConnection c, SqlTransaction t, int igdbId)
    {
        await using var command = new SqlCommand("SELECT Id FROM dbo.Series WHERE IgdbId=@id", c, t); command.Parameters.AddWithValue("@id", igdbId); var value = await command.ExecuteScalarAsync(); return value is null ? null : Convert.ToInt32(value);
    }

    private static async Task ReconcileGameSeriesAsync(SqlConnection c, SqlTransaction t, int gameId, IEnumerable<int> allowedIgdbIds)
    {
        var allowed = allowedIgdbIds.Distinct().ToArray();
        var condition = allowed.Length == 0 ? "1 = 1" : $"s.IgdbId NOT IN ({string.Join(',', allowed)})";
        await using var command = new SqlCommand($"DECLARE @removed TABLE (SeriesId INT PRIMARY KEY); INSERT INTO @removed SELECT gs.SeriesId FROM dbo.GameSeries gs JOIN dbo.Series s ON s.Id=gs.SeriesId WHERE gs.GameId=@gameId AND {condition}; DELETE gs FROM dbo.GameSeries gs JOIN @removed r ON r.SeriesId=gs.SeriesId WHERE gs.GameId=@gameId; DELETE st FROM dbo.SeriesTitles st JOIN @removed r ON r.SeriesId=st.SeriesId WHERE NOT EXISTS (SELECT 1 FROM dbo.GameSeries gs WHERE gs.SeriesId=st.SeriesId); DELETE s FROM dbo.Series s JOIN @removed r ON r.SeriesId=s.Id WHERE NOT EXISTS (SELECT 1 FROM dbo.GameSeries gs WHERE gs.SeriesId=s.Id) AND NOT EXISTS (SELECT 1 FROM dbo.SeriesTitles st WHERE st.SeriesId=s.Id);", c, t);
        command.Parameters.AddWithValue("@gameId", gameId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertSeriesTitleAsync(SqlConnection c, SqlTransaction t, int seriesId, JsonElement item, int igdbId)
    {
        var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "Juego" : "Juego";
        var summary = item.TryGetProperty("summary", out var summaryValue) ? summaryValue.GetString() : null;
        var url = item.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
        var cover = item.TryGetProperty("cover", out var coverValue) && coverValue.TryGetProperty("image_id", out var image) ? $"https://images.igdb.com/igdb/image/upload/t_cover_big/{image.GetString()}.jpg" : null;
        DateTime? release = item.TryGetProperty("first_release_date", out var dateValue) && dateValue.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(dateValue.GetInt64()).UtcDateTime.Date : null;
        decimal? rating = item.TryGetProperty("rating", out var ratingValue) && ratingValue.ValueKind == JsonValueKind.Number ? ratingValue.GetDecimal() : null;
        await using var command = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.SeriesTitles WHERE SeriesId=@seriesId AND IgdbId=@igdbId) INSERT dbo.SeriesTitles(SeriesId,IgdbId,Name,Summary,CoverUrl,IgdbUrl,ReleaseDate) VALUES(@seriesId,@igdbId,@name,@summary,@cover,@url,@release) ELSE UPDATE dbo.SeriesTitles SET Name=@name,Summary=@summary,CoverUrl=COALESCE(@cover,CoverUrl),IgdbUrl=@url,ReleaseDate=@release WHERE SeriesId=@seriesId AND IgdbId=@igdbId", c, t);
        command.Parameters.AddWithValue("@seriesId", seriesId); command.Parameters.AddWithValue("@igdbId", igdbId); command.Parameters.AddWithValue("@name", name); command.Parameters.AddWithValue("@summary", (object?)summary ?? DBNull.Value); command.Parameters.AddWithValue("@cover", (object?)cover ?? DBNull.Value); command.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value); command.Parameters.AddWithValue("@release", (object?)release ?? DBNull.Value); await command.ExecuteNonQueryAsync();
    }

    private static async Task SyncResolvedFranchisesAsync(SqlConnection c, SqlTransaction t, int gameId, JsonElement franchises)
    {
        if (franchises.ValueKind != JsonValueKind.Array) return;
        foreach (var item in franchises.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var igdbId) || !item.TryGetProperty("name", out var name)) continue;
            await UpsertSeriesAsync(c, t, gameId, igdbId, name.GetString() ?? "Franquicia", item.TryGetProperty("url", out var url) ? url.GetString() : null);
        }
    }

    private static async Task<int> UpsertSeriesAsync(SqlConnection c, SqlTransaction t, int gameId, int igdbId, string name, string? url)
    {
        await using var series = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.Series WHERE IgdbId=@id) INSERT dbo.Series(IgdbId,Name,IgdbUrl,LastIgdbSyncAt) VALUES(@id,@name,@url,SYSUTCDATETIME()); UPDATE dbo.Series SET Name=@name,IgdbUrl=@url,LastIgdbSyncAt=SYSUTCDATETIME() WHERE IgdbId=@id; SELECT Id FROM dbo.Series WHERE IgdbId=@id", c, t);
        series.Parameters.AddWithValue("@id", igdbId); series.Parameters.AddWithValue("@name", name); series.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value);
        var seriesId = Convert.ToInt32(await series.ExecuteScalarAsync());
        await using var link = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.GameSeries WHERE GameId=@gameId AND SeriesId=@seriesId) INSERT dbo.GameSeries(GameId,SeriesId) VALUES(@gameId,@seriesId)", c, t);
        link.Parameters.AddWithValue("@gameId", gameId); link.Parameters.AddWithValue("@seriesId", seriesId); await link.ExecuteNonQueryAsync(); return seriesId;
    }

    private static async Task SyncResolvedRelatedGamesAsync(SqlConnection c, SqlTransaction t, int gameId, JsonElement sourceGame, JsonElement relatedGames)
    {
        if (relatedGames.ValueKind != JsonValueKind.Array) return;
        var dlcs = GetReferenceIds(sourceGame, "dlcs").ToHashSet(); var expansions = GetReferenceIds(sourceGame, "expansions").ToHashSet(); var standalone = GetReferenceIds(sourceGame, "standalone_expansions").ToHashSet(); var bundles = GetReferenceIds(sourceGame, "bundles").ToHashSet();
        foreach (var item in relatedGames.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var igdbId) || !item.TryGetProperty("name", out var name)) continue;
            var type = dlcs.Contains(igdbId) ? "DLC" : expansions.Contains(igdbId) ? "Expansión" : standalone.Contains(igdbId) ? "Expansión independiente" : bundles.Contains(igdbId) ? "Bundle" : "Relacionado";
            DateTime? date = item.TryGetProperty("first_release_date", out var dateValue) && dateValue.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(dateValue.GetInt64()).UtcDateTime.Date : null;
            await using var command = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM dbo.Dlc WHERE IgdbId=@igdbId) INSERT dbo.Dlc(IgdbId,BaseGameId,Name,Type,IgdbUrl,ReleaseDate) VALUES(@igdbId,@gameId,@name,@type,@url,@date) ELSE UPDATE dbo.Dlc SET BaseGameId=@gameId,Name=@name,Type=@type,IgdbUrl=@url,ReleaseDate=@date WHERE IgdbId=@igdbId", c, t);
            command.Parameters.AddWithValue("@igdbId", igdbId); command.Parameters.AddWithValue("@gameId", gameId); command.Parameters.AddWithValue("@name", name.GetString() ?? "Relacionado"); command.Parameters.AddWithValue("@type", type); command.Parameters.AddWithValue("@url", item.TryGetProperty("url", out var url) ? (object?)url.GetString() ?? DBNull.Value : DBNull.Value); command.Parameters.AddWithValue("@date", (object?)date ?? DBNull.Value); await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SyncFranchiseSeriesAsync(SqlConnection connection, SqlTransaction transaction, int gameId, JsonElement game)
    {
        var franchises = new List<JsonElement>();
        if (game.TryGetProperty("franchise", out var franchise) && franchise.ValueKind == JsonValueKind.Object) franchises.Add(franchise);
        if (game.TryGetProperty("franchises", out var franchiseList) && franchiseList.ValueKind == JsonValueKind.Array) franchises.AddRange(franchiseList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object));
        foreach (var franchiseItem in franchises)
        {
            if (!franchiseItem.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var igdbId)) continue;
            var name = franchiseItem.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            await using var series = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.Series WHERE IgdbId=@id) INSERT dbo.Series(IgdbId,Name,IgdbUrl,LastIgdbSyncAt) VALUES(@id,@name,@url,SYSUTCDATETIME()); UPDATE dbo.Series SET Name=@name,IgdbUrl=@url,LastIgdbSyncAt=SYSUTCDATETIME() WHERE IgdbId=@id; SELECT Id FROM dbo.Series WHERE IgdbId=@id", connection, transaction);
            series.Parameters.AddWithValue("@id", igdbId); series.Parameters.AddWithValue("@name", name); series.Parameters.AddWithValue("@url", franchiseItem.TryGetProperty("url", out var url) ? (object?)url.GetString() ?? DBNull.Value : DBNull.Value);
            var seriesId = Convert.ToInt32(await series.ExecuteScalarAsync());
            await using var link = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM dbo.GameSeries WHERE GameId=@gameId AND SeriesId=@seriesId) INSERT dbo.GameSeries(GameId,SeriesId) VALUES(@gameId,@seriesId)", connection, transaction);
            link.Parameters.AddWithValue("@gameId", gameId); link.Parameters.AddWithValue("@seriesId", seriesId); await link.ExecuteNonQueryAsync();
        }
    }

    private static async Task RemoveFranchiseRelationsAsync(SqlConnection connection, SqlTransaction transaction, int gameId, JsonElement game)
    {
        var franchiseIds = GetReferenceIds(game, "franchise").Concat(GetReferenceIds(game, "franchises")).Distinct().ToArray();
        if (franchiseIds.Length == 0) return;
        await using var command = new SqlCommand($"DELETE gs FROM dbo.GameSeries gs JOIN dbo.Series s ON s.Id = gs.SeriesId WHERE gs.GameId=@gameId AND s.IgdbId IN ({string.Join(',', franchiseIds)}); DELETE s FROM dbo.Series s WHERE s.IgdbId IN ({string.Join(',', franchiseIds)}) AND NOT EXISTS (SELECT 1 FROM dbo.GameSeries gs WHERE gs.SeriesId=s.Id) AND NOT EXISTS (SELECT 1 FROM dbo.SeriesTitles st WHERE st.SeriesId=s.Id);", connection, transaction);
        command.Parameters.AddWithValue("@gameId", gameId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> RefreshAsync(int gameId)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("SQL_CONNECTION_STRING no está configurada.");
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT IgdbId FROM dbo.Games WHERE Id=@id", connection);
        command.Parameters.AddWithValue("@id", gameId);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value == DBNull.Value) throw new InvalidOperationException("El juego no tiene un IGDB ID asociado.");
        return await ImportAsync(Convert.ToInt32(value));
    }

    private static async Task UpdateExtendedMetadataAsync(SqlConnection connection, SqlTransaction transaction, int gameId, JsonElement game)
    {
        await using var command = new SqlCommand("UPDATE dbo.Games SET Genres=@genres,Developers=@developers,Publishers=@publishers,Franchises=@franchises,Screenshots=@screenshots,Videos=@videos WHERE Id=@gameId", connection, transaction);
        command.Parameters.AddWithValue("@gameId", gameId); command.Parameters.AddWithValue("@genres", JsonNames(game, "genres")); command.Parameters.AddWithValue("@developers", JsonCompanies(game, true)); command.Parameters.AddWithValue("@publishers", JsonCompanies(game, false)); command.Parameters.AddWithValue("@franchises", JsonNames(game, "franchises", "franchise")); command.Parameters.AddWithValue("@screenshots", JsonImages(game, "screenshots")); command.Parameters.AddWithValue("@videos", JsonVideos(game)); await command.ExecuteNonQueryAsync();
    }

    private static async Task SyncDlcAsync(SqlConnection connection, SqlTransaction transaction, int gameId, JsonElement game)
    {
        foreach (var property in new[] { "dlcs", "expansions", "standalone_expansions", "bundles" })
        {
            if (!game.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in values.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idValue) || !item.TryGetProperty("name", out var nameValue)) continue;
                DateTime? date = item.TryGetProperty("first_release_date", out var dateValue) && dateValue.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(dateValue.GetInt64()).UtcDateTime.Date : null;
                await using var command = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM dbo.Dlc WHERE IgdbId=@igdbId) INSERT dbo.Dlc(IgdbId,BaseGameId,Name,Type,IgdbUrl,ReleaseDate) VALUES(@igdbId,@gameId,@name,@type,@url,@date) ELSE UPDATE dbo.Dlc SET BaseGameId=@gameId,Name=@name,Type=@type,IgdbUrl=@url,ReleaseDate=@date WHERE IgdbId=@igdbId", connection, transaction);
                command.Parameters.AddWithValue("@igdbId", idValue.GetInt32()); command.Parameters.AddWithValue("@gameId", gameId); command.Parameters.AddWithValue("@name", nameValue.GetString() ?? "DLC"); command.Parameters.AddWithValue("@type", property == "bundles" ? "Bundle" : property.Contains("expansion") ? "Expansión" : "DLC"); command.Parameters.AddWithValue("@url", item.TryGetProperty("url", out var url) ? (object?)url.GetString() ?? DBNull.Value : DBNull.Value); command.Parameters.AddWithValue("@date", (object?)date ?? DBNull.Value); await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static string JsonNames(JsonElement game, params string[] properties) => JsonSerializer.Serialize(properties.SelectMany(property => game.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.TryGetProperty("name", out var name) ? name.GetString() : null) : []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string JsonCompanies(JsonElement game, bool developers) => JsonSerializer.Serialize(game.TryGetProperty("involved_companies", out var companies) && companies.ValueKind == JsonValueKind.Array ? companies.EnumerateArray().Where(x => x.TryGetProperty(developers ? "developer" : "publisher", out var flag) && flag.GetBoolean()).Select(x => x.TryGetProperty("company", out var company) && company.TryGetProperty("name", out var name) ? name.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase) : []);
    private static string JsonImages(JsonElement game, string property) => JsonSerializer.Serialize(game.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(x => x.TryGetProperty("image_id", out var image) ? $"https://images.igdb.com/igdb/image/upload/t_1080p/{image.GetString()}.jpg" : null).Where(x => !string.IsNullOrWhiteSpace(x)) : []);
    private static string JsonVideos(JsonElement game) => JsonSerializer.Serialize(game.TryGetProperty("videos", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(x => x.TryGetProperty("video_id", out var video) ? $"https://www.youtube.com/watch?v={video.GetString()}" : null).Where(x => !string.IsNullOrWhiteSpace(x)) : []);
    private static void Add(SqlCommand c, int id, string name, string? summary, string? cover, string? url, DateTime? release, decimal? rating, bool includeId = true) { if (includeId) c.Parameters.AddWithValue("@id", id); c.Parameters.AddWithValue("@name", name); c.Parameters.AddWithValue("@summary", (object?)summary ?? DBNull.Value); c.Parameters.AddWithValue("@cover", (object?)cover ?? DBNull.Value); c.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value); c.Parameters.AddWithValue("@release", (object?)release ?? DBNull.Value); c.Parameters.AddWithValue("@rating", (object?)rating ?? DBNull.Value); }
    private static string? CoverUrl(string? image) => string.IsNullOrWhiteSpace(image) ? null : $"https://images.igdb.com/igdb/image/upload/t_cover_big/{image}.jpg";
}
public sealed record IgdbSearchResult(int Id, string Name, string? CoverUrl, int? ReleaseYear, string[] Platforms);
