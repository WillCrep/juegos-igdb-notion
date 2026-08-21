using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinanceApp.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.IdentityModel.Tokens;

namespace FinanceApp;

public static class AuthHelper
{
    private static readonly string JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "ClaveSuperSecretaDeDesarrollo_Minimo32Caracteres12345!";

    // 1. Hash seguro de contraseña
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

    // 2. Verificar contraseña
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

    // 3. Generar JWT Token
    public static string GenerateJwtToken(int userId, string email, string fullName, int? householdId, string? householdName, string role)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(JwtSecret);

        var claims = new List<Claim>
        {
            new Claim("userId", userId.ToString()),
            new Claim("email", email),
            new Claim("name", fullName),
            new Claim("role", role)
        };

        if (householdId.HasValue)
        {
            claims.Add(new Claim("householdId", householdId.Value.ToString()));
            claims.Add(new Claim("householdName", householdName ?? string.Empty));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddDays(30),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    // 4. VALIDAR TOKEN EN CADA PETICIÓN HTTP (SOPORTE DUAL DE HEADERS)
    public static AuthenticatedUser? ValidateToken(HttpRequestData req)
    {
        string? token = null;

        // 1. Intentar leer de la cabecera estándar Authorization
        if (req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            var header = authHeaders.FirstOrDefault();
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header.Substring("Bearer ".Length).Trim();
            }
        }

        // 2. Si Azure filtró Authorization, leer de x-access-token
        if (string.IsNullOrEmpty(token) && req.Headers.TryGetValues("x-access-token", out var customHeaders))
        {
            token = customHeaders.FirstOrDefault()?.Trim();
        }

        // Si no vino en ninguna cabecera, rechazar
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("[AUTH] No se encontró token en Authorization ni en x-access-token");
            return null;
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.InboundClaimTypeMap.Clear();

        var key = Encoding.UTF8.GetBytes(JwtSecret);

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out _);

            var userIdClaim = principal.FindFirst("userId")?.Value;
            if (string.IsNullOrEmpty(userIdClaim)) return null;

            var userId = int.Parse(userIdClaim);
            var email = principal.FindFirst("email")?.Value ?? "";
            var name = principal.FindFirst("name")?.Value ?? "";
            var role = principal.FindFirst("role")?.Value ?? "MEMBER";
            
            var householdClaim = principal.FindFirst("householdId")?.Value;
            int? householdId = string.IsNullOrEmpty(householdClaim) ? null : int.Parse(householdClaim);
            var householdName = principal.FindFirst("householdName")?.Value;

            return new AuthenticatedUser(userId, email, name, householdId, householdName, role);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AUTH ERROR] Falló validación de firma: {ex.Message}");
            return null;
        }
    }
}