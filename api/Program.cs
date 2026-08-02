using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
.ConfigureFunctionsWorkerDefaults()
.ConfigureServices(services =>
{
    services.AddHttpClient<IgdbClient>();
    services.AddHttpClient<NotionClient>();
    services.AddSingleton<GameEnrichmentService>();
})
.Build();

host.Run();