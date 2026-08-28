using Microsoft.Data.SqlClient;

public sealed class AchievementImportService
{
    private const string Source = "TrophiesHunter";
    private readonly string? connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");
    private readonly TrophiesHunterClient trophies;
    private readonly AiConsultantService? aiService; // 👈 Inyectar AiConsultantService

    public AchievementImportService(TrophiesHunterClient trophies) => this.trophies = trophies;
    private SqlConnection OpenConnection() => string.IsNullOrWhiteSpace(connectionString) ? throw new InvalidOperationException("SQL_CONNECTION_STRING no está configurada.") : new SqlConnection(connectionString);
    public Task<IReadOnlyList<TrophiesHunterSearchResult>> SearchAsync(string game, string? platform, CancellationToken ct = default) => trophies.SearchAsync(game, platform, ct);

    public async Task<AchievementImportResult> ImportAsync(int gameId, int platformId, int? storeId, string sourceUrl, CancellationToken ct = default)
    {
        var set = await trophies.ScrapeSetAsync(sourceUrl, ct);
        return await SaveAsync(gameId, platformId, storeId, sourceUrl, set, ct);
    }

    public async Task<AchievementImportResult> ImportJsonAsync(int gameId, AchievementJsonImportRequest request, CancellationToken ct = default)
    {
        if (request.Achievements is null || request.Achievements.Count == 0)
            throw new InvalidOperationException("El JSON no contiene logros.");

        var achievements = request.Achievements
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalId) && !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new AchievementJsonItem(x.ExternalId.Trim(), x.Name.Trim(), x.Description, x.Tier, x.Points, x.RarityPercent, x.IsHidden, x.IsMissable, x.ImageUrl, x.ScoreType, x.IsRare))
            .ToList();

        if (achievements.Count == 0) throw new InvalidOperationException("El JSON no contiene logros válidos.");

        // 👉 TRADUCCIÓN AUTOMÁTICA CON GROQ IA SI VIENE HABILITADA
        if (request.TranslateToSpanish && aiService != null)
        {
            achievements = await aiService.TranslateAchievementsBatchAsync(achievements);
        }

        var domainAchievements = achievements
            .Select(x => new TrophiesHunterAchievement(x.ExternalId, x.Name, x.Description, x.Points, x.RarityPercent, x.Tier, x.IsHidden, x.IsMissable, x.ImageUrl, x.ScoreType, x.IsRare))
            .ToList();

        var sourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl) ? "local-json" : request.SourceUrl.Trim();
        var set = new TrophiesHunterAchievementSet(sourceUrl, request.SetName, request.Platform, domainAchievements);
        return await SaveAsync(gameId, request.PlatformId, request.StoreId, sourceUrl, set, ct);
    }

    private async Task<AchievementImportResult> SaveAsync(int gameId, int platformId, int? storeId, string sourceUrl, TrophiesHunterAchievementSet set, CancellationToken ct)
    {
        if (set.Achievements.Count == 0) throw new InvalidOperationException("No se encontraron logros para importar.");
        await using var connection = OpenConnection(); await connection.OpenAsync(ct); await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var setId = await UpsertSet(connection, transaction, gameId, platformId, storeId, sourceUrl, set);
            var order = 0;
            foreach (var achievement in set.Achievements)
                await UpsertAchievement(connection, transaction, setId, achievement, order++, ct);
            await using var sync = new SqlCommand("INSERT INTO dbo.SyncRuns (EntityType, EntityId, Source, Status, FinishedAt, ItemsProcessed) VALUES (N'AchievementSet', @entityId, @source, N'Succeeded', SYSUTCDATETIME(), @count);", connection, transaction);
            sync.Parameters.AddWithValue("@entityId", setId); sync.Parameters.AddWithValue("@source", Source); sync.Parameters.AddWithValue("@count", set.Achievements.Count); await sync.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return new AchievementImportResult(setId, set.Achievements.Count, set.Name, set.Platform);
        }
        catch
        { await transaction.RollbackAsync(ct); throw; }
    }

    private static async Task<int> UpsertSet(SqlConnection c, SqlTransaction t, int gameId, int platformId, int? storeId, string url, TrophiesHunterAchievementSet set)
    {
        await using var cmd = new SqlCommand("""
            MERGE dbo.AchievementSets AS target USING (SELECT @source AS ExternalSource, @externalId AS ExternalId) AS source
            ON target.ExternalSource = source.ExternalSource AND target.ExternalId = source.ExternalId
            WHEN MATCHED THEN UPDATE SET GameId=@gameId, PlatformId=@platformId, StoreId=@storeId, Name=@name, TotalAchievements=@total, LastSyncedAt=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (GameId, PlatformId, StoreId, ExternalSource, ExternalId, Name, TotalAchievements, LastSyncedAt) VALUES (@gameId,@platformId,@storeId,@source,@externalId,@name,@total,SYSUTCDATETIME())
            OUTPUT INSERTED.Id;
            """, c, t);
        Add(cmd, "@source", Source); Add(cmd, "@externalId", url); Add(cmd, "@gameId", gameId); Add(cmd, "@platformId", platformId); Add(cmd, "@storeId", (object?)storeId ?? DBNull.Value); Add(cmd, "@name", (object?)set.Name ?? DBNull.Value); Add(cmd, "@total", set.Achievements.Count);
        return (int)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("No se pudo crear el conjunto de logros."));
    }

    private static async Task UpsertAchievement(SqlConnection c, SqlTransaction t, int setId, TrophiesHunterAchievement item, int order, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            MERGE dbo.Achievements AS target USING (SELECT @setId AS AchievementSetId, @externalId AS ExternalId) AS source
            ON target.AchievementSetId=source.AchievementSetId AND target.ExternalId=source.ExternalId
            WHEN MATCHED THEN UPDATE SET Name=@name, Description=@description, ImageUrl=@imageUrl, Tier=@tier, ScoreType=@scoreType, Points=@points, RarityPercent=@rarity, IsHidden=@isHidden, IsMissable=@isMissable, IsRare=@isRare, SortOrder=@sortOrder
            WHEN NOT MATCHED THEN INSERT (AchievementSetId, ExternalId, Name, Description, ImageUrl, Tier, ScoreType, Points, RarityPercent, IsHidden, IsMissable, IsRare, SortOrder) VALUES (@setId,@externalId,@name,@description,@imageUrl,@tier,@scoreType,@points,@rarity,@isHidden,@isMissable,@isRare,@sortOrder)
            OUTPUT INSERTED.Id;
            """, c, t);
        Add(cmd, "@setId", setId); Add(cmd, "@externalId", item.ExternalId); Add(cmd, "@name", item.Name); Add(cmd, "@description", (object?)item.Description ?? DBNull.Value); Add(cmd, "@imageUrl", (object?)item.ImageUrl ?? DBNull.Value); Add(cmd, "@tier", (object?)item.Tier ?? DBNull.Value); Add(cmd, "@scoreType", (object?)item.ScoreType ?? DBNull.Value); Add(cmd, "@points", (object?)item.Points ?? DBNull.Value); Add(cmd, "@rarity", (object?)item.RarityPercent ?? DBNull.Value); Add(cmd, "@isHidden", item.IsHidden); Add(cmd, "@isMissable", item.IsMissable); Add(cmd, "@isRare", item.IsRare); Add(cmd, "@sortOrder", order);
        var achievementId = (int)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("No se pudo crear el logro."));
        await using var user = new SqlCommand("IF NOT EXISTS (SELECT 1 FROM dbo.UserAchievements WHERE AchievementId=@id) INSERT INTO dbo.UserAchievements (AchievementId, Unlocked) VALUES (@id, 0);", c, t); Add(user, "@id", achievementId); await user.ExecuteNonQueryAsync(ct);
    }
    private static void Add(SqlCommand c, string name, object value) => c.Parameters.AddWithValue(name, value);
}

public sealed record AchievementImportRequest(int PlatformId, int? StoreId, string SourceUrl);
public sealed record AchievementJsonImportRequest(
    int PlatformId, 
    int? StoreId, 
    string? SourceUrl, 
    string? SetName, 
    string? Platform, 
    IReadOnlyList<AchievementJsonItem> Achievements, 
    bool TranslateToSpanish = false // 👈 Nuevo parámetro opcional
);
public sealed record AchievementJsonItem(string ExternalId, string Name, string? Description, string? Tier, int? Points, decimal? RarityPercent, bool IsHidden, bool IsMissable, string? ImageUrl, string? ScoreType, bool IsRare);
public sealed record AchievementImportResult(int AchievementSetId, int Imported, string? Name, string? Platform);
