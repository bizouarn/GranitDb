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
builder.Services.AddScoped(_ => new Client(new HttpClient
{
    BaseAddress = new Uri(apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/"),
    Timeout = TimeSpan.FromMinutes(5)
}));

await builder.Build().RunAsync();
