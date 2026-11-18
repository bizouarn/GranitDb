using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Import d'une documentation externe : rapprochement par nom, descriptions existantes préservées, domaines, relations.</summary>
public sealed class DocumentationImportTests : IDisposable
{
    private const string V1 = "/api/v1";
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public DocumentationImportTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
        });
        _client = _factory.CreateKestrelClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private async Task<string> ImportDbAsync()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb($"{Guid.NewGuid():N}.db"))), "file", "sample.db");
        form.Add(new StringContent("Boutique"), "name");
        var response = await _client.PostAsync($"{V1}/import/sqlite", form);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    private static object Doc(bool overwrite = false, bool dryRun = false) => new
    {
        overwrite, dryRun,
        tables = new object[]
        {
            new { name = "CUSTOMERS", description = "Clients", columns = new[] { new { name = "email", description = "Adresse" }, new { name = "absente", description = "x" } } },
            new { name = "inconnue", description = "x", columns = Array.Empty<object>() }
        },
        domains = new[] { new { name = "Ventes", description = (string?)null, tables = new[] { "customers", "orders", "nope" }, layout = new[] { new { table = "customers", x = 40.0, y = 60.0 } } } },
        relations = new[]
        {
            new { table = "tags", column = "id", refTable = "customers", refColumn = "id", relationType = (string?)null, description = "lien" },
            new { table = "tags", column = "zzz", refTable = "customers", refColumn = "id", relationType = (string?)null, description = (string?)null }
        }
    };

    private async Task<JsonElement> PostAsync(string db, object body)
    {
        var response = await _client.PostAsJsonAsync($"{V1}/import/documentation/{db}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Import_describes_matches_by_name_and_reports_the_rest()
    {
        var db = await ImportDbAsync();
        var result = await PostAsync(db, Doc());

        Assert.Equal(1, result.GetProperty("tablesDescribed").GetInt32());
        Assert.Equal(1, result.GetProperty("columnsDescribed").GetInt32());
        Assert.Equal(1, result.GetProperty("tablesNotFound").GetInt32());
        Assert.Equal(1, result.GetProperty("columnsNotFound").GetInt32());
        Assert.Equal(1, result.GetProperty("domainsCreated").GetInt32());
        Assert.Equal(2, result.GetProperty("domainLinksCreated").GetInt32());
        Assert.Equal(1, result.GetProperty("placementsSet").GetInt32());
        Assert.Equal(1, result.GetProperty("relationsCreated").GetInt32());
        Assert.Equal(1, result.GetProperty("relationsSkipped").GetInt32());

        var domains = await _client.GetFromJsonAsync<JsonElement>($"{V1}/DomainInfo/database/{db}");
        var place = domains.EnumerateArray().Single().GetProperty("tables").EnumerateArray().Single(p => p.GetProperty("x").ValueKind == JsonValueKind.Number);
        Assert.Equal(40, place.GetProperty("x").GetDouble());
        Assert.Equal(60, place.GetProperty("y").GetDouble());

        var tables = await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/database/{db}");
        var customers = tables.EnumerateArray().First(t => t.GetProperty("name").GetString() == "customers");
        Assert.Equal("Clients", customers.GetProperty("description").GetString());
        Assert.Equal("Adresse", customers.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "email").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Import_keeps_hand_written_text_unless_overwrite_and_is_repeatable()
    {
        var db = await ImportDbAsync();
        var tables = await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/database/{db}");
        var id = tables.EnumerateArray().First(t => t.GetProperty("name").GetString() == "customers").GetProperty("id").GetString();
        await _client.PutAsJsonAsync($"{V1}/TableInfo/{id}/documentation", new { description = "Saisi à la main" });

        await PostAsync(db, Doc());
        var kept = await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/{id}");
        Assert.Equal("Saisi à la main", kept.GetProperty("description").GetString());

        var again = await PostAsync(db, Doc());
        Assert.Equal(0, again.GetProperty("domainsCreated").GetInt32());
        Assert.Equal(0, again.GetProperty("domainLinksCreated").GetInt32());
        Assert.Equal(0, again.GetProperty("relationsCreated").GetInt32());

        await PostAsync(db, Doc(overwrite: true));
        var replaced = await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/{id}");
        Assert.Equal("Clients", replaced.GetProperty("description").GetString());
    }

    [Fact]
    public async Task DryRun_reports_without_writing_and_unknown_database_is_404()
    {
        var db = await ImportDbAsync();
        var result = await PostAsync(db, Doc(dryRun: true));
        Assert.Equal(1, result.GetProperty("tablesDescribed").GetInt32());

        var domains = await _client.GetFromJsonAsync<JsonElement>($"{V1}/DomainInfo/database/{db}");
        Assert.Empty(domains.EnumerateArray());

        var missing = await _client.PostAsJsonAsync($"{V1}/import/documentation/nope", Doc());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
