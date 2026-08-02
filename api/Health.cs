using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class Health
{
    [Function("Health")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "health")]
        HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);

        await response.WriteAsJsonAsync(new
        {
            status = "ok",
            service = "juegos-igdb"
        });

        return response;
    }
}