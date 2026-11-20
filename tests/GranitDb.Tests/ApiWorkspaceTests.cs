using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Jalon 11 côté API : suppression d'une base, réglages d'affichage d'un domaine, aperçu du remplacement d'un fichier.</summary>
public sealed class ApiWorkspaceTests : IDisposable
{
    private const string V1 = "/api/v1";
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiWorkspaceTests()
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

    private async Task<string> ImportAsync(string name = "Boutique", string? file = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(file ?? _env.CreateSampleDb($"{Guid.NewGuid():N}.db"))), "file", "sample.db");
        form.Add(new StringContent(name), "name");
        var response = await _client.PostAsync($"{V1}/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string url) => await _client.GetFromJsonAsync<JsonElement>(url);

    private async Task<string> CreateDomainAsync(string databaseId, string name)
    {
        var response = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> FirstRelationAsync(string databaseId)
    {
        var tables = await GetJsonAsync($"{V1}/TableInfo/database/{databaseId}");
        return tables.EnumerateArray().SelectMany(t => t.GetProperty("relations").EnumerateArray())
            .First().GetProperty("id").GetString()!;
    }

    private string SourceFileOf(string databaseId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_env.Directory, "api-data", "granitdb.db")};Mode=ReadOnly");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SourcePath FROM Databases WHERE Id = $id";
        command.Parameters.AddWithValue("$id", databaseId);
        return (string)command.ExecuteScalar()!;
    }

    // --- Suppression d'une base ---

    [Fact]
    public async Task Deleting_a_database_removes_everything_it_owns_notes_it_in_the_history_and_drops_its_kept_file()
    {
        var db = await ImportAsync();
        var other = await ImportAsync("Autre");
        var domain = await CreateDomainAsync(db, "Ventes");
        var relation = await FirstRelationAsync(db);
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { hiddenRelations = new[] { relation } });
        await _client.PostAsJsonAsync($"{V1}/TagInfo/attach", new { name = "legacy", entityType = "database", entityId = db });
        var file = SourceFileOf(db);
        Assert.True(File.Exists(file));

        var delete = await _client.DeleteAsync($"{V1}/DatabaseInfo/{db}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DatabaseInfo/{db}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DomainInfo/database/{db}")).StatusCode);
        Assert.False(File.Exists(file));
        var history = await GetJsonAsync($"{V1}/History?entityId={db}");
        var entry = Assert.Single(history.EnumerateArray());
        Assert.Equal("Deleted", entry.GetProperty("field").GetString());
        Assert.Equal("Boutique", entry.GetProperty("oldValue").GetString());
        // L'autre base n'est pas touchée.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{V1}/DatabaseInfo/{other}")).StatusCode);
        Assert.True(File.Exists(SourceFileOf(other)));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"{V1}/DatabaseInfo/{db}")).StatusCode);
    }

    // --- Domaines configurables ---

    [Fact]
    public async Task A_domain_hides_neighbours_by_default_and_remembers_its_settings()
    {
        var db = await ImportAsync();
        var domain = await CreateDomainAsync(db, "Ventes");
        var relation = await FirstRelationAsync(db);

        var initial = (await GetJsonAsync($"{V1}/DomainInfo/database/{db}")).EnumerateArray().Single();
        Assert.False(initial.GetProperty("showNeighbors").GetBoolean());
        Assert.Empty(initial.GetProperty("hiddenRelations").EnumerateArray());

        var put = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { showNeighbors = true, hiddenRelations = new[] { relation } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = (await GetJsonAsync($"{V1}/DomainInfo/database/{db}")).EnumerateArray().Single();
        Assert.True(saved.GetProperty("showNeighbors").GetBoolean());
        Assert.Equal([relation], saved.GetProperty("hiddenRelations").EnumerateArray().Select(r => r.GetString()!).ToArray());

        // Un champ absent n'est pas touché ; une liste vide remet tout à l'affichage.
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { hiddenRelations = Array.Empty<string>() });
        var cleared = (await GetJsonAsync($"{V1}/DomainInfo/database/{db}")).EnumerateArray().Single();
        Assert.True(cleared.GetProperty("showNeighbors").GetBoolean());
        Assert.Empty(cleared.GetProperty("hiddenRelations").EnumerateArray());
    }

    [Fact]
    public async Task Renaming_a_domain_keeps_its_settings_and_an_unknown_relation_or_domain_is_refused()
    {
        var db = await ImportAsync();
        var domain = await CreateDomainAsync(db, "Ventes");
        var relation = await FirstRelationAsync(db);
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { showNeighbors = true, hiddenRelations = new[] { relation } });

        var rename = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}", new { databaseId = db, name = "Commerce", color = "#00ff00" });

        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = (await GetJsonAsync($"{V1}/DomainInfo/database/{db}")).EnumerateArray().Single();
        Assert.Equal("Commerce", renamed.GetProperty("name").GetString());
        Assert.True(renamed.GetProperty("showNeighbors").GetBoolean());
        Assert.Single(renamed.GetProperty("hiddenRelations").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { hiddenRelations = new[] { "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync($"{V1}/DomainInfo/nope/settings", new { showNeighbors = true })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_relation_or_a_domain_also_drops_the_hidden_marks()
    {
        var db = await ImportAsync();
        var kept = await CreateDomainAsync(db, "Ventes");
        var doomed = await CreateDomainAsync(db, "Archives");
        var relation = await FirstRelationAsync(db);
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{kept}/settings", new { hiddenRelations = new[] { relation } });
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{doomed}/settings", new { hiddenRelations = new[] { relation } });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DomainInfo/{doomed}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/RelationInfo/{relation}")).StatusCode);

        var domain = (await GetJsonAsync($"{V1}/DomainInfo/database/{db}")).EnumerateArray().Single();
        Assert.Empty(domain.GetProperty("hiddenRelations").EnumerateArray());
    }

    // --- Export HTML ---

    [Fact]
    public async Task The_html_export_is_one_self_contained_file_with_the_documentation_and_no_script_breakout()
    {
        var db = await ImportAsync("Boutique </script><b>");
        var domain = await CreateDomainAsync(db, "Ventes");
        var tables = await GetJsonAsync($"{V1}/TableInfo/database/{db}");
        var first = tables.EnumerateArray().First();
        await _client.PutAsJsonAsync($"{V1}/TableInfo/{first.GetProperty("id").GetString()}/documentation",
            new { description = "Description </script> exportée" });

        var response = await _client.GetAsync($"{V1}/DatabaseInfo/{db}/export/html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(first.GetProperty("name").GetString()!, html);
        Assert.DoesNotContain("Description </script>", html);
        Assert.Contains("Description \\u003C/script\\u003E", html);
        Assert.Contains(domain, html);
        Assert.DoesNotContain("{{DATA}}", html);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DatabaseInfo/nope/export/html")).StatusCode);
    }

    [Fact]
    public async Task A_table_colour_chosen_in_a_domain_is_stored_validated_cleared_and_carried_by_the_html_export()
    {
        var db = await ImportAsync();
        var domain = await CreateDomainAsync(db, "Ventes");
        var tables = (await GetJsonAsync($"{V1}/TableInfo/database/{db}")).EnumerateArray().ToList();
        var customers = tables.First(t => t.GetProperty("name").GetString() == "customers").GetProperty("id").GetString()!;
        var orders = tables.First(t => t.GetProperty("name").GetString() == "orders").GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{customers}", new { x = 10.0, y = 10.0 });

        var set = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{customers}/color", new { color = "#2E7D32" });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var view = await set.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("#2E7D32", view.GetProperty("tables").EnumerateArray().Single().GetProperty("color").GetString());
        var html = await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/html");
        Assert.Contains("\"colors\":{\"" + customers + "\":\"#2E7D32\"}", html);
        Assert.Contains(".erd .hd{fill:#1976d2}", html); // sans couleur choisie : le bleu de l'application, pas celui du domaine

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{customers}/color", new { color = "orange" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{orders}/color", new { color = "#112233" })).StatusCode); // pas dans le domaine

        var cleared = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{customers}/color", new { color = "" });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tables").EnumerateArray().Single().GetProperty("color").ValueKind);
        Assert.Contains("\"colors\":{}", await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/html"));
    }

    [Fact]
    public async Task The_html_export_carries_the_domain_plan_and_the_hidden_relations_for_its_diagrams()
    {
        var db = await ImportAsync();
        var domain = await CreateDomainAsync(db, "Ventes");
        var tables = (await GetJsonAsync($"{V1}/TableInfo/database/{db}")).EnumerateArray().ToList();
        var customers = tables.First(t => t.GetProperty("name").GetString() == "customers").GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/tables/{customers}", new { x = 120.0, y = 40.0 });
        var relation = await FirstRelationAsync(db);
        await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { hiddenRelations = new[] { relation } });

        var html = await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/html");

        Assert.Contains("\"layout\":{\"" + customers + "\":[120", html);
        Assert.Contains("\"hidden\":[\"" + relation + "\"]", html);
        Assert.Contains("function domainErd", html);
        Assert.Contains("function neighboursErd", html);
        // Diagramme interactif (zoom, déplacement) et mêmes mesures que l'application pour respecter les places enregistrées.
        Assert.Contains("function zoom", html);
        Assert.Contains("HEAD = 30", html);
        Assert.Contains("ROW = 20", html);
        Assert.Contains("function inkOn", html); // texte lisible sur la couleur d'un domaine
        Assert.Contains("Clés primaires seulement", html);
    }

    [Fact]
    public async Task Markdown_and_csv_exports_list_the_columns_and_defuse_spreadsheet_formulas()
    {
        var db = await ImportAsync();
        var first = (await GetJsonAsync($"{V1}/TableInfo/database/{db}")).EnumerateArray().First();
        var column = first.GetProperty("columns").EnumerateArray().First().GetProperty("id").GetString()!;
        await _client.PutAsJsonAsync($"{V1}/ColumnInfo/{column}/documentation", new { description = "=HYPERLINK(\"http://x\")|a" });

        var markdown = await _client.GetAsync($"{V1}/DatabaseInfo/{db}/export/markdown");
        var csv = await _client.GetAsync($"{V1}/DatabaseInfo/{db}/export/csv");

        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        var md = await markdown.Content.ReadAsStringAsync();
        Assert.StartsWith("# Boutique", md);
        Assert.Contains("| # | Colonne | Type |", md);
        Assert.Contains("\\|a", md);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("\"Table\";\"", text.Replace("Table;Type", "\"Table\";\"Type")); // en-tête présent
        Assert.Contains("\"'=HYPERLINK(", text);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DatabaseInfo/nope/export/markdown")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DatabaseInfo/nope/export/csv")).StatusCode);
    }

    // --- Remplacement d'un fichier ---

    [Fact]
    public async Task Replacing_a_file_can_be_previewed_without_changing_the_catalog()
    {
        var db = await ImportAsync();
        var newer = _env.CreateSampleDb($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(newer, "CREATE TABLE invoices (id INTEGER PRIMARY KEY, total REAL); DROP TABLE tags;");
        var before = (await GetJsonAsync($"{V1}/TableInfo/database/{db}")).GetArrayLength();

        var preview = await PostFileAsync($"{V1}/import/sqlite/{db}/preview", newer);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var changes = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changes").EnumerateArray().ToList();
        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "added" && c.GetProperty("kind").GetString() == "table");
        Assert.Contains(changes, c => c.GetProperty("action").GetString() == "removed" && c.GetProperty("kind").GetString() == "table");
        Assert.Equal(before, (await GetJsonAsync($"{V1}/TableInfo/database/{db}")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await PostFileAsync($"{V1}/import/sqlite/nope/preview", newer)).StatusCode);
    }

    [Fact]
    public async Task The_preview_refuses_what_is_not_a_sqlite_file_and_keeps_nothing_on_disk()
    {
        var db = await ImportAsync();
        var fake = _env.NewFile("fake.db");
        File.WriteAllText(fake, "pas une base de données");
        var sources = Path.Combine(_env.Directory, "api-data", "sources");
        var filesBefore = Directory.GetFiles(sources).Length;

        var refused = await PostFileAsync($"{V1}/import/sqlite/{db}/preview", fake);
        var valid = await PostFileAsync($"{V1}/import/sqlite/{db}/preview", _env.CreateSampleDb($"{Guid.NewGuid():N}.db"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(filesBefore, Directory.GetFiles(sources).Length);
    }

    private async Task<HttpResponseMessage> PostFileAsync(string url, string path)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", "next.db");
        return await _client.PostAsync(url, form);
    }
}
