using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class NotionWebhook
{
    private readonly GameEnrichmentService _service;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotionWebhook> _logger;

    public NotionWebhook(
        GameEnrichmentService service,
        IConfiguration configuration,
        ILogger<NotionWebhook> logger)
    {
        _service = service;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("NotionWebhook")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "NotionWebhook")]
        HttpRequestData request)
    {
        using var reader = new StreamReader(request.Body);
        var rawBody = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return await Response(
                request,
                HttpStatusCode.BadRequest,
                "Body vacío");
        }

        using var json = JsonDocument.Parse(rawBody);

        if (json.RootElement.TryGetProperty(
                "verification_token",
                out var verificationToken))
        {
            var token = verificationToken.GetString();

            _logger.LogWarning(
                "TOKEN DE VERIFICACION DE NOTION: {Token}",
                token);

            return await Response(
                request,
                HttpStatusCode.OK,
                "verification token recibido");
        }

        var savedToken = _configuration[
            "NOTION_VERIFICATION_TOKEN"];

        if (string.IsNullOrWhiteSpace(savedToken))
        {
            _logger.LogError(
                "Falta NOTION_VERIFICATION_TOKEN");

            return await Response(
                request,
                HttpStatusCode.Unauthorized,
                "Webhook no configurado");
        }

        if (!request.Headers.TryGetValues(
                "X-Notion-Signature",
                out var signatureValues))
        {
            return await Response(
                request,
                HttpStatusCode.Unauthorized,
                "Firma ausente");
        }

        var signature = signatureValues.FirstOrDefault();

        if (!IsValidSignature(
                rawBody,
                signature,
                savedToken))
        {
            return await Response(
                request,
                HttpStatusCode.Unauthorized,
                "Firma inválida");
        }

        var root = json.RootElement;

        var eventType = root.TryGetProperty(
                "type",
                out var typeProperty)
            ? typeProperty.GetString()
            : null;

        if (eventType != "page.created")
        {
            return await Response(
                request,
                HttpStatusCode.OK,
                "Evento ignorado");
        }

        var pageId = root
            .GetProperty("entity")
            .GetProperty("id")
            .GetString();

        if (string.IsNullOrWhiteSpace(pageId))
        {
            return await Response(
                request,
                HttpStatusCode.BadRequest,
                "Page ID ausente");
        }

        try
        {
            await _service.ProcessPageAsync(pageId);

            return await Response(
                request,
                HttpStatusCode.OK,
                "Procesado");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error procesando página {PageId}",
                pageId);

            return await Response(
                request,
                HttpStatusCode.InternalServerError,
                "Error procesando página");
        }
    }

    private static bool IsValidSignature(
        string rawBody,
        string? receivedSignature,
        string verificationToken)
    {
        if (string.IsNullOrWhiteSpace(receivedSignature))
        {
            return false;
        }

        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes(verificationToken));

        var hash = hmac.ComputeHash(
            Encoding.UTF8.GetBytes(rawBody));

        var expected =
            "sha256=" +
            Convert.ToHexString(hash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(receivedSignature));
    }

    private static async Task<HttpResponseData> Response(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string message)
    {
        var response = request.CreateResponse(statusCode);

        await response.WriteAsJsonAsync(new
        {
            ok = statusCode == HttpStatusCode.OK,
            message
        });

        return response;
    }
}