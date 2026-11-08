using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

public sealed class ArcadiaDatabase
{
    private const int PasswordIterations = 210_000;
    private readonly string connectionString =
        Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
        ?? throw new InvalidOperationException("SQL_CONNECTION_STRING no está configurada.");

    public async Task<RegisteredAccount?> RegisterAsync(
        string email,
        string displayName,
        string password,
        CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var passwordHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        var userId = Guid.NewGuid();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            await using (var command = new SqlCommand("""
                INSERT INTO dbo.ArcadiaUsers (UserId, Email, NormalizedEmail, DisplayName, PasswordSalt, PasswordHash)
                VALUES (@UserId, @Email, @NormalizedEmail, @DisplayName, @PasswordSalt, @PasswordHash);
                """, connection, transaction))
            {
                command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
                command.Parameters.Add("@Email", SqlDbType.NVarChar, 254).Value = email;
                command.Parameters.Add("@NormalizedEmail", SqlDbType.NVarChar, 254).Value = email.ToUpperInvariant();
                command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 80).Value = displayName;
                command.Parameters.Add("@PasswordSalt", SqlDbType.VarBinary, 16).Value = salt;
                command.Parameters.Add("@PasswordHash", SqlDbType.VarBinary, 32).Value = passwordHash;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var session = await CreateSessionAsync(connection, transaction, userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RegisteredAccount(userId, email, displayName, session.Token, session.ExpiresAt);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<RegisteredAccount?> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        StoredCredentials? credentials = null;

        await using (var command = new SqlCommand("""
            SELECT UserId, Email, DisplayName, PasswordSalt, PasswordHash
            FROM dbo.ArcadiaUsers
            WHERE NormalizedEmail = @NormalizedEmail;
            """, connection, transaction))
        {
            command.Parameters.Add("@NormalizedEmail", SqlDbType.NVarChar, 254).Value =
                email.ToUpperInvariant();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                credentials = new StoredCredentials(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (byte[])reader["PasswordSalt"],
                    (byte[])reader["PasswordHash"]);
            }
        }

        if (credentials is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var candidateHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            credentials.PasswordSalt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        if (!CryptographicOperations.FixedTimeEquals(candidateHash, credentials.PasswordHash))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var session = await CreateSessionAsync(connection, transaction, credentials.UserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RegisteredAccount(
            credentials.UserId,
            credentials.Email,
            credentials.DisplayName,
            session.Token,
            session.ExpiresAt);
    }

    public async Task<AuthenticatedAccount?> AuthenticateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT u.UserId, u.Email, u.DisplayName
            FROM dbo.ArcadiaSessions s
            INNER JOIN dbo.ArcadiaUsers u ON u.UserId = s.UserId
            WHERE s.TokenHash = @TokenHash AND s.ExpiresAt > SYSUTCDATETIME();
            """, connection);
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthenticatedAccount(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task RevokeSessionAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken)
    {
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            DELETE FROM dbo.ArcadiaSessions
            WHERE UserId = @UserId AND TokenHash = @TokenHash;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LibrarySnapshot> GetSnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT Revision, Payload, UpdatedAt
            FROM dbo.ArcadiaLibraries
            WHERE UserId = @UserId;
            """, connection);
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LibrarySnapshot(0, null, null);
        }

        return new LibrarySnapshot(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetDateTime(2));
    }

    public async Task<SnapshotWriteResult> SaveSnapshotAsync(
        Guid userId,
        long baseRevision,
        string payload,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        long currentRevision;
        await using (var command = new SqlCommand("""
            SELECT Revision
            FROM dbo.ArcadiaLibraries WITH (UPDLOCK, HOLDLOCK)
            WHERE UserId = @UserId;
            """, connection, transaction))
        {
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            currentRevision = result is null ? 0 : Convert.ToInt64(result);
        }

        if (currentRevision != baseRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SnapshotWriteResult(false, currentRevision, null);
        }

        var nextRevision = currentRevision + 1;
        await using (var command = new SqlCommand(currentRevision == 0
            ? """
              INSERT INTO dbo.ArcadiaLibraries (UserId, Revision, Payload, UpdatedAt)
              VALUES (@UserId, @Revision, @Payload, SYSUTCDATETIME());
              """
            : """
              UPDATE dbo.ArcadiaLibraries
              SET Revision = @Revision, Payload = @Payload, UpdatedAt = SYSUTCDATETIME()
              WHERE UserId = @UserId;
              """, connection, transaction))
        {
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            command.Parameters.Add("@Revision", SqlDbType.BigInt).Value = nextRevision;
            command.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new SqlCommand(
            "SELECT UpdatedAt FROM dbo.ArcadiaLibraries WHERE UserId = @UserId;",
            connection,
            transaction))
        {
            command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
            var updatedAt = (DateTime)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("No se pudo leer la fecha de sincronización."));
            await transaction.CommitAsync(cancellationToken);
            return new SnapshotWriteResult(true, nextRevision, updatedAt);
        }
    }

    private static async Task<CreatedSession> CreateSessionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var expiresAt = DateTime.UtcNow.AddDays(30);

        await using var command = new SqlCommand("""
            INSERT INTO dbo.ArcadiaSessions (TokenHash, UserId, ExpiresAt)
            VALUES (@TokenHash, @UserId, @ExpiresAt);
            """, connection, transaction);
        command.Parameters.Add("@TokenHash", SqlDbType.VarBinary, 32).Value = tokenHash;
        command.Parameters.Add("@UserId", SqlDbType.UniqueIdentifier).Value = userId;
        command.Parameters.Add("@ExpiresAt", SqlDbType.DateTime2).Value = expiresAt;
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new CreatedSession(token, expiresAt);
    }
}

public sealed record RegisteredAccount(
    Guid UserId,
    string Email,
    string DisplayName,
    string Token,
    DateTime ExpiresAt);

public sealed record AuthenticatedAccount(Guid UserId, string Email, string DisplayName);

public sealed record LibrarySnapshot(long Revision, string? Snapshot, DateTime? UpdatedAt);

public sealed record SnapshotWriteResult(bool Saved, long Revision, DateTime? UpdatedAt);

internal sealed record CreatedSession(string Token, DateTime ExpiresAt);

internal sealed record StoredCredentials(
    Guid UserId,
    string Email,
    string DisplayName,
    byte[] PasswordSalt,
    byte[] PasswordHash);
