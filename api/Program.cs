using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;

var host = new HostBuilder()
.ConfigureFunctionsWebApplication()
.ConfigureServices(services =>
{
    services.Configure<JsonSerializerOptions>(options =>
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
    });
    services.AddSingleton<ArcadiaDatabase>();
    services.AddHttpClient("igdb");
    services.AddHttpClient("groq");
    services.AddSingleton<ArcadiaExternalServices>();
})
.Build();

host.Run();
