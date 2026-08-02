using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

public sealed class NotionClient
{
    private const string NotionVersion = "2026-03-11";

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;

    public NotionClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        object? body = null)
    {
        var token = _configuration["NOTION_TOKEN"]
            ?? throw new InvalidOperationException("NOTION_TOKEN no está configurado");
        
        var request = new HttpRequestMessage(
            method,
            $"https://api.notion.com/v1/{url}"
        );

        request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", token);

        request.Headers.Add("Notion-Version", NotionVersion);

        if(body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public async Task<JsonDocument> GetPageAsync(string pageId)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"pages/{pageId}"
        );

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(json);
    }

    public async Task UpdatePageAsync(
        string pageId,
        Dictionary<string, object> properties
    )
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"pages/{pageId}",
            new
            {
                properties
            });
        
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Error actualizando Notion: {(int)response.StatusCode} {error}"
            );
        }
    }

    public async Task AppendBlocksAsync(
        string pageId,
        List<object> children)
    {
        using var request = CreateRequest(
            new HttpMethod("PATCH"),
            $"blocks/{pageId}/children",
            new
            {
                children
            });
        
        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Error agregando bloques: {(int)response.StatusCode} {error}");
        }
    }
}