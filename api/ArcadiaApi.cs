using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

public sealed class ArcadiaApi(
    ArcadiaDatabase database,
    ArcadiaExternalServices externalServices,
    ILogger<ArcadiaApi> logger)
{
    private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    private const int MaximumCompressedSnapshotBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    [Function("Register")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var body = await ReadJsonAsync<RegisterRequest>(request, cancellationToken);
        if (body is null
            || !IsValidEmail(body.Email)
            || string.IsNullOrWhiteSpace(body.DisplayName)
            || body.DisplayName.Trim().Length > 80
            || body.Password is null
            || body.Password.Length is < 12 or > 128)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new
            {
                error = "Indica un correo válido, un nombre de hasta 80 caracteres y una contraseña de 12 a 128 caracteres."
            });
        }

        try
        {
            var account = await database.RegisterAsync(
                body.Email.Trim(),
                body.DisplayName.Trim(),
                body.Password,
                cancellationToken);
            if (account is null)
            {
                return await JsonAsync(request, HttpStatusCode.Conflict, new { error = "Ese correo ya tiene una cuenta." });
            }

            return await JsonAsync(request, HttpStatusCode.Created, new
            {
                token = account.Token,
                expiresAt = account.ExpiresAt,
                user = new { id = account.UserId, email = account.Email, displayName = account.DisplayName }
            });
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error registering account.");
            return await ServiceUnavailableAsync(request);
        }
    }

    [Function("Login")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var body = await ReadJsonAsync<LoginRequest>(request, cancellationToken);
        if (body is null || !IsValidEmail(body.Email) || string.IsNullOrEmpty(body.Password))
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Correo o contraseña inválidos." });
        }

        try
        {
            var account = await database.LoginAsync(body.Email.Trim(), body.Password, cancellationToken);
            if (account is null)
            {
                return await JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "Correo o contraseña incorrectos." });
            }

            return await JsonAsync(request, HttpStatusCode.OK, new
            {
                token = account.Token,
                expiresAt = account.ExpiresAt,
                user = new { id = account.UserId, email = account.Email, displayName = account.DisplayName }
            });
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error signing in.");
            return await ServiceUnavailableAsync(request);
        }
    }

    [Function("CurrentAccount")]
    public async Task<HttpResponseData> CurrentAccount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var account = authentication.Account!;
        return await JsonAsync(request, HttpStatusCode.OK, new
        {
            id = account.UserId,
            email = account.Email,
            displayName = account.DisplayName
        });
    }

    [Function("Logout")]
    public async Task<HttpResponseData> Logout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var account = authentication.Account!;
        var token = GetBearerToken(request);
        if (token is null)
        {
            return await UnauthorizedAsync(request);
        }

        try
        {
            await database.RevokeSessionAsync(account.UserId, token, cancellationToken);
            return request.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error revoking a session.");
            return await ServiceUnavailableAsync(request);
        }
    }

    [Function("GetLibrarySnapshot")]
    public async Task<HttpResponseData> GetLibrarySnapshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sync/snapshot")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var account = authentication.Account!;
        try
        {
            var snapshot = await database.GetSnapshotAsync(account.UserId, cancellationToken);
            JsonElement? snapshotJson = null;
            if (snapshot.Snapshot is not null)
            {
                using var document = JsonDocument.Parse(snapshot.Snapshot);
                snapshotJson = document.RootElement.Clone();
            }

            return await JsonAsync(request, HttpStatusCode.OK, new
            {
                revision = snapshot.Revision,
                snapshot = snapshotJson,
                updatedAt = snapshot.UpdatedAt
            });
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error reading a library snapshot.");
            return await ServiceUnavailableAsync(request);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Stored library snapshot is invalid JSON.");
            return await JsonAsync(request, HttpStatusCode.InternalServerError, new { error = "La copia guardada no se puede leer." });
        }
    }

    [Function("SaveLibrarySnapshot")]
    public async Task<HttpResponseData> SaveLibrarySnapshot(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sync/snapshot")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var account = authentication.Account!;
        var parsedBody = await ReadSnapshotRequestAsync(request, cancellationToken);
        if (parsedBody.TooLarge)
        {
            return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new
            {
                error = "La biblioteca supera el tamaño máximo de 16 MiB sin comprimir o 8 MiB comprimida."
            });
        }

        var body = parsedBody.Body;
        if (body is null
            || body.BaseRevision < 0
            || body.Snapshot.ValueKind != JsonValueKind.Object
            || !body.Snapshot.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new
            {
                error = "El cuerpo debe incluir baseRevision y una biblioteca con schemaVersion 1."
            });
        }

        var payload = body.Snapshot.GetRawText();
        if (Encoding.UTF8.GetByteCount(payload) > MaximumSnapshotBytes)
        {
            return await JsonAsync(request, HttpStatusCode.RequestEntityTooLarge, new
            {
                error = "La biblioteca supera el tamaño máximo de 16 MiB sin comprimir."
            });
        }

        try
        {
            var result = await database.SaveSnapshotAsync(
                account.UserId,
                body.BaseRevision,
                payload,
                cancellationToken);
            if (!result.Saved)
            {
                return await JsonAsync(request, HttpStatusCode.Conflict, new
                {
                    error = "La biblioteca cambió en otro dispositivo. Descarga la revisión actual antes de volver a guardar.",
                    currentRevision = result.Revision
                });
            }

            return await JsonAsync(request, HttpStatusCode.OK, new
            {
                revision = result.Revision,
                updatedAt = result.UpdatedAt
            });
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error saving a library snapshot.");
            return await ServiceUnavailableAsync(request);
        }
    }

    [Function("IgdbQuery")]
    public async Task<HttpResponseData> IgdbQuery(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "igdb/query")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var body = await ReadJsonAsync<IgdbQueryRequest>(request, cancellationToken);
        if (body is null
            || string.IsNullOrWhiteSpace(body.Endpoint)
            || string.IsNullOrWhiteSpace(body.Query))
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "Endpoint o consulta IGDB inválidos." });
        }

        try
        {
            var result = await externalServices.QueryIgdbAsync(
                body.Endpoint.Trim(),
                body.Query,
                cancellationToken);
            return await ExternalResponseAsync(request, result);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "IGDB request failed.");
            return await JsonAsync(request, HttpStatusCode.BadGateway, new { error = "IGDB no está disponible en este momento." });
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "IGDB service configuration is incomplete.");
            return await ServiceUnavailableAsync(request);
        }
    }

    [Function("GroqChatCompletion")]
    public async Task<HttpResponseData> GroqChatCompletion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "groq/chat/completions")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(request, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var body = await ReadJsonAsync<JsonElement>(request, cancellationToken, 48_000);
        if (body.ValueKind != JsonValueKind.Object)
        {
            return await JsonAsync(request, HttpStatusCode.BadRequest, new { error = "El cuerpo de Groq debe ser un objeto JSON." });
        }

        try
        {
            var result = await externalServices.ChatCompletionAsync(
                authentication.Account!.UserId,
                body.GetRawText(),
                cancellationToken);
            return await ExternalResponseAsync(request, result);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Groq request failed.");
            return await JsonAsync(request, HttpStatusCode.BadGateway, new { error = "El proveedor de IA no está disponible en este momento." });
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(exception, "Groq service configuration is incomplete.");
            return await ServiceUnavailableAsync(request);
        }
    }

    private async Task<(AuthenticatedAccount? Account, HttpResponseData? Error)> AuthenticateAsync(
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var token = GetBearerToken(request);
        if (token is null)
        {
            return (null, await UnauthorizedAsync(request));
        }

        try
        {
            var account = await database.AuthenticateAsync(token, cancellationToken);
            return account is null
                ? (null, await UnauthorizedAsync(request))
                : (account, null);
        }
        catch (SqlException exception)
        {
            logger.LogError(exception, "Database error validating an access token.");
            return (null, await ServiceUnavailableAsync(request));
        }
    }

    private static string? GetBearerToken(HttpRequestData request)
    {
        if (request.Headers.TryGetValues("X-Arcadia-Session", out var sessionValues))
        {
            var sessionToken = sessionValues.FirstOrDefault()?.Trim();
            if (IsValidSessionToken(sessionToken))
            {
                return sessionToken;
            }
        }

        if (request.Headers.TryGetValues("Authorization", out var values))
        {
            var value = values.FirstOrDefault();
            if (value is not null
                && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && value.Length > 7)
            {
                var token = value[7..].Trim();
                if (IsValidSessionToken(token))
                {
                    return token;
                }
            }
        }

        return null;
    }

    private static bool IsValidSessionToken(string? token) =>
        token is not null && token.Length is >= 40 and <= 100;

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= 254
        && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var parsed)
        && string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<T?> ReadJsonAsync<T>(
        HttpRequestData request,
        CancellationToken cancellationToken,
        int maximumBytes = 16 * 1024)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                return default;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<(SaveSnapshotRequest? Body, bool TooLarge)> ReadSnapshotRequestAsync(
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValues("Content-Encoding", out var encodings))
        {
            var encoding = encodings.FirstOrDefault();
            if (!string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                return (null, false);
            }

        }

        using var input = new MemoryStream();
        var buffer = new byte[8 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (input.Length + read > MaximumSnapshotBytes)
            {
                return (null, true);
            }

            await input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var inputBytes = input.ToArray();
        var isGzip = inputBytes.Length >= 2
            && inputBytes[0] == 0x1f
            && inputBytes[1] == 0x8b;
        if (isGzip && inputBytes.Length > MaximumCompressedSnapshotBytes)
        {
            return (null, true);
        }

        byte[] jsonBytes;
        if (isGzip)
        {
            using var compressed = new MemoryStream(inputBytes);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            while ((read = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (decompressed.Length + read > MaximumSnapshotBytes)
                {
                    return (null, true);
                }

                await decompressed.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            jsonBytes = decompressed.ToArray();
        }
        else
        {
            jsonBytes = inputBytes;
        }

        try
        {
            return (
                JsonSerializer.Deserialize<SaveSnapshotRequest>(jsonBytes, JsonOptions),
                false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
        catch (InvalidDataException)
        {
            return (null, false);
        }
    }

    private static async Task<HttpResponseData> JsonAsync(
        HttpRequestData request,
        HttpStatusCode status,
        object body)
    {
        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(body);
        return response;
    }

    private static async Task<HttpResponseData> ExternalResponseAsync(
        HttpRequestData request,
        ExternalResponse result)
    {
        if ((int)result.StatusCode is < 200 or >= 300)
        {
            var statusCode = result.StatusCode == HttpStatusCode.TooManyRequests
                ? HttpStatusCode.TooManyRequests
                : result.StatusCode == HttpStatusCode.BadRequest
                    ? HttpStatusCode.BadRequest
                    : HttpStatusCode.BadGateway;
            return await JsonAsync(request, statusCode, new { error = "El proveedor externo rechazó la solicitud." });
        }

        var response = request.CreateResponse(result.StatusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(result.Content);
        return response;
    }

    private static Task<HttpResponseData> UnauthorizedAsync(HttpRequestData request) =>
        JsonAsync(request, HttpStatusCode.Unauthorized, new { error = "La sesión no es válida o expiró." });

    private static Task<HttpResponseData> ServiceUnavailableAsync(HttpRequestData request) =>
        JsonAsync(request, HttpStatusCode.ServiceUnavailable, new { error = "El servicio de datos no está disponible." });
}

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record SaveSnapshotRequest(long BaseRevision, JsonElement Snapshot);

public sealed record IgdbQueryRequest(string Endpoint, string Query);
