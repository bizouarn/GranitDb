using GranitDb.Client.Models;
using Microsoft.Data.Sqlite;
using ApiClient = GranitDb.Client.api.Client;

namespace GranitDb.Tests;

/// <summary>
///     Le <c>Client</c> de l'interface (bâti sur le client généré) face à la vraie API : chaque écriture et chaque lecture
///     utilisées par les pages, avec les vrais codes de réponse (201, 204…) que les faux serveurs des autres tests simulent.
/// </summary>
public sealed class ClientAgainstApiTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _http;
    private readonly ApiClient _client;

    public ClientAgainstApiTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
        });
        _http = _factory.CreateKestrelClient();
        _client = new ApiClient(_http);
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private async Task<string> ImportAsync()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT); CREATE TABLE b (id INTEGER PRIMARY KEY, a_ref INTEGER, other TEXT);");
        await using var file = File.OpenRead(path);
        var imported = await _client.UploadSqliteAsync(file, "demo.db", "Demo", null);
        return imported.Database.Id;
    }

    [Fact]
    public async Task Reading_a_database_gives_typed_tables_columns_and_pages()
    {
        var db = await ImportAsync();

        Assert.Equal(db, Assert.Single(await _client.GetAllDatabaseAsync()).Id);
        var page = await _client.GetDatabasesPageAsync(0, 10);
        Assert.Equal(1, page.Total);
        Assert.Null((await _client.GetDatabaseAsync(db))!.Description); // description vide : stockée comme absente
        Assert.Null(await _client.GetDatabaseAsync("inconnue"));
        var summary = await _client.GetTableSummariesAsync(db);
        Assert.Equal(2, summary.Length);
        Assert.All(summary, t => Assert.Empty(t.Columns));
        var a = summary.Single(t => t.Name == "a");
        Assert.Equal(["id", "name"], (await _client.GetColumnsAsync(a.Id)).Select(c => c.Name).ToArray());
        Assert.Equal(5, (await _client.GetTablesAsync(db)).Sum(t => t.Columns.Count));
        Assert.NotEmpty(await _client.SearchAsync("demo"));
        Assert.False((await _client.GetCapabilitiesAsync()).AllowLocalPaths);
        Assert.NotNull(await _client.GetCompletenessAsync(db));
    }

    [Fact]
    public async Task Writes_documentation_sensitivity_tags_and_relations_round_trip()
    {
        var db = await ImportAsync();
        var tables = await _client.GetTablesAsync(db);
        var a = tables.Single(t => t.Name == "a");
        var b = tables.Single(t => t.Name == "b");

        await _client.UpdateDocumentationAsync("TableInfo", a.Id, "Table A", null);
        await _client.UpdateDocumentationAsync("DatabaseInfo", db, "Ma base", null);
        await _client.SetSensitiveAsync(a.Columns.Single(c => c.Name == "name").Id, true);
        await _client.AttachTagAsync("RGPD", "table", a.Id);
        var links = await _client.GetTagLinksAsync(db);
        var link = Assert.Single(links);
        Assert.Equal("RGPD", link.TagName);
        await _client.UpdateTagAsync((await _client.GetTagsAsync()).Single(), "PII", "#ff0000");
        await _client.DetachTagAsync(link.TagId, "table", a.Id);
        await _client.DeleteTagAsync(link.TagId);
        Assert.Empty(await _client.GetTagsAsync());

        await _client.CreateRelationAsync(a.Columns.Single(c => c.Name == "id").Id, b.Columns.Single(c => c.Name == "a_ref").Id, "One-to-Many", "A vers B");
        var relation = (await _client.GetTablesAsync(db)).Single(t => t.Name == "a").Relations.Single();
        await _client.UpdateRelationTypeAsync(relation, "One-to-One");
        await _client.UpdateRelationAsync(relation.Id, relation.FirstId, relation.SecondId, "One-to-One", "Maj", null, []);
        Assert.Equal("One-to-One", (await _client.GetTablesAsync(db)).Single(t => t.Name == "a").Relations.Single().RelationType);
        Assert.NotNull(await _client.GetImpactAsync("table", a.Id));
        Assert.Null(await _client.GetImpactAsync("table", "inconnue"));
        await _client.DeleteRelationAsync(relation.Id);

        Assert.Equal("Table A", (await _client.GetTablesAsync(db)).Single(t => t.Name == "a").Description);
        Assert.Equal("Ma base", (await _client.GetDatabaseAsync(db))!.Description);
    }

    [Fact]
    public async Task Domains_are_created_filled_laid_out_configured_and_deleted()
    {
        var db = await ImportAsync();
        var tables = await _client.GetTablesAsync(db);
        var a = tables.Single(t => t.Name == "a");
        var b = tables.Single(t => t.Name == "b");

        var domain = await _client.CreateDomainAsync(db, "Ventes", "#00aa00");
        await _client.AddTableToDomainAsync(domain.Id, a.Id, x: 10, y: 20);
        await _client.AddTableToDomainAsync(domain.Id, b.Id);
        await _client.SaveLayoutAsync(domain.Id, [(a.Id, 1, 2), (b.Id, 3, 4)]);
        await _client.SaveDomainSettingsAsync(domain.Id, showNeighbors: true);
        await _client.RenameDomainAsync(domain, "Commerce");
        var read = Assert.Single(await _client.GetDomainsAsync(db));
        Assert.Equal("Commerce", read.Name);
        Assert.True(read.ShowNeighbors);
        Assert.Equal(2, read.Tables.Count);
        Assert.Equal(1, read.Tables.Single(t => t.TableId == a.Id).X);
        await _client.RemoveTableFromDomainAsync(domain.Id, b.Id);
        await _client.DeleteDomainAsync(domain.Id);
        Assert.Empty(await _client.GetDomainsAsync(db));
    }

    [Fact]
    public async Task Sync_preview_replacement_exports_audit_and_deletion_work_with_real_status_codes()
    {
        var db = await ImportAsync();

        Assert.Equal(0, (await _client.SyncAsync(db)).TablesAdded);
        Assert.False((await _client.PreviewSyncAsync(db)).HasChanges);
        var replacement = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(replacement, "CREATE TABLE a (id INTEGER PRIMARY KEY, name TEXT); CREATE TABLE c (id INTEGER PRIMARY KEY);");
        var bytes = await File.ReadAllBytesAsync(replacement);
        Assert.True((await _client.PreviewReplaceSqliteAsync(db, bytes, "new.db")).HasChanges);
        Assert.Equal(1, (await _client.ReplaceSqliteAsync(db, bytes, "new.db")).Sync.TablesAdded);

        Assert.Contains("Demo", await _client.ExportAsync(db, "markdown"));
        Assert.StartsWith("UEs", await _client.ExportXlsxBase64Async(db)); // « PK » : début d'un zip
        var audit = await _client.GetAuditAsync(new AuditFilter(Method: "POST"), 0, 5);
        Assert.NotEmpty(audit.Items);
        Assert.True(audit.Total >= audit.Items.Length);

        await _client.DeleteDatabaseAsync(db);
        Assert.Empty(await _client.GetAllDatabaseAsync());
        var error = await Assert.ThrowsAsync<GranitDb.Client.api.ApiException>(() => _client.SyncAsync(db));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Webhooks_are_saved_listed_and_deleted_and_a_refusal_reads_as_a_message()
    {
        await _client.SaveWebhookAsync(null, new WebhookForm { Url = "https://example.org/hook", Events = ["schema.changed"] });
        var hook = Assert.Single(await _client.GetWebhooksAsync());
        await _client.SaveWebhookAsync(hook.Id, new WebhookForm { Url = "https://example.org/other", Events = ["database.deleted"], IsActive = false });
        Assert.Equal("https://example.org/other", Assert.Single(await _client.GetWebhooksAsync()).Url);
        Assert.Empty(await _client.GetWebhookDeliveriesAsync(hook.Id));

        var refused = await Assert.ThrowsAsync<GranitDb.Client.api.ApiException>(() =>
            _client.SaveWebhookAsync(null, new WebhookForm { Url = "http://127.0.0.1/x", Events = ["schema.changed"] }));
        Assert.DoesNotContain("Erreur 4", refused.Message); // le message du serveur, pas un simple code

        await _client.DeleteWebhookAsync(hook.Id);
        Assert.Empty(await _client.GetWebhooksAsync());
    }
}
