using GranitDb.Client;
using GranitDb.Client.api;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddMudServices();
builder.Services.AddGranitWorkspace();

// URL de l'API : wwwroot/appsettings.json (ApiBaseUrl).
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7112/";
// La session est un cookie émis par l'API (autre origine possible) : quand l'authentification est activée, les appels
// l'envoient avec les identifiants ; chaque écriture porte l'en-tête que l'API exige alors.
builder.Services.AddScoped(_ =>
{
    var session = new SessionHandler(new HttpClientHandler());
    return new Client(new HttpClient(session)
    {
        BaseAddress = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/"),
        // Large : l'envoi d'un gros fichier de base prend du temps.
        Timeout = TimeSpan.FromMinutes(30)
    }, session);
});

await builder.Build().RunAsync();
