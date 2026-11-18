using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Suivi de l'audit (F1-F4) : en-têtes de durcissement, identifiant de requête, balayage des fichiers sources orphelins.</summary>
public sealed class HardeningTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private KestrelApiFactory? _factory;
    private HttpClient? _client;

    private string DataDir => Path.Combine(_env.Directory, "api-data");

    private HttpClient Start()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = DataDir
        });
        return _client = _factory.CreateKestrelClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    [Fact]
    public async Task Every_response_carries_a_request_id_and_hardening_headers()
    {
        var response = await Start().GetAsync("/api/v1/DatabaseInfo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(string.IsNullOrWhiteSpace(response.Headers.GetValues("X-Request-Id").Single()));
    }

    [Fact]
    public async Task Startup_removes_unreferenced_old_source_files_and_keeps_recent_ones()
    {
        var sources = Path.Combine(DataDir, "sources");
        Directory.CreateDirectory(sources);
        var oldOrphan = Path.Combine(sources, $"{Guid.NewGuid()}.db");
        var oldTemp = Path.Combine(sources, $"{Guid.NewGuid()}.tmp");
        var recent = Path.Combine(sources, $"{Guid.NewGuid()}.db");
        var other = Path.Combine(sources, "notes.txt");
        foreach (var file in new[] { oldOrphan, oldTemp, recent, other }) File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-2));

        await Start().GetAsync("/api/v1/DatabaseInfo"); // démarre l'API

        Assert.False(File.Exists(oldOrphan));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(recent)); // un import peut être en cours
        Assert.True(File.Exists(other)); // pas un fichier du serveur
    }

    [Fact]
    public async Task A_source_file_still_referenced_by_a_database_survives_the_sweep()
    {
        var client = Start();
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE t (id INTEGER PRIMARY KEY);");
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(File.ReadAllBytes(path)), "file", "keep.db" },
            { new StringContent("Keep"), "name" }
        };
        var created = await client.PostAsync("/api/v1/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var kept = Directory.GetFiles(Path.Combine(DataDir, "sources"), "*.db");
        Assert.Single(kept);
        foreach (var file in kept) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

        // Un second démarrage balaie le dossier : le fichier référencé reste.
        _client!.Dispose();
        _factory!.Dispose();
        SqliteConnection.ClearAllPools();
        var restarted = Start();
        Assert.Equal(HttpStatusCode.OK, (await restarted.GetAsync("/api/v1/DatabaseInfo")).StatusCode);

        Assert.True(File.Exists(kept[0]));
    }

    [Fact]
    public async Task The_table_summary_leaves_out_columns_and_the_columns_come_with_the_table()
    {
        var client = Start();
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT);");
        var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(path)), "file", "s.db" }, { new StringContent("S"), "name" } };
        var created = await (await client.PostAsync("/api/v1/import/sqlite", form)).Content.ReadFromJsonAsync<JsonElement>();
        var db = created.GetProperty("database").GetProperty("id").GetString();

        var summary = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/database/{db}?summary=true")).EnumerateArray().Single();
        Assert.Equal("a", summary.GetProperty("name").GetString());
        Assert.Equal(0, summary.GetProperty("columns").GetArrayLength());
        var full = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/database/{db}")).EnumerateArray().Single();
        Assert.Equal(2, full.GetProperty("columns").GetArrayLength());
        var columns = await client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/{full.GetProperty("id").GetString()}/columns");
        Assert.Equal(["id", "name"], columns.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray());
    }
}
