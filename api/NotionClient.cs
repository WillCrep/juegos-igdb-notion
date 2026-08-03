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

    public async Task<string?> FindPageByNumberAsync(
        string dataSourceId,
        string propertyName,
        int value)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"data_sources/{dataSourceId}/query",
            new
            {
                filter = new
                {
                    property = propertyName,
                    number = new
                    {
                        equals = value
                    }
                },
                page_size = 1
            });

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Error consultando la base de datos de Notion: " +
                $"{(int)response.StatusCode} {json}");
        }

        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results");

        if (results.GetArrayLength() == 0)
        {
            return null;
        }

        return results[0].GetProperty("id").GetString();
    }

    public async Task<string> GetDataSourceIdAsync(string databaseId)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"databases/{databaseId}");

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Error obteniendo la base de datos de Notion: " +
                $"{(int)response.StatusCode} {json}");
        }

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty(
                "data_sources",
                out var dataSources) ||
            dataSources.ValueKind != JsonValueKind.Array ||
            dataSources.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "No se encontró un data source para la base de datos de DLC.");
        }

        return dataSources[0]
            .GetProperty("id")
            .GetString()
            ?? throw new InvalidOperationException(
                "Notion no devolvió el ID del data source.");
    }

    public async Task<string> CreatePageAsync(
        string dataSourceId,
        Dictionary<string, object> properties)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            "pages",
            new
            {
                parent = new
                {
                    data_source_id = dataSourceId
                },
                properties
            });

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Error creando página en Notion: " +
                $"{(int)response.StatusCode} {json}");
        }

        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("id")
            .GetString()
            ?? throw new InvalidOperationException(
                "Notion no devolvió el ID de la página creada.");
    }

    public async Task AppendBlocksAsync(
        string pageId,
        List<object> children)
    {
        foreach (var batch in children.Chunk(100))
        {
            using var request = CreateRequest(
                new HttpMethod("PATCH"),
                $"blocks/{pageId}/children",
                new
                {
                    children = batch
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

    public async Task<JsonDocument> GetBlockChildrenAsync(string blockId)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"blocks/{blockId}/children?page_size=100");

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Error obteniendo bloques: {(int)response.StatusCode} {json}");
        }

        return JsonDocument.Parse(json);
    }
}
