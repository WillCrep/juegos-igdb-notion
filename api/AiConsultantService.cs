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
}
