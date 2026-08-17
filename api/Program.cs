using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

var host = new HostBuilder()
.ConfigureFunctionsWebApplication()
.ConfigureServices(services =>
{
    services.Configure<JsonSerializerOptions>(options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
    services.AddHttpClient<IgdbClient>();
    services.AddHttpClient<NotionClient>();
    services.AddHttpClient<AiConsultantService>();
    services.AddHttpClient<TrophiesHunterClient>();
    services.AddSingleton<GameEnrichmentService>();
    services.AddSingleton<LibraryService>();
    services.AddSingleton<IgdbLibraryService>();
    services.AddSingleton<AchievementImportService>();

    // Agregar Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
})
.Build();

host.Run();
