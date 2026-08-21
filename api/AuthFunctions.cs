using System.Net;
using System.Text.Json;
using FinanceApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace FinanceApp;

public class AuthFunctions
{
    private readonly string _connectionString;

    public AuthFunctions()
    {
        _connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING") ?? string.Empty;
    }

    // POST /api/auth/register
    [Function("Register")]
    public async Task<HttpResponseData> Register([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<RegisterDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // 1. Validar si ya existe el correo
        using (var cmd = new SqlCommand("SELECT 1 FROM BnkUsers WHERE Email = @Email", conn))
        {
            cmd.Parameters.AddWithValue("@Email", dto.Email.ToLower().Trim());
            var exists = await cmd.ExecuteScalarAsync();
            if (exists != null)
            {
                var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                await conflict.WriteStringAsync("El correo ya está registrado.");
                return conflict;
            }
        }

        using var tx = conn.BeginTransaction();
        try
        {
            // 2. Insertar Usuario
            int userId;
            var hash = AuthHelper.HashPassword(dto.Password);
            var insertUserSql = @"
                INSERT INTO BnkUsers (Email, PasswordHash, FullName) 
                OUTPUT INSERTED.UserId
                VALUES (@Email, @Hash, @FullName);";

            using (var cmd = new SqlCommand(insertUserSql, conn, tx))
            {
                cmd.Parameters.AddWithValue("@Email", dto.Email.ToLower().Trim());
                cmd.Parameters.AddWithValue("@Hash", hash);
                cmd.Parameters.AddWithValue("@FullName", dto.FullName);
                userId = (int)await cmd.ExecuteScalarAsync();
            }

            int? householdId = null;
            string? householdName = null;
            string role = "MEMBER";

            // 3. Crear o Unirse a Hogar
            if (!string.IsNullOrWhiteSpace(dto.InviteCode))
            {
                // Unirse a Hogar existente
                var findHomeSql = "SELECT HouseholdId, Name FROM BnkHouseholds WHERE InviteCode = @Code";
                using (var cmd = new SqlCommand(findHomeSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Code", dto.InviteCode.Trim().ToUpper());
                    using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        householdId = reader.GetInt32(0);
                        householdName = reader.GetString(1);
                    }
                }

                if (householdId.HasValue)
                {
                    var joinSql = "INSERT INTO BnkHouseholdMembers (HouseholdId, UserId, Role) VALUES (@HouseholdId, @UserId, 'MEMBER')";
                    using var cmd = new SqlCommand(joinSql, conn, tx);
                    cmd.Parameters.AddWithValue("@HouseholdId", householdId.Value);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            else if (!string.IsNullOrWhiteSpace(dto.HouseholdName))
            {
                // Crear nuevo Hogar
                role = "ADMIN";
                householdName = dto.HouseholdName;
                var inviteCode = "CASA-" + Random.Shared.Next(1000, 9999);

                var createHomeSql = @"
                    INSERT INTO BnkHouseholds (Name, InviteCode, CreatedByUserId) 
                    OUTPUT INSERTED.HouseholdId
                    VALUES (@Name, @Code, @UserId);";

                using (var cmd = new SqlCommand(createHomeSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Name", householdName);
                    cmd.Parameters.AddWithValue("@Code", inviteCode);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    householdId = (int)await cmd.ExecuteScalarAsync();
                }

                var addMemberSql = "INSERT INTO BnkHouseholdMembers (HouseholdId, UserId, Role) VALUES (@HouseholdId, @UserId, 'ADMIN')";
                using (var cmd = new SqlCommand(addMemberSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@HouseholdId", householdId.Value);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            tx.Commit();

            
            var token = await AuthHelper.CreateSessionTokenAsync(conn, tx, userId);
            var res = req.CreateResponse(HttpStatusCode.Created);
            await res.WriteAsJsonAsync(new { token, user = new { userId, fullName = dto.FullName, email = dto.Email }, household = new { householdId, name = householdName, role } });
            return res;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message);
            return err;
        }
    }

    // POST /api/auth/login
    [Function("Login")]
    public async Task<HttpResponseData> Login([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<LoginDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var query = @"
            SELECT u.UserId, u.PasswordHash, u.FullName, hm.HouseholdId, h.Name AS HouseholdName, hm.Role
            FROM BnkUsers u
            LEFT JOIN BnkHouseholdMembers hm ON u.UserId = hm.UserId
            LEFT JOIN BnkHouseholds h ON hm.HouseholdId = h.HouseholdId
            WHERE u.Email = @Email";

        using var cmd = new SqlCommand(query, conn);
        cmd.Parameters.AddWithValue("@Email", dto.Email.ToLower().Trim());
        using var reader = await cmd.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            var userId = reader.GetInt32(0);
            var storedHash = reader.GetString(1);
            var fullName = reader.GetString(2);
            int? householdId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            string? householdName = reader.IsDBNull(4) ? null : reader.GetString(4);
            string role = reader.IsDBNull(5) ? "MEMBER" : reader.GetString(5);

            if (AuthHelper.VerifyPassword(dto.Password, storedHash))
            {
                var token = await AuthHelper.CreateSessionTokenAsync(conn, null, userId);
                var res = req.CreateResponse(HttpStatusCode.OK);
                await res.WriteAsJsonAsync(new { 
                    token, 
                    user = new { userId, fullName, email = dto.Email }, 
                    household = new { householdId, name = householdName, role } 
                });
                return res;
            }
        }

        var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
        await unauth.WriteStringAsync("Correo o contraseña incorrectos.");
        return unauth;
    }
}