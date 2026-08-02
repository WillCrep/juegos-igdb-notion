using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public sealed class GameEnrichmentService
{
    private readonly IgdbClient _igdb;
    private readonly NotionClient _notion;
    private readonly ILogger<GameEnrichmentService> _logger;

    public GameEnrichmentService(
        IgdbClient igdb,
        NotionClient notion,
        ILogger<GameEnrichmentService> logger)
    {
        _igdb = igdb;
        _notion = notion;
        _logger = logger;
    }

    public async Task ProcessPageAsync(string pageId)
    {
        using var page = await _notion.GetPageAsync(pageId);

        var properties = page.RootElement.GetProperty("properties");

        var gameName = GetTitle(properties);
        var ownedPlatforms = GetMultiSelect(properties, "plataforma");

        if (string.IsNullOrWhiteSpace(gameName))
{
    _logger.LogWarning(
        "La página {PageId} todavía no tiene título. Se omitirá este evento.",
        pageId);

    return;
}

        await _notion.UpdatePageAsync(
            pageId,
            new Dictionary<string, object>
            {
                ["Estado"] = new
                {
                    select = new
                    {
                        name = "Procesando"
                    }
                }
            });
        
        using var search = await _igdb.SearchGamesAsync(gameName);

        var candidates = search.RootElement
        .EnumerateArray()
        .ToList();

        if(candidates.Count == 0)
        {
            await _notion.UpdatePageAsync(
                pageId,
                new Dictionary<string, object>
                {
                    ["Estado"] = new
                    {
                        select = new
                        {
                            name = "Revisión manual"
                        }
                    }
                });

            await _notion.AppendBlocksAsync(
                pageId,
                new List<object>
                {
                    Heading("Resultado IGDB"),
                    Paragraph($"No se encontró coincidencia para: {gameName}")
                });

            return;
        }
        
        var selected = SelectCandidate(
            candidates,
            gameName,
            ownedPlatforms);

        if(selected is null)
        {
            await _notion.UpdatePageAsync(
                pageId,
                new Dictionary<string, object>
                {
                    ["Estado"] = new
                    {
                        select = new
                        {
                            name = "Revisión manual"
                        }
                    }
                });

            await _notion.AppendBlocksAsync(
                pageId,
                new List<object>
                {
                    Heading("Coincidencias IGDB"),
                    Paragraph(
                        string.Join(
                            "\n",
                            candidates.Select(x =>
                            $"{GetString(x, "id")}: {GetString(x, "name")}")))
                });

            return;
        }

    if (!selected.Value.TryGetProperty("id", out var idProperty))
    {
        throw new InvalidOperationException(
            "El resultado seleccionado de IGDB no contiene id.");
    }

    var igdbId = idProperty.GetInt32();

        using var detailDocument = await _igdb.GetGameAsync(igdbId);

        if (!detailDocument.RootElement.EnumerateArray().Any())
        {
            throw new InvalidOperationException(
                "IGDB no devolvió los detalles del juego.");
        }

        var game = detailDocument.RootElement
        .EnumerateArray()
            .First();

        var summary = GetString(game, "summary");
        var releaseDate = GetDate(game, "first_release_date");
        var rating = GetDouble(game, "rating");
        var coverImageId = GetNestedString(game, "cover", "image_id");
        var coverUrl = CreateCoverUrl(coverImageId);
        var igdbUrl = GetString(game, "url");

        var genres = GetNestedNames(game, "genres");
        var igdbPlatforms = GetNestedNames(game, "platforms");
        var franchises = GetFranchiseNames(game);
        var developers = GetCompanies(game, developer: true);
        var publishers = GetCompanies(game, publisher: true);

        var pageProperties = new Dictionary<string, object>
        {
            ["Estado"] = Select("Completado"),
            ["IGDB ID"] = Number(igdbId),
            ["IGDB rating"] = Number(rating),
            ["IGDB URL"] = Url(igdbUrl),
            ["Franquicia"] = RichText(
                string.Join(", ", franchises)),
            ["Desarrolladores"] = MultiSelect(developers),
            ["Publishers"] = MultiSelect(publishers),
            ["Plataformas IGDB"] = MultiSelect(igdbPlatforms),
            ["Última sincronización"] = Date(
                DateTime.UtcNow.ToString("yyyy-MM-dd"))
        };

        if(releaseDate is not null)
        {
            pageProperties["Año"] = Date(releaseDate);
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            pageProperties["resumen"] = RichText(
                Truncate(summary, 1800));
        }

         if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            pageProperties["Portada"] = Files(coverUrl);
        }

        await _notion.UpdatePageAsync(pageId, pageProperties);

        var blocks = new List<object>
        {
            Heading("Información de IGDB"),
            Paragraph($"Nombre: {GetString(game, "name")}"),
            Paragraph($"Géneros: {string.Join(", ", genres)}"),
            Paragraph($"Plataformas: {string.Join(", ", igdbPlatforms)}"),
            Paragraph($"Desarrolladores: {string.Join(", ", developers)}"),
            Paragraph($"Publishers: {string.Join(", ", publishers)}"),
            Paragraph($"Franquicia: {string.Join(", ", franchises)}")
        };

        if (!string.IsNullOrWhiteSpace(summary))
        {
            blocks.Add(Heading("Resumen"));
            blocks.Add(Paragraph(Truncate(summary, 1800)));
        }

        var dlcs = GetNestedItems(game, "dlcs");
        var expansions = GetNestedItems(game, "expansions");
        var bundles = GetNestedItems(game, "bundles");

        if (dlcs.Count > 0)
        {
            blocks.Add(Heading("DLC"));
            foreach (var item in dlcs.Take(30))
            {
                blocks.Add(Bullet(
                    $"{GetString(item, "name")} — IGDB ID {GetString(item, "id")}"));
            }
        }

        if (expansions.Count > 0)
        {
            blocks.Add(Heading("Expansiones"));
            foreach (var item in expansions.Take(30))
            {
                blocks.Add(Bullet(
                    $"{GetString(item, "name")} — IGDB ID {GetString(item, "id")}"));
            }
        }

        if (bundles.Count > 0)
        {
            blocks.Add(Heading("Bundles"));
            foreach (var item in bundles.Take(30))
            {
                blocks.Add(Bullet(
                    $"{GetString(item, "name")} — IGDB ID {GetString(item, "id")}"));
            }
        }

        blocks.Add(Heading("Fuente"));
        blocks.Add(Paragraph(
            "Datos proporcionados por IGDB: https://www.igdb.com/"));

        await _notion.AppendBlocksAsync(pageId, blocks);

        _logger.LogInformation(
            "Página {PageId} procesada con IGDB {IgdbId}",
            pageId,
            igdbId);
    }

    private static JsonElement? SelectCandidate(
        List<JsonElement> candidates,
        string requestedName,
        List<string> requestedPlatforms)
    {
        var exact = candidates
            .Where(x =>
                string.Equals(
                    GetString(x, "name"),
                    requestedName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exact.Count == 1)
        {
            return exact[0];
        }

        var platformMatch = candidates
            .Where(x =>
                GetNestedNames(x, "platforms")
                    .Any(p => requestedPlatforms.Any(r =>
                        p.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                        r.Contains(p, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        return platformMatch.Count == 1
            ? platformMatch[0]
            : null;
    }

    private string GetTitle(JsonElement properties)
{
    foreach (var property in properties.EnumerateObject())
    {
        var value = property.Value;

        var type = value.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        _logger.LogInformation(
            "Propiedad recibida: {PropertyName}, tipo: {PropertyType}",
            property.Name,
            type);

        if (type != "title")
            continue;

        if (!value.TryGetProperty("title", out var title))
        {
            _logger.LogWarning(
                "La propiedad {PropertyName} no contiene title",
                property.Name);

            return string.Empty;
        }

        if (title.ValueKind != JsonValueKind.Array ||
            title.GetArrayLength() == 0)
        {
            _logger.LogWarning(
                "La propiedad de título {PropertyName} está vacía",
                property.Name);

            return string.Empty;
        }

        var text = title[0]
            .TryGetProperty("plain_text", out var plainText)
                ? plainText.GetString()
                : null;

        _logger.LogInformation(
            "Título obtenido: {Title}",
            text);

        return text?.Trim() ?? string.Empty;
    }

    _logger.LogWarning("No se encontró ninguna propiedad de tipo title");

    return string.Empty;
}

    private static List<string> GetMultiSelect(
        JsonElement properties,
        string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var property))
        {
            return [];
        }

        if (property.GetProperty("type").GetString() != "multi_select")
        {
            return [];
        }

        return property
            .GetProperty("multi_select")
            .EnumerateArray()
            .Select(x => GetString(x, "name"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList()!;
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString()
            : null;
    }

    private static string? GetNestedString(
        JsonElement element,
        string first,
        string second)
    {
        if (!element.TryGetProperty(first, out var nested))
        {
            return null;
        }

        return GetString(nested, second);
    }

    private static double? GetDouble(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static string? GetDate(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return DateTimeOffset
            .FromUnixTimeSeconds(value.GetInt64())
            .UtcDateTime
            .ToString("yyyy-MM-dd");
    }

    private static List<string> GetNestedNames(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Select(x => GetString(x, "name"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private static List<string> GetFranchiseNames(
        JsonElement game)
    {
        var names = new List<string>();

        if (game.TryGetProperty("franchise", out var franchise))
        {
            var name = GetString(franchise, "name");

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        names.AddRange(GetNestedNames(game, "franchises"));

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetCompanies(
        JsonElement game,
        bool developer = false,
        bool publisher = false)
    {
        if (!game.TryGetProperty(
                "involved_companies",
                out var companies) ||
            companies.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();

        foreach (var item in companies.EnumerateArray())
        {
            var matchesDeveloper =
                developer &&
                item.TryGetProperty("developer", out var d) &&
                d.GetBoolean();

            var matchesPublisher =
                publisher &&
                item.TryGetProperty("publisher", out var p) &&
                p.GetBoolean();

            if (!matchesDeveloper && !matchesPublisher)
            {
                continue;
            }

            if (item.TryGetProperty("company", out var company))
            {
                var name = GetString(company, "name");

                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(name);
                }
            }
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<JsonElement> GetNestedItems(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().ToList();
    }

    private static string CreateCoverUrl(string? imageId)
    {
        return string.IsNullOrWhiteSpace(imageId)
            ? string.Empty
            : $"https://images.igdb.com/igdb/image/upload/t_cover_big/{imageId}.jpg";
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max
            ? text
            : text[..max] + "...";
    }

    private static object Select(string name) =>
        new
        {
            select = new
            {
                name
            }
        };

    private static object Number(double? value) =>
        new
        {
            number = value
        };

    private static object Number(int value) =>
        new
        {
            number = value
        };

    private static object Url(string? url) =>
        new
        {
            url
        };

    private static object Date(string date) =>
        new
        {
            date = new
            {
                start = date
            }
        };

    private static object RichText(string text) =>
        new
        {
            rich_text = new[]
            {
                new
                {
                    type = "text",
                    text = new
                    {
                        content = text
                    }
                }
            }
        };

    private static object MultiSelect(List<string> values) =>
        new
        {
            multi_select = values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(100)
                .Select(x => new
                {
                    name = x
                })
                .ToArray()
        };

    private static object Files(string url) =>
        new
        {
            files = new[]
            {
                new
                {
                    name = "Portada IGDB",
                    external = new
                    {
                        url
                    }
                }
            }
        };

    private static object Heading(string text) =>
        new
        {
            @object = "block",
            type = "heading_2",
            heading_2 = new
            {
                rich_text = new[]
                {
                    new
                    {
                        type = "text",
                        text = new
                        {
                            content = text
                        }
                    }
                }
            }
        };

    private static object Paragraph(string text) =>
        new
        {
            @object = "block",
            type = "paragraph",
            paragraph = new
            {
                rich_text = new[]
                {
                    new
                    {
                        type = "text",
                        text = new
                        {
                            content = text
                        }
                    }
                }
            }
        };

    private static object Bullet(string text) =>
        new
        {
            @object = "block",
            type = "bulleted_list_item",
            bulleted_list_item = new
            {
                rich_text = new[]
                {
                    new
                    {
                        type = "text",
                        text = new
                        {
                            content = text
                        }
                    }
                }
            }
        };
}