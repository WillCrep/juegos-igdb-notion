using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class AiConsultant
{
    private readonly AiConsultantService service;
    public AiConsultant(AiConsultantService service) => this.service = service;

    [Function("AiProfiles")]
    public async Task<HttpResponseData> Profiles([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ai/prompts")] HttpRequestData request)
        => await Json(request, HttpStatusCode.OK, (await service.GetProfilesAsync()).Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description
        }));

    [Function("AiSaveProfile")]
    public async Task<HttpResponseData> SaveProfile([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai/prompts")] HttpRequestData request)
    {
        if (!IsAdmin(request)) return await Json(request, HttpStatusCode.Unauthorized, new { error = "Se requiere la clave administrativa." });
        try
        {
            var profile = await JsonSerializer.DeserializeAsync<PromptProfile>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Instructions))
                return await Json(request, HttpStatusCode.BadRequest, new { error = "Id, nombre e instrucciones son obligatorios." });
            return await Json(request, HttpStatusCode.OK, await service.SaveProfileAsync(profile));
        }
        catch (Exception ex) { return await Json(request, HttpStatusCode.BadRequest, new { error = ex.Message }); }
    }

    [Function("AiDeleteProfile")]
    public async Task<HttpResponseData> DeleteProfile([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "ai/prompts/{id}")] HttpRequestData request, string id)
    {
        if (!IsAdmin(request)) return await Json(request, HttpStatusCode.Unauthorized, new { error = "Se requiere la clave administrativa." });
        try { await service.DeleteProfileAsync(id); return request.CreateResponse(HttpStatusCode.NoContent); }
        catch (Exception ex) { return await Json(request, HttpStatusCode.BadRequest, new { error = ex.Message }); }
    }

    [Function("AiChat")]
    public async Task<HttpResponseData> Chat([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai/chat")] HttpRequestData request)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<AiChatRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (body is null) return await Json(request, HttpStatusCode.BadRequest, new { error = "Cuerpo inválido." });
            return await Json(request, HttpStatusCode.OK, new { answer = await service.AskAsync(body), mode = body.Mode });
        }
        catch (ArgumentException ex) { return await Json(request, HttpStatusCode.BadRequest, new { error = ex.Message }); }
        catch (Exception ex) { return await Json(request, HttpStatusCode.BadGateway, new { error = ex.Message }); }
    }

    private static bool IsAdmin(HttpRequestData request)
    {
        var expected = Environment.GetEnvironmentVariable("AI_ADMIN_KEY");
        return !string.IsNullOrWhiteSpace(expected) && request.Headers.TryGetValues("x-ai-admin-key", out var values) && values.Contains(expected);
    }

    private static async Task<HttpResponseData> Json(HttpRequestData request, HttpStatusCode status, object value)
    {
        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(value);
        return response;
    }
}
