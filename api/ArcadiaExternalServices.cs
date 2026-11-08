using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public sealed class ArcadiaExternalServices(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration)
{
    private static readonly HashSet<string> IgdbEndpoints =
        ["games", "collections", "franchises"];
    private static readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> GroqRequests = new();
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string? accessToken;
    private DateTimeOffset tokenExpiresAt;

    public async Task<ExternalResponse> QueryIgdbAsync(
        string endpoint,
        string query,
        CancellationToken cancellationToken)
    {
        if (!IgdbEndpoints.Contains(endpoint) || query.Length is < 1 or > 8_000)
        {
            return new ExternalResponse(HttpStatusCode.BadRequest, """{"error":"Consulta IGDB inválida."}""");
        }

        var clientId = configuration["IGDB_CLIENT_ID"];
        var clientSecret = configuration["IGDB_CLIENT_SECRET"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("IGDB server credentials are not configured.");
        }

        var token = await GetIgdbTokenAsync(clientId, clientSecret, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.igdb.com/v4/{endpoint}");
        request.Headers.Add("Client-ID", clientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

        using var response = await httpClientFactory.CreateClient("igdb")
            .SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ExternalResponse(response.StatusCode, content);
    }

    public async Task<ExternalResponse> ChatCompletionAsync(
        Guid userId,
        string requestBody,
        CancellationToken cancellationToken)
    {
        if (requestBody.Length > 48_000)
        {
            return new ExternalResponse(HttpStatusCode.RequestEntityTooLarge, """{"error":"La solicitud supera el tamaño permitido."}""");
        }

        if (!AllowGroqRequest(userId))
        {
            return new ExternalResponse(HttpStatusCode.TooManyRequests, """{"error":"Se alcanzó el límite temporal de solicitudes de IA."}""");
        }

        var apiKey = configuration["GROQ_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Groq server credentials are not configured.");
        }

        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(requestBody)?.AsObject()
                ?? throw new JsonException("Expected a JSON object.");
        }
        catch (JsonException)
        {
            return new ExternalResponse(HttpStatusCode.BadRequest, """{"error":"El cuerpo de Groq no es JSON válido."}""");
        }

        if (!IsValidGroqPayload(payload))
        {
            return new ExternalResponse(HttpStatusCode.BadRequest, """{"error":"Los mensajes de Groq no son válidos o superan los límites."}""");
        }

        payload["model"] = configuration["GROQ_MODEL"] ?? "llama-3.1-8b-instant";
        if (payload["max_tokens"] is JsonNode maxTokensNode
            && (!maxTokensNode.AsValue().TryGetValue<int>(out var requestedTokens)
                || requestedTokens < 1))
        {
            return new ExternalResponse(HttpStatusCode.BadRequest, """{"error":"max_tokens inválido."}""");
        }

        payload["max_tokens"] = Math.Clamp(
            payload["max_tokens"]?.GetValue<int>() ?? 1800,
            1,
            2000);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await httpClientFactory.CreateClient("groq")
            .SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ExternalResponse(response.StatusCode, content);
    }

    private async Task<string> GetIgdbTokenAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && DateTimeOffset.UtcNow < tokenExpiresAt)
            {
                return accessToken;
            }

            var url = "https://id.twitch.tv/oauth2/token"
                + $"?client_id={Uri.EscapeDataString(clientId)}"
                + $"&client_secret={Uri.EscapeDataString(clientSecret)}"
                + "&grant_type=client_credentials";
            using var response = await httpClientFactory.CreateClient("igdb")
                .PostAsync(url, null, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("IGDB returned an empty access token.");
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires)
                && expires.TryGetInt32(out var seconds)
                    ? seconds
                    : 3600;
            tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return accessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static bool IsValidGroqPayload(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages || messages.Count is < 1 or > 20)
        {
            return false;
        }

        var totalCharacters = 0;
        foreach (var item in messages)
        {
            if (item is not JsonObject message
                || message["role"] is not JsonValue roleNode
                || !roleNode.TryGetValue<string>(out var role)
                || role is not ("system" or "user" or "assistant")
                || message["content"] is not JsonValue contentNode
                || !contentNode.TryGetValue<string>(out var content)
                || content.Length > 16_000)
            {
                return false;
            }

            totalCharacters += content.Length;
        }

        if (payload["tools"] is JsonArray tools)
        {
            if (tools.Count > 1
                || tools.Any(tool =>
                    tool is not JsonObject toolObject
                    || toolObject["type"] is not JsonValue typeNode
                    || !typeNode.TryGetValue<string>(out var type)
                    || type != "browser_search"))
            {
                return false;
            }
        }
        else if (payload.ContainsKey("tools"))
        {
            return false;
        }

        return totalCharacters <= 40_000;
    }

    private static bool AllowGroqRequest(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var requests = GroqRequests.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (requests)
        {
            while (requests.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(1))
            {
                requests.Dequeue();
            }

            if (requests.Count >= 10)
            {
                return false;
            }

            requests.Enqueue(now);
            return true;
        }
    }
}

public sealed record ExternalResponse(HttpStatusCode StatusCode, string Content);
