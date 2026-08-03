using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class GameEnrichmentService
{
    private readonly IgdbClient _igdb;
    private readonly NotionClient _notion;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GameEnrichmentService> _logger;

    public GameEnrichmentService(
        IgdbClient igdb,
        NotionClient notion,
        IConfiguration configuration,
        ILogger<GameEnrichmentService> logger)
    {
        _igdb = igdb;
        _notion = notion;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ProcessPageAsync(
        string pageId,
        bool isEditedEvent = false)
    {
        using var page = await _notion.GetPageAsync(pageId);

        var properties = page.RootElement.GetProperty("properties");

_logger.LogWarning(
    "Claves del objeto properties: {Keys}",
    string.Join(
        ", ",
        properties.EnumerateObject().Select(p => p.Name)));


        var gameName = GetTitle(properties);

        var currentState = GetSelectName(properties, "Estado");
        var manualIgdbId = GetNumber(properties, "IGDB ID");
        var isManualRetry =
            string.Equals(
                currentState,
                "Revisi\u00f3n manual",
                StringComparison.OrdinalIgnoreCase) &&
            manualIgdbId.HasValue;

        _logger.LogInformation(
            "P\u00e1gina {PageId}: estado={State}, IGDB ID={IgdbId}, evento editado={IsEdited}",
            pageId,
            currentState ?? "(vac\u00edo)",
            manualIgdbId?.ToString() ?? "(vac\u00edo)",
            isEditedEvent);

        // Las ediciones normales provocadas por el propio servicio se ignoran.
        // Solo una ediciÃ³n manual con estado RevisiÃ³n manual e IGDB ID
        // debe volver a procesarse.
        if (isEditedEvent && !isManualRetry)
        {
            _logger.LogInformation(
                "EdiciÃ³n ignorada para {PageId}: no es una selecciÃ³n manual pendiente.",
                pageId);
            return;
        }

_logger.LogWarning(
    "Título extraído: '{GameName}'",
    gameName);

        if (string.IsNullOrWhiteSpace(gameName))
        {
            throw new InvalidOperationException(
                "La página no tiene un  válido"
            );
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
        
        using var search = isManualRetry
            ? JsonDocument.Parse(
                $"[{{\"id\":{manualIgdbId!.Value},\"name\":{JsonSerializer.Serialize(gameName)}}}]")
            : await _igdb.SearchGamesAsync(gameName);

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
        
        var selected = SelectCandidate(candidates, gameName);

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
            ["Franquicia"] = MultiSelect(franchises),
            ["Desarrolladores"] = MultiSelect(developers),
            ["Publishers"] = MultiSelect(publishers),
            ["Género"] = MultiSelect(genres),
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
            pageProperties["Resumen"] = RichText(
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
            Paragraph($": {GetString(game, "name")}"),
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
        var standaloneExpansions = GetNestedItems(game, "standalone_expansions");
        var bundles = GetNestedItems(game, "bundles");

        await SyncDlcDatabaseAsync(
            pageId,
            dlcs,
            expansions,
            standaloneExpansions,
            bundles);

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

        if (standaloneExpansions.Count > 0)
        {
            blocks.Add(Heading("Expansiones independientes"));
            foreach (var item in standaloneExpansions.Take(30))
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

        // El contenido de la página es una ficha visual. Los datos
        // estructurados permanecen en las propiedades y los DLC en su base.
        blocks = BuildGameContentBlocks(game, summary, coverUrl);

        await _notion.AppendBlocksAsync(pageId, blocks);

        _logger.LogInformation(
            "Página {PageId} procesada con IGDB {IgdbId}",
            pageId,
            igdbId);
    }

    private static List<object> BuildGameContentBlocks(
        JsonElement game,
        string? summary,
        string coverUrl)
    {
        var blocks = new List<object>
        {
            Heading("Ficha del juego"),
            Paragraph(GetString(game, "name") ?? "")
        };

        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            blocks.Add(CenteredImage(coverUrl));
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            blocks.Add(Heading("Resumen"));
            blocks.Add(Paragraph(Truncate(summary, 1800)));
        }

        var images = GetImageUrls(game, "screenshots", "t_1080p")
            .Concat(GetImageUrls(game, "artworks", "t_1080p"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        if (images.Count > 0)
        {
            blocks.Add(Heading("ImÃ¡genes"));
            foreach (var image in images)
            {
                blocks.Add(Image(image, "Imagen del juego"));
            }
        }

        var videos = GetNestedItems(game, "videos")
            .Select(video => new
            {
                Url = GetString(video, "video_id") is { } id
                    ? $"https://www.youtube.com/watch?v={id}"
                    : null,
                Name = GetString(video, "name")
            })
            .Where(video => !string.IsNullOrWhiteSpace(video.Url))
            .Take(2)
            .ToList();

        if (videos.Count > 0)
        {
            blocks.Add(Heading("Videos"));
            foreach (var video in videos)
            {
                blocks.Add(Video(video.Url!, video.Name ?? "Video de IGDB"));
            }
        }

        var releaseRows = GetNestedItems(game, "release_dates")
            .Select(item => new[]
            {
                GetNestedString(item, "platform", "name") ?? "Sin plataforma",
                GetString(item, "human") ?? GetDate(item, "date") ?? "Sin fecha",
                GetNestedString(item, "release_region", "region") ?? ""
            })
            .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
            .DistinctBy(row => string.Join("|", row), StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row[0])
            .Take(100)
            .ToList();

        if (releaseRows.Count > 0)
        {
            blocks.Add(Heading("Fechas de lanzamiento por plataforma"));
            blocks.Add(Table(
                new[] { "Plataforma", "Fecha", "RegiÃ³n" },
                releaseRows));
        }

        var ageRows = GetNestedItems(game, "age_ratings")
            .Select(item => new[]
            {
                GetNestedString(item, "organization", "name") ?? "Sin organizaciÃ³n",
                GetNestedString(item, "rating_category", "rating") ?? "Sin clasificaciÃ³n",
                GetString(item, "synopsis") ?? ""
            })
            .DistinctBy(row => string.Join("|", row), StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        /*
        if (false)
        {
            blocks.Add(Heading("ClasificaciÃ³n por edades"));
            blocks.Add(Table(
                new[] { "OrganizaciÃ³n", "ClasificaciÃ³n", "DescripciÃ³n" },
                ageRows));
        }

        */
        var languageRows = GetNestedItems(game, "language_supports")
            .Select(item => new[]
            {
                GetNestedString(item, "language", "name") ?? "Sin idioma",
                GetNestedString(item, "language_support_type", "name") ?? ""
            })
            .DistinctBy(row => string.Join("|", row), StringComparer.OrdinalIgnoreCase)
            .OrderBy(row => row[0])
            .Take(100)
            .ToList();

        /*
        if (false)
        {
            blocks.Add(Heading("Idiomas soportados"));
            blocks.Add(Table(
                new[] { "Idioma", "Tipo de soporte" },
                languageRows));
        }

        */
        if (ageRows.Count > 0 || languageRows.Count > 0)
        {
            blocks.Add(TwoColumnTables(ageRows, languageRows));
        }

        blocks.Add(Heading("Fuente"));
        blocks.Add(Paragraph("Datos proporcionados por IGDB: https://www.igdb.com/"));

        return blocks;
    }

    private static List<string> GetImageUrls(
        JsonElement game,
        string property,
        string size)
    {
        return GetNestedItems(game, property)
            .Select(item => GetString(item, "image_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => $"https://images.igdb.com/igdb/image/upload/{size}/{id}.jpg")
            .ToList()!;
    }

    private async Task SyncDlcDatabaseAsync(
        string baseGamePageId,
        List<JsonElement> dlcs,
        List<JsonElement> expansions,
        List<JsonElement> standaloneExpansions,
        List<JsonElement> bundles)
    {
        var dataSourceId = _configuration["NOTION_DLC_DATA_SOURCE_ID"];

        if (string.IsNullOrWhiteSpace(dataSourceId))
        {
            var databaseId = _configuration["NOTION_DLC_DATABASE_ID"];

            if (!string.IsNullOrWhiteSpace(databaseId))
            {
                dataSourceId = await _notion.GetDataSourceIdAsync(databaseId);
            }
        }

        if (string.IsNullOrWhiteSpace(dataSourceId))
        {
            _logger.LogWarning(
                "NOTION_DLC_DATABASE_ID o NOTION_DLC_DATA_SOURCE_ID " +
                "no está configurado. " +
                "Se omitirá la sincronización de DLC.");

            return;
        }

        var groups = new[]
        {
            (Type: "DLC", Items: dlcs),
            (Type: "Expansión", Items: expansions),
            (Type: "Expansión independiente", Items: standaloneExpansions),
            (Type: "Bundle", Items: bundles)
        };

        foreach (var group in groups)
        {
            foreach (var item in group.Items)
            {
                if (!item.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.Number ||
                    !idProperty.TryGetInt32(out var igdbId))
                {
                    continue;
                }

                var name = GetString(item, "name")?.Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var releaseDate = GetDate(item, "first_release_date");
                var platforms = GetNestedNames(item, "platforms");
                var url = GetString(item, "url");

                var automaticProperties = new Dictionary<string, object>
                {
                    ["Nombre"] = Title(name),
                    ["Juego base"] = Relation(baseGamePageId),
                    ["IGDB ID"] = Number(igdbId),
                    ["Tipo"] = Select(group.Type),
                    ["Fecha de lanzamiento"] = releaseDate is null
                        ? new { date = (object?)null }
                        : Date(releaseDate),
                    ["Plataformas"] = MultiSelect(platforms),
                    ["IGDB URL"] = Url(url)
                };

                var existingPageId = await _notion.FindPageByNumberAsync(
                    dataSourceId,
                    "IGDB ID",
                    igdbId);

                if (existingPageId is null)
                {
                    var createProperties = new Dictionary<string, object>(
                        automaticProperties)
                    {
                        ["Notas"] = RichText(
                            "Creado automáticamente desde IGDB.")
                    };

                    await _notion.CreatePageAsync(
                        dataSourceId,
                        createProperties);

                    _logger.LogInformation(
                        "DLC creado en Notion: {Name} ({IgdbId})",
                        name,
                        igdbId);
                }
                else
                {
                    await _notion.UpdatePageAsync(
                        existingPageId,
                        automaticProperties);

                    _logger.LogInformation(
                        "DLC actualizado en Notion: {Name} ({IgdbId})",
                        name,
                        igdbId);
                }
            }
        }
    }

    private static JsonElement? SelectCandidate(
        List<JsonElement> candidates,
        string requestedName)
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

        // La plataforma poseída se gestiona posteriormente en Mi colección.
        return exact.Count == 0 && candidates.Count == 1
            ? candidates[0]
            : null;
    }

    private static string GetTitle(JsonElement properties)
    {
        foreach (var property in properties.EnumerateObject())
        {
            var value = property.Value;

            if (value.GetProperty("type").GetString() != "title")
            {
                continue;
            }

            var title = value.GetProperty("title");

            if (title.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            return title[0]
                .GetProperty("plain_text")
                .GetString()
                ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? GetSelectName(
        JsonElement properties,
        string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var property) ||
            property.GetProperty("type").GetString() != "select")
        {
            return null;
        }

        var select = property.GetProperty("select");

        return select.ValueKind == JsonValueKind.Null
            ? null
            : GetString(select, "name");
    }

    private static int? GetNumber(
        JsonElement properties,
        string propertyName)
    {
        if (!properties.TryGetProperty(propertyName, out var property) ||
            property.GetProperty("type").GetString() != "number")
        {
            return null;
        }

        var number = property.GetProperty("number");

        return number.ValueKind == JsonValueKind.Number &&
               number.TryGetInt32(out var value)
            ? value
            : null;
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

    private static object Title(string text) =>
        new
        {
            title = new[]
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

    private static object Relation(string pageId) =>
        new
        {
            relation = new[]
            {
                new
                {
                    id = pageId
                }
            }
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
                            content = NormalizeText(text)
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
                            content = NormalizeText(text)
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
                            content = NormalizeText(text)
                        }
                    }
                }
            }
        };

    private static object Image(string url, string caption) =>
        new
        {
            @object = "block",
            type = "image",
            image = new
            {
                type = "external",
                external = new
                {
                    url
                },
                caption = Caption(caption)
            }
        };

    private static object Video(string url, string caption) =>
        new
        {
            @object = "block",
            type = "video",
            video = new
            {
                type = "external",
                external = new
                {
                    url
                },
                caption = Caption(caption)
            }
        };

    private static object CenteredImage(string url) =>
        ColumnList(
            Column(Paragraph(" ")),
            Column(Image(url, "Portada")),
            Column(Paragraph(" ")));

    private static object TwoColumnTables(
        List<string[]> ageRows,
        List<string[]> languageRows)
    {
        var ageContent = ageRows.Count > 0
            ? new object[]
            {
                Heading("Clasificaci\u00f3n por edades"),
                Table(
                    new[] { "Organizaci\u00f3n", "Clasificaci\u00f3n", "Descripci\u00f3n" },
                    ageRows)
            }
            : new object[] { Paragraph("Sin datos de clasificaci\u00f3n") };

        var languageContent = languageRows.Count > 0
            ? new object[]
            {
                Heading("Idiomas soportados"),
                Table(
                    new[] { "Idioma", "Tipo de soporte" },
                    languageRows)
            }
            : new object[] { Paragraph("Sin datos de idiomas") };

        return ColumnList(
            Column(ageContent),
            Column(languageContent));
    }

    private static object ColumnList(params object[] columns) =>
        new
        {
            @object = "block",
            type = "column_list",
            column_list = new
            {
                children = columns
            }
        };

    private static object Column(params object[] children) =>
        new
        {
            @object = "block",
            type = "column",
            column = new
            {
                children
            }
        };

    private static object Table(
        string[] headers,
        List<string[]> rows)
    {
        var allRows = new List<string[]> { headers };
        allRows.AddRange(rows);

        return new
        {
            @object = "block",
            type = "table",
            table = new
            {
                table_width = headers.Length,
                has_column_header = true,
                has_row_header = false,
                children = allRows
                    .Select(TableRow)
                    .ToArray()
            }
        };
    }

    private static object TableRow(string[] values) =>
        new
        {
            @object = "block",
            type = "table_row",
            table_row = new
            {
                cells = values
                    .Select(value => new[]
                    {
                        new
                        {
                            type = "text",
                            text = new
                            {
                                content = NormalizeText(Truncate(value ?? "", 1900))
                            }
                        }
                    })
                    .ToArray()
            }
        };

    private static string NormalizeText(string text)
    {
        return text
            .Replace("ÃƒÂ¡", "\u00e1")
            .Replace("ÃƒÂ©", "\u00e9")
            .Replace("ÃƒÂ­", "\u00ed")
            .Replace("ÃƒÂ³", "\u00f3")
            .Replace("ÃƒÂº", "\u00fa")
            .Replace("ÃƒÂ±", "\u00f1")
            .Replace("ÃƒÂ¼", "\u00fc")
            .Replace("Ã¡", "\u00e1")
            .Replace("Ã©", "\u00e9")
            .Replace("Ã­", "\u00ed")
            .Replace("Ã³", "\u00f3")
            .Replace("Ãº", "\u00fa")
            .Replace("Ã±", "\u00f1")
            .Replace("Ã¼", "\u00fc")
            .Replace("â€”", "\u2014");
    }

    private static object[] Caption(string text) =>
        new object[]
        {
            new
            {
                type = "text",
                text = new
                {
                    content = NormalizeText(text)
                }
            }
        };
}
