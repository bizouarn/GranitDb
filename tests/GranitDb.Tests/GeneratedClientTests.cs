using GranitDb.Client.Generated;
using GranitDb.Client.Models;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Le client typé généré depuis OpenAPI parle vraiment à l'API : routes, types d'entrée et types de réponse.</summary>
public sealed class GeneratedClientTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _http;
    private readonly GranitApi _api;

    public GeneratedClientTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
        });
        _http = _factory.CreateKestrelClient();
        _api = new GranitApi(_http);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    [Fact]
    public async Task The_generated_client_uploads_a_database_then_reads_it_back_with_typed_results()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT); CREATE TABLE b (id INTEGER PRIMARY KEY, a_id INTEGER REFERENCES a(id));");

        await using var file = File.OpenRead(path);
        var imported = await _api.SqliteImport.UploadAsync(new FileParameter(file, "demo.db", "application/octet-stream"), "Demo", "Une base de démonstration", true);

        Assert.Equal("Demo", imported.Database.Name);
        Assert.Equal(2, imported.Sync.TablesAdded);
        var databases = (await _api.Databases.GetAllAsync()).Result;
        Assert.Equal(imported.Database.Id, Assert.Single(databases).Id);
        var tables = await _api.Tables.GetTablesByDatabaseIdAsync(imported.Database.Id);
        Assert.Equal(["a", "b"], tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal(2, tables.Single(t => t.Name == "a").Columns.Count);
        Assert.Single(tables.Single(t => t.Name == "b").Relations);
        var summary = await _api.Tables.GetTablesByDatabaseIdAsync(imported.Database.Id, summary: true);
        Assert.All(summary, t => Assert.Empty(t.Columns));
        var completeness = await _api.Completeness.OfDatabaseAsync(imported.Database.Id);
        Assert.Equal(2, completeness.Tables);
    }

    [Fact]
    public async Task The_bundled_sample_database_imports_in_one_call_with_its_tables_and_relations()
    {
        var imported = await _api.SqliteImport.SampleAsync();

        Assert.Equal("Boutique (exemple)", imported.Database.Name);
        Assert.Equal(11, imported.Sync.TablesAdded);
        var tables = await _api.Tables.GetTablesByDatabaseIdAsync(imported.Database.Id);
        Assert.Contains(tables, t => t.Name == "orders" && t.Relations.Count > 0);
    }

    [Fact]
    public async Task A_refused_call_raises_the_generated_exception_with_the_status_code()
    {
        var error = await Assert.ThrowsAsync<GranitApiException>(() => _api.Databases.GetAsync("n-existe-pas"));

        Assert.Equal(404, error.StatusCode);
    }

    [Fact]
    public async Task Typed_inputs_create_and_list_webhooks()
    {
        var hook = await _api.Webhooks.CreateAsync(new WebhookInput { Url = "https://example.org/hook", Events = ["schema.changed"], IsActive = true });

        Assert.Equal("https://example.org/hook", hook.Url);
        Assert.Equal(hook.Id, Assert.Single(await _api.Webhooks.ListAsync()).Id);
    }
}
