using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
.ConfigureFunctionsWebApplication()
.ConfigureServices(services =>
{
    services.AddHttpClient<IgdbClient>();
    services.AddHttpClient<NotionClient>();
    services.AddHttpClient<AiConsultantService>();
    services.AddSingleton<GameEnrichmentService>();

    // Agregar Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
})
.Build();

host.Run();
