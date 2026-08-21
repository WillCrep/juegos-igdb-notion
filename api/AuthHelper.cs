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
    // Clave fija garantizada de al menos 256 bits (32 caracteres)
    private const string DefaultSecret = ".Ge?5%OS7N:u+V(5,m#1[6Jelq1P~([6k:/t0}V-#=hncESm8lB~Gl5uZo4Dg,,3";

 // Método centralizado para obtener siempre la misma clave y el mismo KeyId
    private static SymmetricSecurityKey GetSecurityKey()
    {
        var envSecret = Environment.GetEnvironmentVariable("JwtSecret");
        var secret = (!string.IsNullOrWhiteSpace(envSecret) && envSecret.Length >= 32) ? envSecret : DefaultSecret;

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))
        {
            KeyId = "finanzas-jwt-key-v1" // Identificador explícito de clave
        };
    }

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

 // 3. Generar JWT Token (Firmado con GetSecurityKey)
    public static string GenerateJwtToken(int userId, string email, string fullName, int? householdId, string? householdName, string role)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = GetSecurityKey();

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
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

      // 4. Validar Token (Validado con el mismo GetSecurityKey)
    public static (AuthenticatedUser? User, string? Error) ValidateToken(HttpRequestData req)
    {
        string? token = null;

        if (req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            var header = authHeaders.FirstOrDefault();
            if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = header.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrEmpty(token) && req.Headers.TryGetValues("x-access-token", out var customHeaders))
        {
            token = customHeaders.FirstOrDefault()?.Trim();
        }

        if (string.IsNullOrEmpty(token))
        {
            return (null, "No se encontró el token en las cabeceras 'Authorization' ni 'x-access-token'.");
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = GetSecurityKey();

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            }, out var validatedToken);

            if (validatedToken is JwtSecurityToken jwtToken)
            {
                var userIdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "userId" || c.Type == "nameid")?.Value;
                if (string.IsNullOrEmpty(userIdClaim)) 
                    return (null, "El token no contiene el claim 'userId'.");

                var userId = int.Parse(userIdClaim);
                var email = jwtToken.Claims.FirstOrDefault(c => c.Type == "email")?.Value ?? "";
                var name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? "";
                var role = jwtToken.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "MEMBER";

                var householdClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "householdId")?.Value;
                int? householdId = string.IsNullOrEmpty(householdClaim) ? null : int.Parse(householdClaim);
                var householdName = jwtToken.Claims.FirstOrDefault(c => c.Type == "householdName")?.Value;

                return (new AuthenticatedUser(userId, email, name, householdId, householdName, role), null);
            }

            return (null, "Formato de token no reconocido.");
        }
        catch (Exception ex)
        {
            return (null, $"Fallo de validación JWT: {ex.Message}");
        }
    }

}