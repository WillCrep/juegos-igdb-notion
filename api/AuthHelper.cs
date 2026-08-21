using System.Data;
using System.Security.Cryptography;
using System.Text;
using FinanceApp.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace FinanceApp;

public static class AuthHelper
{
    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING") ?? string.Empty;

    // 1. HASH Y VERIFICACIÓN DE CONTRASEÑA (PBKDF2 NATIVO)
    public static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32
        );
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;

        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] originalHash = Convert.FromBase64String(parts[1]);

        byte[] newHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32
        );

        return CryptographicOperations.FixedTimeEquals(originalHash, newHash);
    }

    // 2. CREAR TOKEN DE SESIÓN EN BASE DE DATOS
    public static async Task<string> CreateSessionTokenAsync(SqlConnection conn, SqlTransaction? tx, int userId)
    {
        // Genera un token aleatorio seguro de 64 caracteres hexadecimales
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToHexString(randomBytes).ToLower();

        var sql = @"
            INSERT INTO BnkUserTokens (UserId, Token, ExpiresAt)
            VALUES (@UserId, @Token, DATEADD(DAY, 60, SYSUTCDATETIME()));";

        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@Token", SqlDbType.NVarChar).Value = token;

        await cmd.ExecuteNonQueryAsync();
        return token;
    }

    // 3. VALIDAR TOKEN EN BASE DE DATOS (100% PRECISO Y DIRECTO)
    public static async Task<(AuthenticatedUser? User, string? Error)> ValidateTokenAsync(HttpRequestData req)
    {
        string? token = null;

        // Leer de Authorization: Bearer <token>
        if (req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            var h = authHeaders.FirstOrDefault();
            if (!string.IsNullOrEmpty(h) && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = h.Substring("Bearer ".Length).Trim();
        }

        // Leer de x-access-token
        if (string.IsNullOrEmpty(token) && req.Headers.TryGetValues("x-access-token", out var customHeaders))
        {
            token = customHeaders.FirstOrDefault()?.Trim();
        }

        if (string.IsNullOrEmpty(token))
            return (null, "No se proporcionó token en las cabeceras.");

        try
        {
            using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT u.UserId, u.Email, u.FullName, hm.HouseholdId, h.Name AS HouseholdName, ISNULL(hm.Role, 'MEMBER') AS Role
                FROM BnkUserTokens t
                INNER JOIN BnkUsers u ON t.UserId = u.UserId
                LEFT JOIN BnkHouseholdMembers hm ON u.UserId = hm.UserId
                LEFT JOIN BnkHouseholds h ON hm.HouseholdId = h.HouseholdId
                WHERE t.Token = @Token AND t.ExpiresAt > SYSUTCDATETIME() AND t.IsRevoked = 0";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@Token", SqlDbType.NVarChar).Value = token;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var userId = reader.GetInt32(0);
                var email = reader.GetString(1);
                var fullName = reader.GetString(2);
                int? householdId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                string? householdName = reader.IsDBNull(4) ? null : reader.GetString(4);
                string role = reader.GetString(5);

                return (new AuthenticatedUser(userId, email, fullName, householdId, householdName, role), null);
            }

            return (null, "Token de sesión inválido, revocado o expirado.");
        }
        catch (Exception ex)
        {
            return (null, $"Error al verificar sesión en BD: {ex.Message}");
        }
    }
}