using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Connectors;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Relations multi-colonnes (clé étrangère composite, comme dans Dataedo) : une relation, plusieurs paires de colonnes.</summary>
public sealed class MultiColumnRelationTests : IDisposable
{
    private const string V1 = "/api/v1";
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public MultiColumnRelationTests()
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

    /// <summary>Une clé composite (child → parent sur deux colonnes) et une clé simple (child.owner → owner).</summary>
    private string CompositeDb(bool declared = true)
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, $"""
            CREATE TABLE owner (id INTEGER PRIMARY KEY);
            CREATE TABLE parent (a INTEGER, b INTEGER, PRIMARY KEY (a, b));
            CREATE TABLE child (id INTEGER PRIMARY KEY, pa INTEGER, pb INTEGER, owner_id INTEGER REFERENCES owner(id){(declared ? ", FOREIGN KEY (pa, pb) REFERENCES parent(a, b)" : "")});
            """);
        return path;
    }

    private async Task<string> ImportAsync(string path)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(path)), "file", "multi.db");
        form.Add(new StringContent("Multi"), "name");
        var response = await _client.PostAsync($"{V1}/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    private async Task<List<JsonElement>> TablesAsync(string db) =>
        (await _client.GetFromJsonAsync<JsonElement>($"{V1}/TableInfo/database/{db}")).EnumerateArray().ToList();

    private static string ColumnId(IEnumerable<JsonElement> tables, string table, string column) =>
        tables.First(t => t.GetProperty("name").GetString() == table).GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == column).GetProperty("id").GetString()!;

    private static List<JsonElement> RelationsOf(IEnumerable<JsonElement> tables, string table) =>
        tables.First(t => t.GetProperty("name").GetString() == table).GetProperty("relations").EnumerateArray().ToList();

    [Fact]
    public async Task The_sqlite_connector_groups_the_columns_of_a_composite_key_and_leaves_simple_keys_alone()
    {
        var schema = await new SqliteSchemaConnector().ReadSchemaAsync(CompositeDb());

        var composite = schema.ForeignKeys.Where(f => f.RefTable == "parent").ToList();
        Assert.Equal(2, composite.Count);
        Assert.Equal(["a", "b"], composite.Select(f => f.RefColumn));
        Assert.Equal(["pa", "pb"], composite.Select(f => f.Column));
        Assert.All(composite, f => Assert.Equal(composite[0].Constraint, f.Constraint));
        Assert.NotNull(composite[0].Constraint);
        Assert.Null(schema.ForeignKeys.Single(f => f.RefTable == "owner").Constraint);
    }

    [Fact]
    public async Task A_composite_foreign_key_becomes_one_relation_that_survives_a_resync()
    {
        var db = await ImportAsync(CompositeDb());

        for (var round = 0; round < 2; round++)
        {
            if (round == 1) Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null)).StatusCode);
            var tables = await TablesAsync(db);
            var relations = RelationsOf(tables, "child");
            Assert.Equal(2, relations.Count); // la clé composite + la clé simple
            var composite = relations.Single(r => r.GetProperty("extraColumns").GetArrayLength() > 0);
            Assert.Equal(ColumnId(tables, "parent", "a"), composite.GetProperty("firstId").GetString());
            Assert.Equal(ColumnId(tables, "child", "pa"), composite.GetProperty("secondId").GetString());
            var extra = composite.GetProperty("extraColumns").EnumerateArray().Single();
            Assert.Equal(ColumnId(tables, "parent", "b"), extra.GetProperty("firstId").GetString());
            Assert.Equal(ColumnId(tables, "child", "pb"), extra.GetProperty("secondId").GetString());
        }
    }

    [Fact]
    public async Task The_exports_show_a_composite_relation_once_with_all_its_columns()
    {
        var db = await ImportAsync(CompositeDb());

        var html = await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/html");
        var markdown = await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/markdown");

        Assert.Contains("\"extra\":[{\"referencedColumn\":\"b\",\"referencingColumn\":\"pb\"}]", html);
        Assert.Contains("function pairsOf", html);
        Assert.Contains("- child.(pa, pb) → parent.(a, b)", markdown);
        Assert.Contains("- child.owner_id → owner.id", markdown);
    }

    [Fact]
    public async Task The_excel_export_is_a_real_workbook_with_one_row_per_column_and_one_per_composite_relation()
    {
        var db = await ImportAsync(CompositeDb());
        var response = await _client.GetAsync($"{V1}/DatabaseInfo/{db}/export/xlsx");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync());
        string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
        var workbook = Read("xl/workbook.xml");
        foreach (var sheet in new[] { "Tables", "Colonnes", "Relations", "Index" }) Assert.Contains($"name=\"{sheet}\"", workbook);
        var columns = Read("xl/worksheets/sheet2.xml");
        Assert.Contains("<t xml:space=\"preserve\">pa</t>", columns);
        Assert.Equal(1 + 7, System.Text.RegularExpressions.Regex.Matches(columns, "<row ").Count); // en-tête + 1 + 2 + 4 colonnes
        var relations = Read("xl/worksheets/sheet3.xml");
        Assert.Contains("pa, pb", relations); // la clé composite tient sur une seule ligne
        Assert.Equal(1 + 2, System.Text.RegularExpressions.Regex.Matches(relations, "<row ").Count);
        Assert.DoesNotContain("<f>", columns); // jamais de formule
    }

    [Fact]
    public async Task The_html_export_has_a_printable_version_for_pdf()
    {
        var db = await ImportAsync(CompositeDb());

        var html = await _client.GetStringAsync($"{V1}/DatabaseInfo/{db}/export/html");

        Assert.Contains("function printAll", html);
        Assert.Contains("href=\"#/print\"", html);
        Assert.Contains("@media print", html);
    }

    [Fact]
    public async Task Documentation_import_creates_a_multi_column_relation_and_hides_it_unless_drawn()
    {
        var db = await ImportAsync(CompositeDb(declared: false));
        var relation = new
        {
            table = "child", column = "pa", refTable = "parent", refColumn = "a",
            extra = new[] { new { column = "pb", refColumn = "b" } }
        };
        var result = await _client.PostAsJsonAsync($"{V1}/import/documentation/{db}", new
        {
            relations = new[] { relation },
            domains = new[] { new { name = "Dom", tables = new[] { "child", "parent" }, drawnRelations = new[] { relation } } }
        });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var summary = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, summary.GetProperty("relationsCreated").GetInt32());
        Assert.Equal(0, summary.GetProperty("relationsHidden").GetInt32()); // tracée : pas masquée
        var tables = await TablesAsync(db);
        var created = RelationsOf(tables, "child").Single(r => r.GetProperty("extraColumns").GetArrayLength() == 1);
        Assert.Equal(ColumnId(tables, "child", "pa"), created.GetProperty("secondId").GetString());
    }

    [Fact]
    public async Task A_manual_multi_column_relation_is_checked_and_stored()
    {
        var db = await ImportAsync(CompositeDb(declared: false));
        var tables = await TablesAsync(db);
        string Id(string t, string c) => ColumnId(tables, t, c);

        var created = await _client.PostAsJsonAsync($"{V1}/RelationInfo", new
        {
            firstId = Id("parent", "a"), secondId = Id("child", "pa"), relationType = "One-to-Many",
            extraColumns = new[] { new { firstId = Id("parent", "b"), secondId = Id("child", "pb") } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var read = await _client.GetFromJsonAsync<JsonElement>($"{V1}/RelationInfo/{id}");
        Assert.Equal(1, read.GetProperty("extraColumns").GetArrayLength());

        // Une paire d'une autre table que celle du premier côté est refusée.
        var wrongSide = await _client.PostAsJsonAsync($"{V1}/RelationInfo", new
        {
            firstId = Id("parent", "a"), secondId = Id("child", "id"), relationType = "One-to-Many",
            extraColumns = new[] { new { firstId = Id("owner", "id"), secondId = Id("child", "pa") } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrongSide.StatusCode);

        // Supprimer la colonne d'une paire retire la paire, la relation reste ; supprimer la relation retire tout.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/ColumnInfo/{Id("child", "pb")}")).StatusCode);
        var after = await _client.GetFromJsonAsync<JsonElement>($"{V1}/RelationInfo/{id}");
        Assert.Equal(0, after.GetProperty("extraColumns").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/RelationInfo/{id}")).StatusCode);
    }
}
