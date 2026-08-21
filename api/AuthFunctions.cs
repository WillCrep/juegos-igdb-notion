using System.Data;
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

    // =========================================================================
    // 1. REGISTRO DE USUARIO (POST /api/auth/register)
    // =========================================================================
    [Function("Register")]
    public async Task<HttpResponseData> Register([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<RegisterDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // 1.1 Validar si el correo ya existe
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
            // 1.2 Insertar Usuario
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

            // 1.3 Unirse a Hogar existente si envió código
            if (!string.IsNullOrWhiteSpace(dto.InviteCode))
            {
                var findHomeSql = "SELECT HouseholdId, Name FROM BnkHouseholds WHERE InviteCode = @Code";
                using (var cmd = new SqlCommand(findHomeSql, conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Code", dto.InviteCode.Trim().ToUpper());
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            householdId = reader.GetInt32(0);
                            householdName = reader.GetString(1);
                        }
                    } // 👈 El reader se cierra aquí
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
            // 1.4 Crear nuevo Hogar si envió nombre
            else if (!string.IsNullOrWhiteSpace(dto.HouseholdName))
            {
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

            // 1.5 Crear Token de sesión en BD
            var token = await AuthHelper.CreateSessionTokenAsync(conn, tx, userId);

            tx.Commit();

            var res = req.CreateResponse(HttpStatusCode.Created);
            await res.WriteAsJsonAsync(new { 
                token, 
                user = new { userId, fullName = dto.FullName, email = dto.Email }, 
                household = new { householdId, name = householdName, role } 
            });
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

    // =========================================================================
    // 2. INICIO DE SESIÓN (POST /api/auth/login) - CORREGIDO
    // =========================================================================
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
            SELECT u.UserId, u.PasswordHash, u.FullName, hm.HouseholdId, h.Name AS HouseholdName, ISNULL(hm.Role, 'MEMBER') AS Role
            FROM BnkUsers u
            LEFT JOIN BnkHouseholdMembers hm ON u.UserId = hm.UserId
            LEFT JOIN BnkHouseholds h ON hm.HouseholdId = h.HouseholdId
            WHERE u.Email = @Email";

        int userId = 0;
        string storedHash = string.Empty;
        string fullName = string.Empty;
        int? householdId = null;
        string? householdName = null;
        string role = "MEMBER";
        bool userFound = false;

        // 2.1 Leer datos del usuario y CERRAR el DataReader de inmediato
        using (var cmd = new SqlCommand(query, conn))
        {
            cmd.Parameters.AddWithValue("@Email", dto.Email.ToLower().Trim());
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    userFound = true;
                    userId = reader.GetInt32(0);
                    storedHash = reader.GetString(1);
                    fullName = reader.GetString(2);
                    householdId = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                    householdName = reader.IsDBNull(4) ? null : reader.GetString(4);
                    role = reader.GetString(5);
                }
            } // 👈 El DataReader se cierra y libera la conexión AQUÍ
        }

        // 2.2 Verificar contraseña con la conexión ya libre
        if (userFound && AuthHelper.VerifyPassword(dto.Password, storedHash))
        {
            // Ahora CreateSessionTokenAsync puede ejecutar su INSERT sin bloqueos
            var token = await AuthHelper.CreateSessionTokenAsync(conn, null, userId);

            var res = req.CreateResponse(HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new { 
                token, 
                user = new { userId, fullName, email = dto.Email }, 
                household = new { householdId, name = householdName, role } 
            });
            return res;
        }

        var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
        await unauth.WriteStringAsync("Correo o contraseña incorrectos.");
        return unauth;
    }
}