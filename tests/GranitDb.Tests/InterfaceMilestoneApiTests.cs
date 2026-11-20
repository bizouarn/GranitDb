using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>
///     Jalon 11 côté API : relations indirectes et plan d'origine d'un domaine, impact des relations multi-colonnes,
///     tables saisies à la main (jamais supprimées par la synchronisation, la source gagne si elle gagne la table).
/// </summary>
public sealed class InterfaceMilestoneApiTests : IDisposable
{
    private const string V1 = "/api/v1";
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public InterfaceMilestoneApiTests()
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

    private string CompositeDb(string extra = "")
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, $"""
            CREATE TABLE owner (id INTEGER PRIMARY KEY);
            CREATE TABLE parent (a INTEGER, b INTEGER, PRIMARY KEY (a, b));
            CREATE TABLE child (id INTEGER PRIMARY KEY, pa INTEGER, pb INTEGER, owner_id INTEGER REFERENCES owner(id), FOREIGN KEY (pa, pb) REFERENCES parent(a, b));
            {extra}
            """);
        return path;
    }

    private async Task<string> ImportAsync(string path)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", "db.db");
        form.Add(new StringContent("Base"), "name");
        var response = await _client.PostAsync($"{V1}/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    private async Task<List<JsonElement>> TablesAsync(string db) =>
        (await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/database/{db}")).EnumerateArray().ToList();

    private static string TableId(IEnumerable<JsonElement> tables, string table) =>
        tables.First(t => t.GetProperty("name").GetString() == table).GetProperty("id").GetString()!;

    private static string ColumnId(IEnumerable<JsonElement> tables, string table, string column) =>
        tables.First(t => t.GetProperty("name").GetString() == table).GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == column).GetProperty("id").GetString()!;

    private async Task<string> CreateDomainAsync(string db, params string[] tableIds)
    {
        var response = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId = db, name = "Tout" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        foreach (var table in tableIds)
            Assert.Equal(HttpStatusCode.OK, (await _client.PutAsJsonAsync($"{V1}/DomainInfo/{id}/tables/{table}", new { })).StatusCode);
        return id;
    }

    private async Task<JsonElement> DomainAsync(string db, string id) =>
        (await _client.GetFromJsonAsync<JsonElement>($"{V1}/DomainInfo/database/{db}")).EnumerateArray()
        .First(d => d.GetProperty("id").GetString() == id);

    private static string[] Hidden(JsonElement domain) =>
        domain.GetProperty("hiddenRelations").EnumerateArray().Select(h => h.GetString()!).ToArray();

    // --- Impact et relations multi-colonnes ---

    [Fact]
    public async Task The_impact_of_a_column_follows_every_pair_of_a_composite_relation()
    {
        var db = await ImportAsync(CompositeDb());
        var tables = await TablesAsync(db);

        // parent.b est la deuxième paire de la clé composite : sans prise en compte des paires, rien ne le référencerait.
        var impact = await _client.GetFromJsonAsync<JsonElement>($"{V1}/Impact/column/{ColumnId(tables, "parent", "b")}");
        var dependents = impact.GetProperty("dependents").EnumerateArray().ToList();
        var dependent = Assert.Single(dependents);
        Assert.Equal("child", dependent.GetProperty("tableName").GetString());
        Assert.Equal("pb", dependent.GetProperty("columnName").GetString());

        var reverse = await _client.GetFromJsonAsync<JsonElement>($"{V1}/Impact/column/{ColumnId(tables, "child", "pb")}");
        Assert.Equal("b", Assert.Single(reverse.GetProperty("dependencies").EnumerateArray()).GetProperty("columnName").GetString());

        // Au niveau de la table, la clé composite ne fait qu'un lien (pas deux).
        var tableImpact = await _client.GetFromJsonAsync<JsonElement>($"{V1}/Impact/table/{TableId(tables, "child")}");
        Assert.Equal(["owner", "parent"],
            tableImpact.GetProperty("dependencies").EnumerateArray().Select(d => d.GetProperty("tableName").GetString()!).Order().ToArray());
    }

    // --- Relations indirectes et plan d'origine d'un domaine ---

    [Fact]
    public async Task Hiding_indirect_relations_keeps_only_detected_foreign_keys_and_is_repeatable()
    {
        var db = await ImportAsync(CompositeDb());
        var tables = await TablesAsync(db);
        var domain = await CreateDomainAsync(db, TableId(tables, "owner"), TableId(tables, "child"), TableId(tables, "parent"));
        var manual = await _client.PostAsJsonAsync($"{V1}/RelationInfo",
            new { firstId = ColumnId(tables, "owner", "id"), secondId = ColumnId(tables, "parent", "a"), relationType = "One-to-Many" });
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        var manualId = (await manual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        for (var round = 0; round < 2; round++)
        {
            var response = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { hideIndirectRelations = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([manualId], Hidden(await response.Content.ReadFromJsonAsync<JsonElement>()));
        }

        // Sans plan d'origine connu, « réinitialiser » est refusé.
        Assert.False((await DomainAsync(db, domain)).GetProperty("hasDocumentationOrigin").GetBoolean());
        var refused = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{domain}/settings", new { resetToDocumentation = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_domain_imported_from_a_documentation_can_be_reset_to_its_original_plan()
    {
        var db = await ImportAsync(CompositeDb());
        // Le plan d'origine ne trace aucune relation : tout est masqué dans le domaine importé.
        var import = await _client.PostAsJsonAsync($"{V1}/import/documentation/{db}", new
        {
            domains = new[] { new { name = "Plan", tables = new[] { "owner", "child", "parent" }, drawnRelations = Array.Empty<object>() } }
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var domains = (await _client.GetFromJsonAsync<JsonElement>($"{V1}/DomainInfo/database/{db}")).EnumerateArray().ToList();
        var domain = Assert.Single(domains);
        var id = domain.GetProperty("id").GetString()!;
        Assert.True(domain.GetProperty("hasDocumentationOrigin").GetBoolean());
        var original = Hidden(domain);
        Assert.Equal(2, original.Length); // la clé composite compte pour une relation (pas deux paires) + la clé simple

        var shown = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{id}/settings", new { hiddenRelations = Array.Empty<string>() });
        Assert.Empty(Hidden(await shown.Content.ReadFromJsonAsync<JsonElement>()));

        var reset = await _client.PutAsJsonAsync($"{V1}/DomainInfo/{id}/settings", new { resetToDocumentation = true });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(original.Order(), Hidden(await reset.Content.ReadFromJsonAsync<JsonElement>()).Order());
    }

    // --- Tables saisies à la main ---

    private Task<HttpResponseMessage> PostManualAsync(string db, string name = "fichier_clients", object[]? columns = null, string? domainId = null) =>
        _client.PostAsJsonAsync($"{V1}/TableInfo/manual", new
        {
            databaseId = db, name, tableType = "Table", description = "Export hebdomadaire", domainId,
            columns = columns ?? [new { name = "id", dataType = "integer", isPrimaryKey = true, isNullable = false, description = "Clé" },
                                  new { name = "email", dataType = "text", isNullable = true, isSensitive = true }]
        });

    [Fact]
    public async Task A_manual_table_is_created_with_its_columns_and_domain_in_one_go()
    {
        var db = await ImportAsync(CompositeDb());
        var domain = await CreateDomainAsync(db);

        var response = await PostManualAsync(db, domainId: domain);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(table.GetProperty("isManual").GetBoolean());
        var columns = table.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["id", "email"], columns.Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal([0, 1], columns.Select(c => c.GetProperty("position").GetInt32()).ToArray());
        Assert.True(columns[0].GetProperty("isPrimaryKey").GetBoolean());
        Assert.False(columns[0].GetProperty("isNullable").GetBoolean());
        Assert.True(columns[1].GetProperty("isSensitive").GetBoolean());
        Assert.Equal(table.GetProperty("id").GetString(),
            Assert.Single((await DomainAsync(db, domain)).GetProperty("tables").EnumerateArray()).GetProperty("tableId").GetString());
    }

    [Fact]
    public async Task A_manual_table_is_all_or_nothing_and_refuses_duplicates()
    {
        var db = await ImportAsync(CompositeDb());

        var twice = await PostManualAsync(db, "doublon", [new { name = "a", dataType = "text" }, new { name = "A", dataType = "text" }]);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.DoesNotContain(await TablesAsync(db), t => t.GetProperty("name").GetString() == "doublon");

        Assert.Equal(HttpStatusCode.Created, (await PostManualAsync(db)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostManualAsync(db)).StatusCode); // même nom
        Assert.Equal(HttpStatusCode.BadRequest, (await PostManualAsync(db, "OWNER")).StatusCode); // table de la source, casse ignorée
        Assert.Equal(HttpStatusCode.BadRequest, (await PostManualAsync("inconnue")).StatusCode);
    }

    [Fact]
    public async Task Columns_are_added_to_a_manual_table_but_not_to_a_table_of_the_source()
    {
        var db = await ImportAsync(CompositeDb());
        var manual = await (await PostManualAsync(db)).Content.ReadFromJsonAsync<JsonElement>();
        var id = manual.GetProperty("id").GetString()!;

        var added = await _client.PostAsJsonAsync($"{V1}/TableInfo/{id}/columns",
            new { columns = new[] { new { name = "ville", dataType = "text", isNullable = true } } });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var columns = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["id", "email", "ville"], columns.Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, columns[2].GetProperty("position").GetInt32());

        var again = await _client.PostAsJsonAsync($"{V1}/TableInfo/{id}/columns", new { columns = new[] { new { name = "EMAIL", dataType = "text" } } });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var source = TableId(await TablesAsync(db), "owner");
        var refused = await _client.PostAsJsonAsync($"{V1}/TableInfo/{source}/columns", new { columns = new[] { new { name = "x", dataType = "text" } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_manual_table_survives_a_resync_and_is_not_announced_as_removed()
    {
        var db = await ImportAsync(CompositeDb());
        var id = (await (await PostManualAsync(db)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var preview = await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync/preview", null);
        Assert.DoesNotContain("fichier_clients", await preview.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null)).StatusCode);

        var table = (await TablesAsync(db)).Single(t => t.GetProperty("id").GetString() == id);
        Assert.True(table.GetProperty("isManual").GetBoolean());
        Assert.Equal(2, table.GetProperty("columns").GetArrayLength());
        Assert.Equal("Clé", table.GetProperty("columns").EnumerateArray().First().GetProperty("description").GetString());
    }

    [Fact]
    public async Task When_the_source_gains_the_same_table_the_source_wins_and_the_mark_is_lifted()
    {
        var db = await ImportAsync(CompositeDb());
        var id = (await (await PostManualAsync(db, "fichier_clients")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(CompositeDb("CREATE TABLE fichier_clients (id INTEGER PRIMARY KEY, nom TEXT);"))), "file", "db.db");
        var replaced = await _client.PutAsync($"{V1}/import/sqlite/{db}", form);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        var table = (await TablesAsync(db)).Single(t => t.GetProperty("name").GetString() == "fichier_clients");
        Assert.Equal(id, table.GetProperty("id").GetString()); // la documentation reste attachée à la même table
        Assert.False(table.GetProperty("isManual").GetBoolean());
        // Les colonnes sont désormais celles de la source : « email », saisie à la main, a disparu.
        Assert.Equal(["id", "nom"], table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).Order().ToArray());
    }
}
