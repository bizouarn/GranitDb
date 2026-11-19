using GranitDb.Client;
using GranitDb.Client.api;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddGranitWorkspace();

// URL de l'API : wwwroot/appsettings.json (ApiBaseUrl).
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7112/";
// La session est un cookie émis par l'API (autre origine possible) : les appels l'envoient avec les identifiants, et chaque
// écriture porte l'en-tête que l'API exige quand l'authentification est activée.
builder.Services.AddScoped(_ => new Client(new HttpClient(new SessionHandler())
{
    BaseAddress = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/"),
    Timeout = TimeSpan.FromMinutes(5)
}));

await builder.Build().RunAsync();

/// <summary>Envoie le cookie de session avec chaque appel et marque les écritures comme venant de l'interface.</summary>
internal sealed class SessionHandler : DelegatingHandler
{
    public SessionHandler() : base(new HttpClientHandler())
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            request.Headers.TryAddWithoutValidation("X-GranitDb-Client", "1");
        return base.SendAsync(request, cancellationToken);
    }
}
