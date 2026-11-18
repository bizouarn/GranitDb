using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GranitDb.Tests;

/// <summary>
///     Démarre l'API sur un vrai serveur Kestrel (port libre sur 127.0.0.1) plutôt que sur le TestServer en mémoire :
///     le TestServer de .NET 8 est incompatible avec un runtime plus récent (PipeWriter.UnflushedBytes), et un vrai
///     serveur teste aussi le multipart et le pipeline HTTP réels.
/// </summary>
public sealed class KestrelApiFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
{
    private IHost? _kestrelHost;

    public string BaseUrl { get; private set; } = "";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        // Pas d'authentification dans l'API : un test peut simuler un appelant identifié avec l'en-tête X-Test-User.
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestActorFilter>());
    }

    private sealed class TestActorFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers.TryGetValue("X-Test-User", out var user) && user.Count > 0)
                        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.ToString())], "test"));
                    await continuation();
                });
                next(app);
            };
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Hôte factice (TestServer) exigé par WebApplicationFactory, puis hôte réel sur Kestrel.
        var dummy = builder.Build();
        builder.ConfigureWebHost(web => web.UseKestrel(o => o.Listen(IPAddress.Loopback, 0)));
        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        var addresses = _kestrelHost.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses;
        BaseUrl = addresses.First();
        return dummy;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.StopAsync().GetAwaiter().GetResult();
            _kestrelHost?.Dispose();
        }

        base.Dispose(disposing);
    }

    public HttpClient CreateKestrelClient()
    {
        _ = Server; // force la création de l'hôte
        return new HttpClient { BaseAddress = new Uri(BaseUrl) };
    }
}
