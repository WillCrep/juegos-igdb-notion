using Microsoft.Data.SqlClient;
using System.Data;

public sealed class LibraryService
{
    private readonly string? connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING");

    private SqlConnection OpenConnection()
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("SQL_CONNECTION_STRING no está configurada.");

        return new SqlConnection(connectionString);
    }

    public async Task<LibraryStats> GetStatsAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM dbo.Games) AS Games,
                (SELECT COUNT(*) FROM dbo.CollectionEntries WHERE Owned = 1) AS Owned,
                (SELECT COUNT(*) FROM dbo.Series) AS Series,
                (SELECT COUNT(*) FROM dbo.Achievements) AS Achievements,
                (SELECT COUNT(*) FROM dbo.UserAchievements WHERE Unlocked = 1) AS Unlocked;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new LibraryStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    public async Task<IReadOnlyList<GameSummary>> GetGamesAsync(string? search, string? status, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        
        await using var command = new SqlCommand("""
            WITH PagedGames AS (
                SELECT g.Id, g.Name, g.CoverUrl, g.Status, g.MyRating, g.ReleaseDate,
                       g.PlayTimeHours, g.Genres, g.UpdatedAt
                FROM dbo.Games g
                WHERE (@search IS NULL OR g.Name LIKE N'%' + @search + N'%')
                  AND (@status IS NULL OR g.Status = @status)
                ORDER BY g.UpdatedAt DESC, g.Name
                OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            )
            SELECT pg.Id, pg.Name, pg.CoverUrl, pg.Status, pg.MyRating, pg.ReleaseDate,
                   pg.PlayTimeHours, s.Name AS SeriesName,
                   COALESCE(cs.OwnedCopies, 0) AS OwnedCopies,
                   COALESCE(gs.AchievementTotal, 0) AS AchievementTotal,
                   COALESCE(gs.AchievementUnlocked, 0) AS AchievementUnlocked,
                   pg.Genres
            FROM PagedGames pg
            OUTER APPLY (
                SELECT TOP 1 s.Name
                FROM dbo.GameSeries gser JOIN dbo.Series s ON s.Id = gser.SeriesId
                WHERE gser.GameId = pg.Id
                ORDER BY gser.SequenceNumber, s.Name
            ) s
            OUTER APPLY (
                SELECT COUNT(*) AS OwnedCopies
                FROM dbo.CollectionEntries ce
                WHERE ce.GameId = pg.Id AND ce.Owned = 1
            ) cs
            OUTER APPLY (
                SELECT 
                    COUNT(a.Id) AS AchievementTotal,
                    COUNT(CASE WHEN ua.Unlocked = 1 THEN 1 END) AS AchievementUnlocked
                FROM dbo.AchievementSets aset
                JOIN dbo.Achievements a ON a.AchievementSetId = aset.Id
                LEFT JOIN dbo.UserAchievements ua ON ua.AchievementId = a.Id
                WHERE aset.GameId = pg.Id
            ) gs
            ORDER BY pg.UpdatedAt DESC, pg.Name;
        """, connection);

        command.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 300) { Value = (object?)search ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 40) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("@pageSize", pageSize);

        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<GameSummary>();
        while (await reader.ReadAsync())
        {
            result.Add(new GameSummary(
                reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetByte(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return result;
    }

    public async Task<GameDetails?> GetGameAsync(int id)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT g.Id, g.Name, g.IgdbId, g.Summary, g.CoverUrl, g.IgdbUrl, g.ReleaseDate, g.IgdbRating, g.MyRating, g.Status, g.PlayTimeHours, g.Notes,
                   s.Id AS SeriesId, s.Name AS SeriesName, g.Genres, g.Developers, g.Publishers, g.Franchises, g.Screenshots, g.Videos
            FROM dbo.Games g
            OUTER APPLY (SELECT TOP 1 s.Id, s.Name FROM dbo.GameSeries gs JOIN dbo.Series s ON s.Id = gs.SeriesId WHERE gs.GameId = g.Id ORDER BY gs.SequenceNumber, s.Name) s
            WHERE g.Id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var game = new GameDetails(
            reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetByte(8),
            reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19));
        await reader.CloseAsync();

        await using var collection = new SqlCommand("""
            SELECT ce.Id, p.Name, st.Name, ce.Edition, ce.Owned, ce.Format
            FROM dbo.CollectionEntries ce JOIN dbo.Platforms p ON p.Id = ce.PlatformId JOIN dbo.Stores st ON st.Id = ce.StoreId
            WHERE ce.GameId = @id ORDER BY p.Name, st.Name;
            """, connection);
        collection.Parameters.AddWithValue("@id", id);
        await using var collectionReader = await collection.ExecuteReaderAsync();
        var entries = new List<CollectionItem>();
        while (await collectionReader.ReadAsync()) entries.Add(new CollectionItem(collectionReader.GetInt32(0), collectionReader.GetString(1), collectionReader.GetString(2), collectionReader.GetString(3), collectionReader.GetBoolean(4), collectionReader.IsDBNull(5) ? null : collectionReader.GetString(5)));
        await collectionReader.CloseAsync();

        await using var dlcCommand = new SqlCommand("""
            SELECT d.Id, d.IgdbId, d.Name, d.Type, d.IgdbUrl, d.ReleaseDate,
                   ce.Id, p.Name, st.Name, ce.Edition, cdlc.Owned
            FROM dbo.Dlc d
            LEFT JOIN dbo.CollectionEntryDlcs cdlc ON cdlc.DlcId=d.Id
            LEFT JOIN dbo.CollectionEntries ce ON ce.Id=cdlc.CollectionEntryId
            LEFT JOIN dbo.Platforms p ON p.Id=ce.PlatformId
            LEFT JOIN dbo.Stores st ON st.Id=ce.StoreId
            WHERE d.BaseGameId=@id
            ORDER BY d.Name, p.Name, st.Name;
            """, connection);
        dlcCommand.Parameters.AddWithValue("@id", id);
        await using var dlcReader = await dlcCommand.ExecuteReaderAsync();
        var dlcs = new Dictionary<int, DlcItem>();
        while (await dlcReader.ReadAsync())
        {
            var dlcId = dlcReader.GetInt32(0);
            if (!dlcs.TryGetValue(dlcId, out var dlc))
            {
                dlc = new DlcItem(dlcId, dlcReader.IsDBNull(1) ? null : dlcReader.GetInt32(1), dlcReader.GetString(2), dlcReader.IsDBNull(3) ? null : dlcReader.GetString(3), dlcReader.IsDBNull(4) ? null : dlcReader.GetString(4), dlcReader.IsDBNull(5) ? null : dlcReader.GetDateTime(5), []);
                dlcs[dlcId] = dlc;
            }
            if (!dlcReader.IsDBNull(6)) dlcs[dlcId] = dlc with { Copies = dlc.Copies.Append(new DlcCopyItem(dlcReader.GetInt32(6), dlcReader.GetString(7), dlcReader.GetString(8), dlcReader.GetString(9), ReadBoolean(dlcReader, 10))).ToList() };
        }
        return game with { Collection = entries, Dlcs = dlcs.Values.ToList() };
    }

    public async Task<int> CreateGameAsync(CreateGameRequest request)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO dbo.Games (Name, IgdbId, CoverUrl, IgdbUrl, Status, Notes)
            OUTPUT INSERTED.Id VALUES (@name, @igdbId, @coverUrl, @igdbUrl, @status, @notes);
            """, connection);
        command.Parameters.AddWithValue("@name", request.Name.Trim());
        command.Parameters.AddWithValue("@igdbId", (object?)request.IgdbId ?? DBNull.Value);
        command.Parameters.AddWithValue("@coverUrl", (object?)request.CoverUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@igdbUrl", (object?)request.IgdbUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", string.IsNullOrWhiteSpace(request.Status) ? "Nuevo" : request.Status);
        command.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    public async Task<bool> UpdateGameAsync(int id, UpdateGameRequest request)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.Games SET
                Status = COALESCE(@status, Status), MyRating = COALESCE(@rating, MyRating),
                PlayTimeHours = COALESCE(@hours, PlayTimeHours), Notes = COALESCE(@notes, Notes), UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @id;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@status", (object?)request.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("@rating", (object?)request.MyRating ?? DBNull.Value);
        command.Parameters.AddWithValue("@hours", (object?)request.PlayTimeHours ?? DBNull.Value);
        command.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<AchievementItem>> GetAchievementsAsync(int gameId, int? platformId, int? storeId)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT a.Id, aset.Name, p.Name, st.Name, a.Name, a.Description, a.ImageUrl, a.Tier, a.ScoreType, a.RarityPercent,
                   a.IsHidden, a.IsMissable, a.IsRare, COALESCE(ua.Unlocked, 0), ua.UnlockedAt
            FROM dbo.Achievements a JOIN dbo.AchievementSets aset ON aset.Id = a.AchievementSetId
            JOIN dbo.Platforms p ON p.Id = aset.PlatformId LEFT JOIN dbo.Stores st ON st.Id = aset.StoreId LEFT JOIN dbo.UserAchievements ua ON ua.AchievementId = a.Id
            WHERE aset.GameId = @gameId AND (@platformId IS NULL OR aset.PlatformId = @platformId) AND (@storeId IS NULL OR aset.StoreId = @storeId)
            ORDER BY aset.Name, a.SortOrder, a.Name;
            """, connection);
        command.Parameters.AddWithValue("@gameId", gameId); command.Parameters.AddWithValue("@platformId", (object?)platformId ?? DBNull.Value); command.Parameters.AddWithValue("@storeId", (object?)storeId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<AchievementItem>();
        while (await reader.ReadAsync()) result.Add(new AchievementItem(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetDecimal(9), ReadBoolean(reader, 10), ReadBoolean(reader, 11), ReadBoolean(reader, 12), ReadBoolean(reader, 13), reader.IsDBNull(14) ? null : reader.GetDateTime(14)));
        return result;
    }

    private static bool ReadBoolean(SqlDataReader reader, int ordinal) => !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));

    public async Task<IReadOnlyList<GlobalAchievementItem>> GetAllAchievementsAsync(string? search, int? gameId, int? platformId, int? storeId, bool? unlocked)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP (10000) a.Id, g.Id, g.Name, a.Name, a.Description, a.ImageUrl, aset.Name,
                   p.Name, st.Name, a.Tier, a.ScoreType, a.Points, a.RarityPercent, a.IsHidden, a.IsMissable, a.IsRare,
                   COALESCE(ua.Unlocked, 0), ua.UnlockedAt
            FROM dbo.Achievements a
            JOIN dbo.AchievementSets aset ON aset.Id = a.AchievementSetId
            JOIN dbo.Games g ON g.Id = aset.GameId
            JOIN dbo.Platforms p ON p.Id = aset.PlatformId
            LEFT JOIN dbo.Stores st ON st.Id = aset.StoreId
            LEFT JOIN dbo.UserAchievements ua ON ua.AchievementId = a.Id
            WHERE (@search IS NULL OR g.Name LIKE N'%' + @search + N'%' OR a.Name LIKE N'%' + @search + N'%')
              AND (@gameId IS NULL OR g.Id = @gameId)
              AND (@platformId IS NULL OR aset.PlatformId = @platformId)
              AND (@storeId IS NULL OR aset.StoreId = @storeId)
              AND (@unlocked IS NULL OR COALESCE(ua.Unlocked, 0) = @unlocked)
            ORDER BY g.Name, aset.Name, a.SortOrder, a.Name;
            """, connection);
        command.Parameters.Add(new SqlParameter("@search", SqlDbType.NVarChar, 300) { Value = (object?)search ?? DBNull.Value });
        command.Parameters.AddWithValue("@gameId", (object?)gameId ?? DBNull.Value); command.Parameters.AddWithValue("@platformId", (object?)platformId ?? DBNull.Value); command.Parameters.AddWithValue("@storeId", (object?)storeId ?? DBNull.Value); command.Parameters.AddWithValue("@unlocked", (object?)unlocked ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<GlobalAchievementItem>();
        while (await reader.ReadAsync()) result.Add(new GlobalAchievementItem(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetDecimal(12), ReadBoolean(reader, 13), ReadBoolean(reader, 14), ReadBoolean(reader, 15), ReadBoolean(reader, 16), reader.IsDBNull(17) ? null : reader.GetDateTime(17)));
        return result;
    }

    public async Task<bool> SetAchievementAsync(int achievementId, bool unlocked)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            MERGE dbo.UserAchievements AS target USING (SELECT @id AS AchievementId) AS source
            ON target.AchievementId = source.AchievementId
            WHEN MATCHED THEN UPDATE SET Unlocked = @unlocked, UnlockedAt = CASE WHEN @unlocked = 1 THEN COALESCE(target.UnlockedAt, SYSUTCDATETIME()) ELSE NULL END, UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (AchievementId, Unlocked, UnlockedAt) VALUES (@id, @unlocked, CASE WHEN @unlocked = 1 THEN SYSUTCDATETIME() ELSE NULL END);
            """, connection);
        command.Parameters.AddWithValue("@id", achievementId); command.Parameters.AddWithValue("@unlocked", unlocked);
        await command.ExecuteNonQueryAsync(); return true;
    }

    public async Task<bool> SetDlcOnCollectionAsync(int collectionEntryId, int dlcId, bool owned)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            IF NOT EXISTS (SELECT 1 FROM dbo.CollectionEntries ce JOIN dbo.Dlc d ON d.BaseGameId=ce.GameId WHERE ce.Id=@entryId AND d.Id=@dlcId)
                THROW 50001, 'El DLC no pertenece al juego base de la copia.', 1;
            MERGE dbo.CollectionEntryDlcs AS target USING (SELECT @entryId AS CollectionEntryId, @dlcId AS DlcId) AS source
            ON target.CollectionEntryId=source.CollectionEntryId AND target.DlcId=source.DlcId
            WHEN MATCHED THEN UPDATE SET Owned=@owned
            WHEN NOT MATCHED THEN INSERT (CollectionEntryId,DlcId,Owned) VALUES (@entryId,@dlcId,@owned);
            """, connection);
        command.Parameters.AddWithValue("@entryId", collectionEntryId); command.Parameters.AddWithValue("@dlcId", dlcId); command.Parameters.AddWithValue("@owned", owned); await command.ExecuteNonQueryAsync(); return true;
    }

    public async Task<bool> RemoveDlcFromCollectionAsync(int collectionEntryId, int dlcId)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM dbo.CollectionEntryDlcs WHERE CollectionEntryId=@entryId AND DlcId=@dlcId", connection); command.Parameters.AddWithValue("@entryId", collectionEntryId); command.Parameters.AddWithValue("@dlcId", dlcId); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<CatalogOption>> GetCatalogOptionsAsync(string table)
    {
        var allowed = table is "Platforms" or "Stores" ? table : throw new ArgumentException("Catálogo inválido.");
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT Id, Name FROM dbo.{allowed} ORDER BY Name", connection);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<CatalogOption>();
        while (await reader.ReadAsync()) result.Add(new CatalogOption(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    public async Task<int> AddCollectionEntryAsync(int gameId, CollectionEntryRequest request)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO dbo.CollectionEntries (GameId, PlatformId, StoreId, Edition, Owned, Format, Notes)
            OUTPUT INSERTED.Id VALUES (@gameId, @platformId, @storeId, @edition, @owned, @format, @notes);
            """, connection);
        command.Parameters.AddWithValue("@gameId", gameId); command.Parameters.AddWithValue("@platformId", request.PlatformId); command.Parameters.AddWithValue("@storeId", request.StoreId); command.Parameters.AddWithValue("@edition", string.IsNullOrWhiteSpace(request.Edition) ? "Base" : request.Edition); command.Parameters.AddWithValue("@owned", request.Owned); command.Parameters.AddWithValue("@format", (object?)request.Format ?? DBNull.Value); command.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
        try { return Convert.ToInt32(await command.ExecuteScalarAsync()); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { throw new InvalidOperationException("Ya existe esa copia para el juego, plataforma, tienda y edición.", ex); }
    }

    public async Task<bool> DeleteCollectionEntryAsync(int id)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync(); await using var command = new SqlCommand("DELETE FROM dbo.CollectionEntries WHERE Id=@id", connection); command.Parameters.AddWithValue("@id", id); return await command.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<OwnedCollectionItem>> GetCollectionAsync(string? search)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT ce.Id, g.Id, g.Name, g.CoverUrl, p.Name, st.Name, ce.Edition, ce.Format, ce.Owned, g.Status
            FROM dbo.CollectionEntries ce
            JOIN dbo.Games g ON g.Id=ce.GameId
            JOIN dbo.Platforms p ON p.Id=ce.PlatformId
            JOIN dbo.Stores st ON st.Id=ce.StoreId
            WHERE ce.Owned=1 AND (@search IS NULL OR g.Name LIKE N'%' + @search + N'%' OR p.Name LIKE N'%' + @search + N'%' OR st.Name LIKE N'%' + @search + N'%')
            ORDER BY g.Name, p.Name, st.Name, ce.Edition;
            """, connection);
        command.Parameters.AddWithValue("@search", (object?)search ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<OwnedCollectionItem>();
        while (await reader.ReadAsync()) result.Add(new OwnedCollectionItem(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetBoolean(8), reader.GetString(9)));
        return result;
    }

    public async Task<IReadOnlyList<SeriesSummary>> GetSeriesAsync()
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            WITH OwnedGames AS
            (
                SELECT DISTINCT GameId FROM dbo.CollectionEntries WHERE Owned=1
            ), Catalog AS
            (
                SELECT st.SeriesId, st.IgdbId, g.Id AS GameId, g.Status
                FROM dbo.SeriesTitles st LEFT JOIN dbo.Games g ON g.IgdbId = st.IgdbId
                UNION
                SELECT gs.SeriesId, g.IgdbId, g.Id, g.Status
                FROM dbo.GameSeries gs JOIN dbo.Games g ON g.Id = gs.GameId
            ), CatalogFlags AS
            (
                SELECT c.*, CASE WHEN o.GameId IS NULL THEN 0 ELSE 1 END AS Owned
                FROM Catalog c LEFT JOIN OwnedGames o ON o.GameId=c.GameId
            )
            SELECT s.Id, s.Name, s.IgdbId, COUNT(DISTINCT c.IgdbId) AS TotalGames,
                   COUNT(DISTINCT CASE WHEN c.Owned=1 THEN c.IgdbId END) AS OwnedGames,
                   COUNT(DISTINCT CASE WHEN c.Status = N'Completado' THEN c.IgdbId END) AS CompletedGames
            FROM dbo.Series s LEFT JOIN CatalogFlags c ON c.SeriesId = s.Id
            GROUP BY s.Id, s.Name, s.IgdbId
            ORDER BY s.Name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<SeriesSummary>();
        while (await reader.ReadAsync()) { var total = reader.GetInt32(3); var owned = reader.GetInt32(4); result.Add(new SeriesSummary(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt32(2), total, owned, reader.GetInt32(5), total == 0 ? 0 : Math.Round(owned * 100m / total, 1))); }
        return result;
    }

    public async Task<SeriesDetails?> GetSeriesDetailsAsync(int id)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync();
        await using var seriesCommand = new SqlCommand("""
            WITH OwnedGames AS
            (
                SELECT DISTINCT GameId FROM dbo.CollectionEntries WHERE Owned=1
            ), Catalog AS
            (
                SELECT st.SeriesId, st.IgdbId, g.Id AS GameId, g.Status
                FROM dbo.SeriesTitles st LEFT JOIN dbo.Games g ON g.IgdbId=st.IgdbId
                UNION
                SELECT gs.SeriesId, g.IgdbId, g.Id, g.Status
                FROM dbo.GameSeries gs JOIN dbo.Games g ON g.Id=gs.GameId
            ), CatalogFlags AS
            (
                SELECT c.*, CASE WHEN o.GameId IS NULL THEN 0 ELSE 1 END AS Owned
                FROM Catalog c LEFT JOIN OwnedGames o ON o.GameId=c.GameId
            )
            SELECT s.Id, s.Name, s.IgdbId, COUNT(DISTINCT c.IgdbId) AS TotalGames,
                   COUNT(DISTINCT CASE WHEN c.Owned=1 THEN c.IgdbId END) AS OwnedGames,
                   COUNT(DISTINCT CASE WHEN c.Status=N'Completado' THEN c.IgdbId END) AS CompletedGames
            FROM dbo.Series s LEFT JOIN CatalogFlags c ON c.SeriesId=s.Id
            WHERE s.Id=@id
            GROUP BY s.Id, s.Name, s.IgdbId;
            """, connection); seriesCommand.Parameters.AddWithValue("@id", id);
        await using var seriesReader = await seriesCommand.ExecuteReaderAsync(); if (!await seriesReader.ReadAsync()) return null;
        var totalGames = seriesReader.GetInt32(3); var ownedGames = seriesReader.GetInt32(4); var completedGames = seriesReader.GetInt32(5);
        var series = new SeriesDetails(seriesReader.GetInt32(0), seriesReader.GetString(1), seriesReader.IsDBNull(2) ? null : seriesReader.GetInt32(2), totalGames, ownedGames, completedGames, totalGames == 0 ? 0 : Math.Round(ownedGames * 100m / totalGames, 1), []); await seriesReader.CloseAsync();
        await using var gamesCommand = new SqlCommand("""
            WITH Catalog AS
            (
                SELECT st.IgdbId, st.Name, st.CoverUrl, st.SequenceNumber, g.Id AS GameId, g.Status
                FROM dbo.SeriesTitles st LEFT JOIN dbo.Games g ON g.IgdbId=st.IgdbId WHERE st.SeriesId=@id
                UNION
                SELECT g.IgdbId, g.Name, g.CoverUrl, gs.SequenceNumber, g.Id, g.Status
                FROM dbo.GameSeries gs JOIN dbo.Games g ON g.Id=gs.GameId
                WHERE gs.SeriesId=@id AND NOT EXISTS (SELECT 1 FROM dbo.SeriesTitles st WHERE st.SeriesId=@id AND st.IgdbId=g.IgdbId)
            )
            SELECT c.IgdbId, c.Name, c.CoverUrl, COALESCE(c.Status,N'No agregado'),
                   CASE WHEN EXISTS (SELECT 1 FROM dbo.CollectionEntries ce WHERE ce.GameId=c.GameId AND ce.Owned=1) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END
            FROM Catalog c ORDER BY c.SequenceNumber, c.Name;
            """, connection); gamesCommand.Parameters.AddWithValue("@id", id); await using var reader = await gamesCommand.ExecuteReaderAsync(); var games = new List<SeriesGame>(); while (await reader.ReadAsync()) games.Add(new SeriesGame(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetBoolean(4))); return series with { Games = games };
    }

    public async Task<bool> DeleteGameAsync(int id)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using var command = new SqlCommand("""
                -- 1. Eliminar asociaciones de DLCs con copias de este juego o DLCs de este juego
                DELETE cdlc FROM dbo.CollectionEntryDlcs cdlc 
                JOIN dbo.CollectionEntries ce ON ce.Id = cdlc.CollectionEntryId 
                WHERE ce.GameId = @id;

                DELETE cdlc FROM dbo.CollectionEntryDlcs cdlc 
                JOIN dbo.Dlc d ON d.Id = cdlc.DlcId 
                WHERE d.BaseGameId = @id;

                -- 2. Eliminar DLCs asociados al juego base
                DELETE FROM dbo.Dlc WHERE BaseGameId = @id;

                -- 3. Eliminar copias de la colección
                DELETE FROM dbo.CollectionEntries WHERE GameId = @id;

                -- 4. Eliminar progreso de logros del usuario vinculados al juego
                DELETE ua FROM dbo.UserAchievements ua 
                JOIN dbo.Achievements a ON a.Id = ua.AchievementId 
                JOIN dbo.AchievementSets aset ON aset.Id = a.AchievementSetId 
                WHERE aset.GameId = @id;

                -- 5. Eliminar logros y conjuntos de logros
                DELETE a FROM dbo.Achievements a 
                JOIN dbo.AchievementSets aset ON aset.Id = a.AchievementSetId 
                WHERE aset.GameId = @id;

                DELETE FROM dbo.AchievementSets WHERE GameId = @id;

                -- 6. Eliminar vínculos de plataformas y series
                DELETE FROM dbo.GamePlatforms WHERE GameId = @id;
                DELETE FROM dbo.GameSeries WHERE GameId = @id;

                -- 7. Eliminar el registro del juego
                DELETE FROM dbo.Games WHERE Id = @id;

                -- 8. Limpiar series huérfanas que se hayan quedado sin juegos
                DELETE s FROM dbo.Series s 
                WHERE NOT EXISTS (SELECT 1 FROM dbo.GameSeries gs WHERE gs.SeriesId = s.Id) 
                  AND NOT EXISTS (SELECT 1 FROM dbo.SeriesTitles st WHERE st.SeriesId = s.Id);
            """, connection, transaction);

            command.Parameters.AddWithValue("@id", id);
            var rows = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return rows > 0;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

 public async Task<int> TranslateExistingAchievementsAsync(int gameId)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync();

        // 1. Obtener los logros actuales del juego
        await using var queryCmd = new SqlCommand("""
            SELECT a.Id, a.Name, a.Description, a.ExternalId, a.Tier, a.ScoreType, a.Points, a.RarityPercent, a.IsHidden, a.IsMissable, a.IsRare, a.ImageUrl
            FROM dbo.Achievements a 
            JOIN dbo.AchievementSets aset ON aset.Id = a.AchievementSetId 
            WHERE aset.GameId = @gameId
            ORDER BY a.SortOrder, a.Id;
        """, connection);
        queryCmd.Parameters.AddWithValue("@gameId", gameId);

        var list = new List<AchievementJsonItem>();
        var idMap = new List<int>();

        await using (var reader = await queryCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                idMap.Add(reader.GetInt32(0));
                list.Add(new AchievementJsonItem(
                    reader.GetString(3),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetBoolean(10)
                ));
            }
        }

        if (list.Count == 0) throw new InvalidOperationException("No se encontraron logros guardados para este juego.");

        // 2. Traducir con Groq IA
        var http = new HttpClient();
        var ai = new AiConsultantService(http);
        var translated = await ai.TranslateAchievementsBatchAsync(list);

        // 3. Actualizar nombres y descripciones en Azure SQL
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            for (int i = 0; i < translated.Count; i++)
            {
                await using var updateCmd = new SqlCommand("UPDATE dbo.Achievements SET Name = @name, Description = @desc WHERE Id = @id", connection, tx);
                updateCmd.Parameters.AddWithValue("@name", translated[i].Name);
                updateCmd.Parameters.AddWithValue("@desc", (object?)translated[i].Description ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@id", idMap[i]);
                await updateCmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return translated.Count;
        }
        catch { await tx.RollbackAsync(); throw; }
    }
}

public sealed record LibraryStats(int Games, int Owned, int Series, int Achievements, int Unlocked);
public sealed record GameSummary(int Id, string Name, string? CoverUrl, string Status, byte? MyRating, DateTime? ReleaseDate, decimal? PlayTimeHours, string? SeriesName, int OwnedCopies, int AchievementTotal, int AchievementUnlocked, string? Genres = null);
public sealed record GameDetails(int Id, string Name, int? IgdbId, string? Summary, string? CoverUrl, string? IgdbUrl, DateTime? ReleaseDate, decimal? IgdbRating, byte? MyRating, string Status, decimal? PlayTimeHours, string? Notes, int? SeriesId = null, string? SeriesName = null, string? Genres = null, string? Developers = null, string? Publishers = null, string? Franchises = null, string? Screenshots = null, string? Videos = null, IReadOnlyList<CollectionItem>? Collection = null, IReadOnlyList<DlcItem>? Dlcs = null);
public sealed record CollectionItem(int Id, string Platform, string Store, string Edition, bool Owned, string? Format);
public sealed record DlcItem(int Id, int? IgdbId, string Name, string? Type, string? IgdbUrl, DateTime? ReleaseDate, IReadOnlyList<DlcCopyItem> Copies);
public sealed record DlcCopyItem(int CollectionEntryId, string Platform, string Store, string Edition, bool Owned);
public sealed record OwnedCollectionItem(int Id, int GameId, string GameName, string? CoverUrl, string Platform, string Store, string Edition, string? Format, bool Owned, string Status);
public sealed record AchievementItem(int Id, string? SetName, string Platform, string? Store, string Name, string? Description, string? ImageUrl, string? Tier, string? ScoreType, decimal? RarityPercent, bool IsHidden, bool IsMissable, bool IsRare, bool Unlocked, DateTime? UnlockedAt);
public sealed record GlobalAchievementItem(int Id, int GameId, string GameName, string Name, string? Description, string? ImageUrl, string? SetName, string Platform, string? Store, string? Tier, string? ScoreType, int? Points, decimal? RarityPercent, bool IsHidden, bool IsMissable, bool IsRare, bool Unlocked, DateTime? UnlockedAt);
public sealed record CreateGameRequest(string Name, int? IgdbId, string? CoverUrl, string? IgdbUrl, string? Status, string? Notes);
public sealed record UpdateGameRequest(string? Status, byte? MyRating, decimal? PlayTimeHours, string? Notes);
public sealed record CatalogOption(int Id, string Name);
public sealed record CollectionEntryRequest(int PlatformId, int StoreId, string? Edition, bool Owned, string? Format, string? Notes);
public sealed record SeriesSummary(int Id, string Name, int? IgdbId, int TotalGames, int OwnedGames, int CompletedGames, decimal CompletionPercent);
public sealed record SeriesDetails(int Id, string Name, int? IgdbId, int TotalGames, int OwnedGames, int CompletedGames, decimal CompletionPercent, IReadOnlyList<SeriesGame> Games);
public sealed record SeriesGame(int Id, string Name, string? CoverUrl, string Status, bool Owned);
