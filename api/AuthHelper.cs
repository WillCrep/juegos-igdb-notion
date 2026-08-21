using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceApp.Models;
using Microsoft.Azure.Functions.Worker.Http;

namespace FinanceApp;

public static class AuthHelper
{
    // Clave secreta garantizada de 256 bits (32 caracteres)
    private static readonly byte[] SecretKeyBytes = Encoding.UTF8.GetBytes(
        Environment.GetEnvironmentVariable("JwtSecret") ?? "ClaveMaestraSeguraFinanzasHogar_2026_Minimo32Caracteres!"
    );

    // =========================================================================
    // 1. HASH Y VERIFICACIÓN DE CONTRASEÑA (PBKDF2 NATIVO)
    // =========================================================================
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

    // =========================================================================
    // 2. GENERADOR DE JWT ESTÁNDAR (RFC 7519 NATIVO CON HMAC-SHA256)
    // =========================================================================
    public static string GenerateJwtToken(int userId, string email, string fullName, int? householdId, string? householdName, string role)
    {
        // Header
        var headerJson = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" });
        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));

        // Payload
        var payloadDict = new Dictionary<string, object>
        {
            { "userId", userId },
            { "email", email },
            { "name", fullName },
            { "role", role },
            { "exp", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() },
            { "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };

        if (householdId.HasValue)
        {
            payloadDict["householdId"] = householdId.Value;
            payloadDict["householdName"] = householdName ?? string.Empty;
        }

        var payloadJson = JsonSerializer.Serialize(payloadDict);
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        // Firma HMAC-SHA256
        var dataToSign = $"{headerBase64}.{payloadBase64}";
        using var hmac = new HMACSHA256(SecretKeyBytes);
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        var signatureBase64 = Base64UrlEncode(signatureBytes);

        return $"{dataToSign}.{signatureBase64}";
    }

    // =========================================================================
    // 3. VALIDADOR DE JWT NATIVO (100% INMUNE A CONFLICTOS DE AZURE)
    // =========================================================================
    public static (AuthenticatedUser? User, string? Error) ValidateToken(HttpRequestData req)
    {
        string? token = null;

        // Leer de Authorization
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
            return (null, "No se encontró token en Authorization ni en x-access-token.");

        var parts = token.Split('.');
        if (parts.Length != 3)
            return (null, "Formato JWT inválido (se esperaban 3 partes).");

        // 1. Validar Firma Criptográfica
        var dataToSign = $"{parts[0]}.{parts[1]}";
        using var hmac = new HMACSHA256(SecretKeyBytes);
        var expectedSignatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        var expectedSignature = Base64UrlEncode(expectedSignatureBytes);

        var actualSignatureBytes = Encoding.UTF8.GetBytes(parts[2]);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);

        if (!CryptographicOperations.FixedTimeEquals(actualSignatureBytes, expectedBytes))
        {
            return (null, "Firma del token no coincide con el servidor.");
        }

        // 2. Decodificar Payload y Validar Expiración
        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);
            if (claims == null) return (null, "Payload de token corrupto.");

            if (claims.TryGetValue("exp", out var expElement) && expElement.TryGetInt64(out var exp))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now > exp)
                    return (null, "El token ha expirado.");
            }

            int userId = claims["userId"].GetInt32();
            string email = claims.TryGetValue("email", out var e) ? e.GetString() ?? "" : "";
            string name = claims.TryGetValue("name", out var n) ? n.GetString() ?? "" : "";
            string role = claims.TryGetValue("role", out var r) ? r.GetString() ?? "MEMBER" : "MEMBER";

            int? householdId = null;
            if (claims.TryGetValue("householdId", out var hid) && hid.ValueKind == JsonValueKind.Number)
                householdId = hid.GetInt32();

            string? householdName = claims.TryGetValue("householdName", out var hn) ? hn.GetString() : null;

            return (new AuthenticatedUser(userId, email, name, householdId, householdName, role), null);
        }
        catch (Exception ex)
        {
            return (null, $"Error decodificando claims: {ex.Message}");
        }
    }

    // Helpers Base64URL según RFC 7515
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static byte[] Base64UrlDecode(string input)
    {
        var output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 2: output += "=="; break;
            case 3: output += "="; break;
        }
        return Convert.FromBase64String(output);
    }
}