using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using JsonProperty = Newtonsoft.Json.JsonPropertyAttribute;

public sealed record AiChatRequest(string Question, string Mode = "general", string? PromptId = null);
public sealed class PromptProfile
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
    [JsonProperty("instructions")]
    public string Instructions { get; set; } = string.Empty;
    [JsonProperty("isBuiltIn")]
    public bool IsBuiltIn { get; set; }
    // Compatible con contenedores existentes cuya partition key es /partitionKey.
    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "prompts";
    [JsonProperty("documentType")]
    public string DocumentType { get; set; } = "aiPrompt";

    public PromptProfile() { }

    public PromptProfile(string id, string name, string description, string instructions, bool isBuiltIn = false)
    {
        Id = id;
        Name = name;
        Description = description;
        Instructions = instructions;
        IsBuiltIn = isBuiltIn;
    }
}

public sealed class AiConsultantService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient http;
    private readonly CosmosClient? cosmos;
    private readonly string? groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    private readonly string model = Environment.GetEnvironmentVariable("GROQ_MODEL") ?? "llama-3.1-8b-instant";
    private readonly string databaseId = Environment.GetEnvironmentVariable("COSMOS_DATABASE_ID") ?? "AiConsultant";
    private readonly string containerId = Environment.GetEnvironmentVariable("COSMOS_CONTAINER_ID") ?? "PromptProfiles";

    public AiConsultantService(HttpClient http)
    {
        this.http = http;
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("COSMOS_KEY");
        if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(key))
            cosmos = new CosmosClient(endpoint, key);
    }

    public IReadOnlyList<PromptProfile> BuiltInProfiles => new[]
    {
        new PromptProfile("general", "Consultor general", "Respuestas claras para cualquier tema.", "Responde en español claro y útil. Si el tema requiere datos actuales, indica qué parte debe verificarse. No inventes fuentes ni cifras.", true),
        new PromptProfile("games", "Guías de videojuegos", "Ayuda para avanzar, builds, secretos y decisiones.", "Eres un experto en videojuegos. Da pasos concretos, menciona la plataforma o versión cuando cambie la respuesta y separa spoilers con una advertencia. No inventes nombres de misiones, objetos ni ubicaciones.", true),
        new PromptProfile("achievements", "Logros y trofeos", "Logros, trofeos y 100% en cualquier plataforma.", "Eres especialista en logros y trofeos. Identifica el juego, plataforma y edición; lista requisitos, condiciones perdibles, cooperativo, dificultad y consejos. Aclara cuando un logro dependa de DLC o de una versión concreta. No inventes requisitos.", true)
    };

    public async Task<IReadOnlyList<PromptProfile>> GetProfilesAsync()
    {
        var profiles = BuiltInProfiles.ToList();
        var container = await GetContainerAsync(false);
        if (container is null) return profiles;

        var query = new QueryDefinition("SELECT * FROM c WHERE c.documentType = @documentType")
            .WithParameter("@documentType", "aiPrompt");
        using var iterator = container.GetItemQueryIterator<PromptProfile>(query);
        while (iterator.HasMoreResults)
            profiles.AddRange(await iterator.ReadNextAsync());
        return profiles;
    }

    public async Task<PromptProfile> SaveProfileAsync(PromptProfile profile)
    {
        var container = await GetContainerAsync(true) ?? throw new InvalidOperationException("Cosmos DB no está configurado.");
        var clean = new PromptProfile(profile.Id.Trim(), profile.Name.Trim(), profile.Description?.Trim() ?? string.Empty, profile.Instructions.Trim())
        {
            PartitionKey = "prompts",
            DocumentType = "aiPrompt"
        };
        await container.UpsertItemAsync(clean, new PartitionKey(await GetPartitionValueAsync(container, clean.Id, clean.PartitionKey)));
        return clean;
    }

    public async Task DeleteProfileAsync(string id)
    {
        var container = await GetContainerAsync(false) ?? throw new InvalidOperationException("Cosmos DB no está configurado.");
        await container.DeleteItemAsync<PromptProfile>(id, new PartitionKey(await GetPartitionValueAsync(container, id, "prompts")));
    }

    public async Task<string> AskAsync(AiChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(groqKey)) throw new InvalidOperationException("GROQ_API_KEY no está configurado.");
        var question = request.Question.Trim();
        if (question.Length is < 2 or > 4000) throw new ArgumentException("La consulta debe tener entre 2 y 4000 caracteres.");

        var profiles = await GetProfilesAsync();
        var selected = profiles.FirstOrDefault(p => p.Id.Equals(request.PromptId, StringComparison.OrdinalIgnoreCase));
        selected ??= profiles.FirstOrDefault(p => p.Id.Equals(request.Mode, StringComparison.OrdinalIgnoreCase)) ?? BuiltInProfiles[0];

        var payload = new
        {
            model,
            temperature = 0.35,
            max_tokens = 1200,
            messages = new[]
            {
                new { role = "system", content = selected.Instructions },
                new { role = "user", content = question }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", groqKey);
        message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq devolvió {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "No se recibió una respuesta.";
    }

    private async Task<Container?> GetContainerAsync(bool create)
    {
        if (cosmos is null) return null;
        var database = await cosmos.CreateDatabaseIfNotExistsAsync(databaseId);
        var existing = database.Database.GetContainer(containerId);
        try
        {
            await existing.ReadContainerAsync();
            return existing;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && create)
        {
            var partitionPath = Environment.GetEnvironmentVariable("COSMOS_PARTITION_KEY_PATH") ?? "/id";
            var created = await database.Database.CreateContainerIfNotExistsAsync(containerId, partitionPath);
            return created.Container;
        }
    }

    private static async Task<string> GetPartitionValueAsync(Container container, string id, string fallback)
    {
        var metadata = await container.ReadContainerAsync();
        return metadata.Resource.PartitionKeyPath.EndsWith("/id", StringComparison.OrdinalIgnoreCase) ? id : fallback;
    }

public async Task<List<AchievementJsonItem>> TranslateAchievementsBatchAsync(List<AchievementJsonItem> items)
    {
        if (string.IsNullOrWhiteSpace(groqKey))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR GROQ]: GROQ_API_KEY no está configurada en local.settings.json.");
            Console.ResetColor();
            throw new InvalidOperationException("GROQ_API_KEY no está configurada en las variables de entorno.");
        }

        if (items.Count == 0) return items;

        const int batchSize = 10;
        var chunks = items.Chunk(batchSize).ToList();
        var allTranslated = new List<AchievementJsonItem>();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[IA TRADUCCIÓN]: Iniciando traducción de {items.Count} logros en {chunks.Count} lotes seguros...");
        Console.ResetColor();

        for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
        {
            var chunk = chunks[chunkIndex].ToList();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[IA LOTE {chunkIndex + 1}/{chunks.Count}]: Traduciendo {chunk.Count} logros...");
            Console.ResetColor();

            var translatedChunk = await TranslateSingleChunkWithRetryAsync(chunk, chunkIndex + 1, chunks.Count);
            allTranslated.AddRange(translatedChunk);

            if (chunkIndex < chunks.Count - 1)
            {
                await Task.Delay(400); // Pausa preventiva entre llamadas
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[IA TRADUCCIÓN ÉXITO TOTAL]: Se tradujeron {allTranslated.Count} de {items.Count} logros al español.");
        Console.ResetColor();

        return allTranslated;
    }

     private async Task<List<AchievementJsonItem>> TranslateSingleChunkWithRetryAsync(List<AchievementJsonItem> items, int currentChunk, int totalChunks)
    {
        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await ExecuteChunkTranslationAsync(items, currentChunk);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("429") || ex.Message.Contains("rate_limit_exceeded"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"try again in (\d+(?:\.\d+)?)s");
                double waitSeconds = match.Success ? double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 2.0 : 15.0;

                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[IA EN ESPERA LOTE {currentChunk}]: Límite alcanzado. Esperando {waitSeconds:F1}s para reintentar (Intento {attempt}/{maxRetries})...");
                Console.ResetColor();

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR EN LOTE {currentChunk}]: {ex.Message}");
                Console.ResetColor();
                if (attempt == maxRetries) return items;
                await Task.Delay(1000);
            }
        }

        return items;
    }

      private async Task<List<AchievementJsonItem>> ExecuteChunkTranslationAsync(List<AchievementJsonItem> items, int chunkNumber)
    {
        var payloadToTranslate = items.Select((item, idx) => new
        {
            i = idx,
            n = item.Name,
            d = item.Description ?? string.Empty
        }).ToList();

        var systemPrompt = "Eres un traductor profesional de videojuegos. Traduce al español neutro los campos 'n' (nombre del logro) y 'd' (descripción del logro). Mantén nombres propios. Responde EXCLUSIVAMENTE con un objeto JSON con una propiedad 't' que contenga el array: {\"t\": [{\"i\": 0, \"n\": \"Nombre traducido\", \"d\": \"Descripción traducida\"}]}.";

        var groqPayload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "openai/gpt-oss-20b" : model,
            temperature = 0.1,
            max_tokens = 1200,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(new { list = payloadToTranslate }, JsonOptions) }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", groqKey);
        message.Content = new StringContent(JsonSerializer.Serialize(groqPayload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} - {body}");
        }

        using var jsonDoc = JsonDocument.Parse(body);
        var content = jsonDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(content)) return items;

        using var translatedDoc = JsonDocument.Parse(content);
        var root = translatedDoc.RootElement;

        // BÚSQUEDA UNIVERSAL DEL ARREGLO JSON (Sin importar el nombre de la propiedad)
        JsonElement array = default;
        bool foundArray = false;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            foundArray = true;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    array = prop.Value;
                    foundArray = true;
                    break;
                }
            }
        }

        if (!foundArray)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[AVISO LOTE {chunkNumber}]: No se encontró array en la respuesta. Se mantiene original.");
            Console.ResetColor();
            return items;
        }

        var result = new List<AchievementJsonItem>(items);
        int successInChunk = 0;

        foreach (var elem in array.EnumerateArray())
        {
            int? idx = null;
            if (elem.TryGetProperty("i", out var iProp) && iProp.TryGetInt32(out var iVal))
                idx = iVal;
            else if (elem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idVal))
                idx = idVal;

            if (idx.HasValue && idx.Value >= 0 && idx.Value < result.Count)
            {
                var translatedName = elem.TryGetProperty("n", out var nProp) ? nProp.GetString() : (elem.TryGetProperty("name", out var np) ? np.GetString() : null);
                var translatedDesc = elem.TryGetProperty("d", out var dProp) ? dProp.GetString() : (elem.TryGetProperty("description", out var dp) ? dp.GetString() : null);
                var current = result[idx.Value];

                result[idx.Value] = current with
                {
                    Name = !string.IsNullOrWhiteSpace(translatedName) ? translatedName : current.Name,
                    Description = !string.IsNullOrWhiteSpace(translatedDesc) ? translatedDesc : current.Description
                };
                successInChunk++;
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"[LOTE {chunkNumber} OK]: {successInChunk} logros procesados correctamente.");
        Console.ResetColor();

        return result;
    }

    private async Task<List<AchievementJsonItem>> TranslateSingleChunkAsync(List<AchievementJsonItem> items)
    {
        var payloadToTranslate = items.Select((item, idx) => new
        {
            id = idx,
            name = item.Name,
            description = item.Description ?? string.Empty
        }).ToList();

        var systemPrompt = "Eres un traductor profesional de videojuegos. Traduce al español neutro los campos 'name' y 'description'. Mantén nombres propios. Responde EXCLUSIVAMENTE con un JSON con la propiedad 'translated': {\"translated\": [{\"id\": 0, \"name\": \"Título traducido\", \"description\": \"Descripción traducida\"}]}.";

        var groqPayload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "openai/gpt-oss-20b" : model,
            temperature = 0.1,
            max_tokens = 1800,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(new { list = payloadToTranslate }, JsonOptions) }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", groqKey);
        message.Content = new StringContent(JsonSerializer.Serialize(groqPayload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR GROQ LOTE {(int)response.StatusCode}]: {body}");
            Console.ResetColor();
            throw new InvalidOperationException($"Groq devolvió error {(int)response.StatusCode}: {body}");
        }

        using var jsonDoc = JsonDocument.Parse(body);
        var content = jsonDoc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(content)) return items;

        using var translatedDoc = JsonDocument.Parse(content);
        var root = translatedDoc.RootElement;

        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
            array = root;
        else if (root.TryGetProperty("translated", out var tProp) && tProp.ValueKind == JsonValueKind.Array)
            array = tProp;
        else if (root.TryGetProperty("list", out var lProp) && lProp.ValueKind == JsonValueKind.Array)
            array = lProp;
        else
            array = root.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;

        var result = new List<AchievementJsonItem>(items);

        foreach (var elem in array.EnumerateArray())
        {
            int? idx = null;
            if (elem.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idVal))
                idx = idVal;
            else if (elem.TryGetProperty("i", out var iProp) && iProp.TryGetInt32(out var iVal))
                idx = iVal;

            if (idx.HasValue && idx.Value >= 0 && idx.Value < result.Count)
            {
                var translatedName = elem.TryGetProperty("name", out var nProp) ? nProp.GetString() : (elem.TryGetProperty("n", out var np) ? np.GetString() : null);
                var translatedDesc = elem.TryGetProperty("description", out var dProp) ? dProp.GetString() : (elem.TryGetProperty("d", out var dp) ? dp.GetString() : null);
                var current = result[idx.Value];

                result[idx.Value] = current with
                {
                    Name = !string.IsNullOrWhiteSpace(translatedName) ? translatedName : current.Name,
                    Description = !string.IsNullOrWhiteSpace(translatedDesc) ? translatedDesc : current.Description
                };
            }
        }

        return result;
    }

}
