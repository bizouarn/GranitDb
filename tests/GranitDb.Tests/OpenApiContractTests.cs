using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace GranitDb.Tests;

/// <summary>
///     Contrat de l'API : la description OpenAPI engendrée par le serveur doit être celle qui est enregistrée dans
///     <c>client/api/openapi.json</c>, d'où est généré le client typé (<c>scripts/generate-client.ps1</c>). Si l'API change
///     sans que le fichier soit régénéré, ce test échoue : le client ne peut plus dériver sans que ça se voie.
/// </summary>
public sealed class OpenApiContractTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;

    public OpenApiContractTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private static string SpecPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GranitDb.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("GranitDb.sln introuvable"), "client", "api", "openapi.json");
    }

    private async Task<string> CurrentSpecAsync()
    {
        _ = _factory.CreateKestrelClient(); // démarre l'API
        var document = _factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        return json.Replace("\r\n", "\n").TrimEnd() + "\n";
    }

    [Fact]
    public async Task The_committed_openapi_file_matches_the_api()
    {
        var current = await CurrentSpecAsync();
        var path = SpecPath();
        if (Environment.GetEnvironmentVariable("GRANITDB_UPDATE_OPENAPI") == "1")
        {
            File.WriteAllText(path, current);
            return;
        }

        Assert.True(File.Exists(path), "client/api/openapi.json manque : lancez scripts/generate-client.ps1.");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() + "\n";
        Assert.True(committed == current,
            "L'API ne correspond plus à client/api/openapi.json : lancez scripts/generate-client.ps1 puis validez le fichier et le client régénérés.");
    }

    [Fact]
    public async Task The_contract_only_describes_the_versioned_routes_and_gives_every_operation_a_unique_name()
    {
        var spec = await CurrentSpecAsync();
        using var json = System.Text.Json.JsonDocument.Parse(spec);
        var ids = new List<string>();
        foreach (var path in json.RootElement.GetProperty("paths").EnumerateObject())
        {
            Assert.StartsWith("/api/v1/", path.Name);
            foreach (var operation in path.Value.EnumerateObject())
                ids.Add(operation.Value.GetProperty("operationId").GetString()!);
        }

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Every_read_operation_declares_what_it_returns_except_the_file_exports()
    {
        using var json = System.Text.Json.JsonDocument.Parse(await CurrentSpecAsync());
        var untyped = new List<string>();
        foreach (var path in json.RootElement.GetProperty("paths").EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject().Where(o => o.Name == "get"))
        {
            var id = operation.Value.GetProperty("operationId").GetString()!;
            if (id.Contains("_Export")) continue;
            var ok = operation.Value.GetProperty("responses").GetProperty("200");
            if (!ok.TryGetProperty("content", out _)) untyped.Add(id);
        }

        Assert.True(untyped.Count == 0, "Réponse non déclarée (ResponseTypesFilter) : " + string.Join(", ", untyped));
    }
}
