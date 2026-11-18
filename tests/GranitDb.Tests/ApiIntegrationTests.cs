using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Tests de bout en bout : API démarrée sur un vrai Kestrel local, catalogue SQLite, aucun serveur externe.</summary>
public sealed class ApiIntegrationTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data"),
            ["Catalog:AllowLocalPaths"] = "false"
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

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName, string? name = null,
        string? keepFile = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        if (name != null) form.Add(new StringContent(name), "name");
        if (keepFile != null) form.Add(new StringContent(keepFile), "keepFile");
        return form;
    }

    private byte[] SampleBytes()
    {
        return File.ReadAllBytes(_env.CreateSampleDb());
    }

    private async Task<string> ImportAsync(string? name = "Boutique")
    {
        var response = await _client.PostAsync("/import/sqlite", Upload(SampleBytes(), "sample.db", name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Starts_with_an_empty_sqlite_catalog_and_no_server()
    {
        var response = await _client.GetAsync("/DatabaseInfo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());
        Assert.True(File.Exists(Path.Combine(_env.Directory, "api-data", "granitdb.db")));
    }

    [Fact]
    public async Task Upload_then_browse_tables_columns_and_relations()
    {
        var id = await ImportAsync();

        var list = await _client.GetFromJsonAsync<JsonElement>("/DatabaseInfo");
        var database = Assert.Single(list.EnumerateArray());
        Assert.Equal("Boutique", database.GetProperty("name").GetString());
        Assert.Equal("Sqlite", database.GetProperty("type").GetString());

        var tables = await _client.GetFromJsonAsync<JsonElement>($"/TableInfo/database/{id}");
        Assert.Equal(5, tables.GetArrayLength());
        var orders = tables.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "orders");
        Assert.Equal(3, orders.GetProperty("columns").GetArrayLength());
        Assert.Equal(2, orders.GetProperty("relations").GetArrayLength()); // vers customers et depuis order_lines

        var single = await _client.GetFromJsonAsync<JsonElement>($"/TableInfo/{orders.GetProperty("id").GetString()}");
        Assert.Equal("orders", single.GetProperty("name").GetString());
        Assert.Equal(3, single.GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task Upload_of_a_non_sqlite_file_returns_400()
    {
        var response = await _client.PostAsync("/import/sqlite",
            Upload(new byte[3000].Select(_ => (byte)'q').ToArray(), "bad.db"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQLite", await response.Content.ReadAsStringAsync());
        Assert.Equal("[]", (await _client.GetStringAsync("/DatabaseInfo")).Trim());
    }

    [Fact]
    public async Task Upload_without_file_returns_400()
    {
        var response = await _client.PostAsync("/import/sqlite", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Descriptions_can_be_edited_and_survive_a_sync()
    {
        var id = await ImportAsync();
        var tables = await _client.GetFromJsonAsync<JsonElement>($"/TableInfo/database/{id}");
        var customers = tables.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "customers");
        var tableId = customers.GetProperty("id").GetString()!;

        var put = await _client.PutAsJsonAsync($"/TableInfo/{tableId}", new
        {
            id = tableId, name = "customers", databaseId = id, tableType = "Table", description = "Mes clients"
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var sync = await _client.PostAsync($"/DatabaseInfo/{id}/sync", null);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);

        var after = await _client.GetFromJsonAsync<JsonElement>($"/TableInfo/{tableId}");
        Assert.Equal("Mes clients", after.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Replace_endpoint_resyncs_the_database()
    {
        var id = await ImportAsync();
        var v2 = _env.NewFile("v2.db");
        TestCatalog.Execute(v2, TestCatalog.SampleSql + "CREATE TABLE coupons (code TEXT);");

        var response = await _client.PutAsync($"/import/sqlite/{id}", Upload(File.ReadAllBytes(v2), "v2.db"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tables = await _client.GetFromJsonAsync<JsonElement>($"/TableInfo/database/{id}");
        Assert.Equal(6, tables.GetArrayLength());
    }

    [Fact]
    public async Task Replace_and_sync_return_404_for_unknown_ids()
    {
        var replace = await _client.PutAsync("/import/sqlite/missing", Upload(SampleBytes(), "s.db"));
        Assert.Equal(HttpStatusCode.NotFound, replace.StatusCode);

        var sync = await _client.PostAsync("/DatabaseInfo/missing/sync", null);
        Assert.Equal(HttpStatusCode.NotFound, sync.StatusCode);
    }

    [Fact]
    public async Task Sync_without_a_kept_file_returns_400()
    {
        var response = await _client.PostAsync("/import/sqlite",
            Upload(SampleBytes(), "s.db", "Sans fichier", "false"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("database").GetProperty("id").GetString();

        var sync = await _client.PostAsync($"/DatabaseInfo/{id}/sync", null);

        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
    }

    [Fact]
    public async Task Local_path_import_is_forbidden_by_default()
    {
        var response = await _client.PostAsJsonAsync("/import/sqlite/local", new { path = _env.CreateSampleDb() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_database_removes_it_and_returns_404_afterwards()
    {
        var id = await ImportAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/DatabaseInfo/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/DatabaseInfo/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/DatabaseInfo/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/TableInfo/database/{id}")).StatusCode);
    }

    [Fact]
    public async Task Creating_a_relation_to_unknown_columns_returns_400()
    {
        var response = await _client.PostAsJsonAsync("/RelationInfo",
            new { firstId = "no", secondId = "way", relationType = "One-to-One" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Id_mismatch_on_put_returns_400()
    {
        var id = await ImportAsync();
        var response = await _client.PutAsJsonAsync($"/DatabaseInfo/{id}",
            new { id = "other", name = "x", type = "Sqlite" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
