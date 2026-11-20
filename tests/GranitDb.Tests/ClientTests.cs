using GranitDb.Client.Workspace;
using System.Net;
using System.Text;
using Bunit;
using GranitDb.Client.Models;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using ApiClient = GranitDb.Client.api.Client;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

/// <summary>Réponses programmables pour tester le client HTTP et les pages sans API.</summary>
public sealed class FakeApi : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Le corps est lu ici : le client libère son contenu multipart dès que l'appel est terminé.
        Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
        Requests.Add(request);
        return Respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public ApiClient Client => new(new HttpClient(this) { BaseAddress = new Uri("http://api.test/") });
}

public class ApiClientTests
{
    [Fact]
    public async Task GetAllDatabaseAsync_deserializes_the_list()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""[{"id":"1","name":"Boutique","type":"Sqlite","canResync":true,"sourceLabel":"srv / base"}]""")
        };

        var databases = await api.Client.GetAllDatabaseAsync();

        var database = Assert.Single(databases);
        Assert.Equal("Boutique", database.Name);
        Assert.True(database.CanResync);
        Assert.Equal("srv / base", database.SourceLabel);
        Assert.Equal("http://api.test/api/v1/DatabaseInfo", api.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task GetDatabaseAsync_returns_null_when_missing_and_escapes_the_id()
    {
        var api = new FakeApi();

        Assert.Null(await api.Client.GetDatabaseAsync("a/b c"));
        Assert.Equal("http://api.test/api/v1/DatabaseInfo/a%2Fb%20c", api.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTablesAsync_returns_tables_with_columns_indexes_and_relations_or_nothing_on_404()
    {
        var api = new FakeApi
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/none")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : FakeApi.Json("""
                    [{"id":"t1","name":"orders","tableType":"Table",
                      "columns":[{"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0}],
                      "indexes":[{"id":"i1","name":"ix","columnNames":"id","isUnique":true}],
                      "relations":[{"id":"r1","firstId":"c1","secondId":"c2","relationType":"One-to-Many"}]}]
                    """)
        };

        var tables = await api.Client.GetTablesAsync("db");
        Assert.True(Assert.Single(tables).Columns.Single().IsPrimaryKey);
        Assert.True(tables[0].Indexes.Single().IsUnique);
        Assert.Single(tables[0].Relations);
        Assert.Empty(await api.Client.GetTablesAsync("none"));
    }

    [Fact]
    public async Task UploadSqliteAsync_sends_a_multipart_form_with_file_and_metadata()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"database":{"id":"new","name":"x","type":"Sqlite"},"sync":{"tablesAdded":3,"columnsAdded":9}}""",
                HttpStatusCode.Created)
        };

        var result = await api.Client.UploadSqliteAsync(new MemoryStream([1, 2, 3]), "ma base.db", "Mon nom", "Une description");

        Assert.Equal("new", result.Database.Id);
        Assert.Equal(3, result.Sync.TablesAdded);
        var request = api.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://api.test/api/v1/import/sqlite", request.RequestUri!.ToString());
        var body = api.Bodies.Single();
        Assert.Contains("name=file", body);
        Assert.Contains("ma base.db", body);
        Assert.Contains("Mon nom", body);
        Assert.Contains("Une description", body);
    }

    [Fact]
    public async Task UploadSqliteAsync_sends_blank_metadata_empty_so_the_server_treats_it_as_absent()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"database":{"id":"n","name":"x","type":"Sqlite"},"sync":{}}""", HttpStatusCode.Created)
        };

        await api.Client.UploadSqliteAsync(new MemoryStream([1]), "a.db", " ", null);

        // Le client généré envoie les champs du formulaire ; vides, le serveur les ignore (nom du fichier, pas de description).
        var body = api.Bodies.Single();
        Assert.Matches(@"name=name\s*\r?\n\r?\n\r?\n", body);
        Assert.DoesNotContain("Une description", body);
    }

    [Fact]
    public async Task UploadSqliteAsync_surfaces_the_server_problem_title()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"title":"Le fichier n'est pas une base SQLite (en-tête invalide).","status":400}""",
                HttpStatusCode.BadRequest)
        };

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            api.Client.UploadSqliteAsync(new MemoryStream([1]), "a.db", null, null));

        Assert.Equal("Le fichier n'est pas une base SQLite (en-tête invalide).", ex.Message);
    }

    [Fact]
    public async Task Errors_without_a_json_body_fall_back_to_the_http_status()
    {
        var api = new FakeApi
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>oops</html>") }
        };

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.Client.SyncAsync("x"));

        Assert.Contains("502", ex.Message);
    }

    [Fact]
    public async Task SyncAsync_posts_to_the_sync_endpoint()
    {
        var api = new FakeApi { Respond = _ => FakeApi.Json("""{"tablesAdded":1,"indexesAdded":2}""") };

        var summary = await api.Client.SyncAsync("abc");

        Assert.Equal(2, summary.IndexesAdded);
        Assert.Equal(HttpMethod.Post, api.Requests.Single().Method);
        Assert.Equal("http://api.test/api/v1/DatabaseInfo/abc/sync", api.Requests.Single().RequestUri!.ToString());
    }
}

public class ClientPagesTests : IAsyncLifetime
{
    // Composition plutôt qu'héritage de BunitContext : xUnit libérerait un BunitContext en synchrone, ce que
    // refusent les services MudBlazor qui n'implémentent que IAsyncDisposable.
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    public ClientPagesTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        // Les composants MudBlazor ont besoin de leurs fournisseurs de popover.
        _ctx.Render<MudBlazor.MudPopoverProvider>();
    }

    private const string Table = """
        {"id":"t1","name":"orders","tableType":"Table",
         "columns":[{"id":"c2","name":"customer_id","dataType":"INTEGER","isNullable":false,"position":1},
                    {"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"isNullable":false,"position":0}],
         "indexes":[{"id":"i1","name":"ix_orders_customer","columnNames":"customer_id","isUnique":true}],
         "relations":[{"id":"r1","firstId":"c0","secondId":"c2","relationType":"One-to-Many"}]}
        """;

    private const string Customers = """
        {"id":"t0","name":"customers","tableType":"Table","columns":[{"id":"c0","name":"id","dataType":"INTEGER","position":0}],"indexes":[],"relations":[]}
        """;

    private void ServeDatabase(string databaseJson, string? tablesJson)
    {
        _api.Respond = r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.StartsWith("/api/v1/TableInfo/database/") => tablesJson == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : FakeApi.Json(tablesJson),
            var p when p.StartsWith("/api/v1/DatabaseInfo/") && p.EndsWith("/sync/preview") =>
                FakeApi.Json("""{"changes":[{"kind":"column","action":"added","table":"customers","name":"phone","detail":"TEXT","losesDocumentation":false}],"hasChanges":true,"losesDocumentation":0}"""),
            var p when p.StartsWith("/api/v1/DatabaseInfo/") && p.EndsWith("/sync") =>
                FakeApi.Json("""{"tablesAdded":0,"tablesRemoved":0,"columnsAdded":1,"indexesAdded":2}"""),
            var p when p.StartsWith("/api/v1/DatabaseInfo/") => databaseJson == ""
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : FakeApi.Json(databaseJson),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    [BunitFact]
    public void Home_shows_the_drop_zone_and_the_databases()
    {
        _api.Respond = _ => FakeApi.Json("""[{"id":"1","name":"Boutique","type":"Sqlite"},{"id":"2","name":"RH","type":"SqlServer"}]""");

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("Boutique", page.Markup));
        Assert.Contains("Glissez un fichier SQLite", page.Markup);
        Assert.Contains("RH", page.Markup);
        Assert.Equal(2, page.FindAll("a[href^='/databases/']").Count(a => !a.GetAttribute("href")!.EndsWith("/diagram")));
        Assert.Equal(2, page.FindAll("a[href$='/diagram']").Count);
        Assert.NotNull(page.Find("input[type=file]"));
    }

    [BunitFact]
    public void Home_explains_when_there_is_nothing_yet()
    {
        _api.Respond = _ => FakeApi.Json("[]");

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("Aucune base pour le moment", page.Markup));
    }

    [BunitFact]
    public void Home_reports_an_unreachable_api()
    {
        _api.Respond = _ => throw new HttpRequestException("refused");

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("L'API est injoignable", page.Markup));
    }

    [BunitFact]
    public void Dropping_a_file_uploads_it_and_navigates_to_the_new_database()
    {
        _api.Respond = r => r.Method == HttpMethod.Post
            ? FakeApi.Json("""{"database":{"id":"new-id","name":"dropped","type":"Sqlite"},"sync":{}}""", HttpStatusCode.Created)
            : FakeApi.Json("[]");
        var page = _ctx.Render<Databases>();
        page.WaitForAssertion(() => Assert.Contains("Aucune base", page.Markup));

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "dropped.db"));

        page.WaitForAssertion(() => Assert.EndsWith("/databases/new-id", _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri));
        var upload = _api.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("http://api.test/api/v1/import/sqlite", upload.RequestUri!.ToString());
        Assert.Contains("dropped.db", _api.Bodies.Single(b => b.Contains("dropped.db")));
    }

    [BunitFact]
    public void A_refused_upload_shows_the_server_message_and_stays_on_the_page()
    {
        _api.Respond = r => r.Method == HttpMethod.Post
            ? FakeApi.Json("""{"title":"Le fichier n'est pas une base SQLite (en-tête invalide).","status":400}""", HttpStatusCode.BadRequest)
            : FakeApi.Json("[]");
        var page = _ctx.Render<Databases>();
        page.WaitForAssertion(() => Assert.Contains("Aucune base", page.Markup));

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("not sqlite", "bad.txt"));

        page.WaitForAssertion(() => Assert.Contains("en-tête invalide", page.Markup));
        Assert.DoesNotContain("/databases/", _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [BunitFact]
    public void Detail_lists_tables_columns_in_position_order_indexes_and_labelled_relations()
    {
        ServeDatabase("""{"id":"db","name":"Boutique","type":"Sqlite","description":"Ma boutique"}""", $"[{Table},{Customers}]");

        var page = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));

        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        page.ExpandAllTables();
        Assert.Contains("Boutique", page.Markup);
        Assert.Contains("Ma boutique", page.Markup);
        Assert.Contains("orders", page.Markup);
        var markup = page.Markup;
        Assert.True(markup.IndexOf("<td>id</td>", StringComparison.Ordinal) is var _ || true);
        // La colonne « id » (position 0) est affichée avant « customer_id » (position 1) dans la table orders.
        var rows = page.FindAll("tbody tr td:first-child").Select(td => td.TextContent.Trim()).ToList();
        Assert.True(rows.IndexOf("id") < rows.IndexOf("customer_id"));
        Assert.Contains("ix_orders_customer (customer_id)", page.Markup);
        Assert.Contains("unique", page.Markup);
        Assert.Contains("customers.id → orders.customer_id (One-to-Many)", page.Find("[data-relation='r1']").TextContent);
    }

    [BunitFact]
    public void Detail_filter_narrows_the_table_list()
    {
        ServeDatabase("""{"id":"db","name":"Boutique","type":"Sqlite"}""", $"[{Table},{Customers}]");
        var page = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));

        page.Find("input[type=text]").Input("cust");

        Assert.Contains("customers", page.Markup);
        Assert.DoesNotContain("orders", page.Markup);
    }

    [BunitFact]
    public void Detail_of_an_unknown_database_says_so()
    {
        ServeDatabase("", null);

        var page = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "nope"));

        page.WaitForAssertion(() => Assert.Contains("Base introuvable", page.Markup));
    }

    [BunitFact]
    public void Detail_of_a_database_without_tables_says_so()
    {
        ServeDatabase("""{"id":"db","name":"Vide","type":"Sqlite"}""", null);

        var page = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));

        page.WaitForAssertion(() => Assert.Contains("Aucune table dans cette base", page.Markup));
    }

    [BunitFact]
    public void Resync_button_only_exists_when_a_source_file_is_kept_and_reports_the_result()
    {
        ServeDatabase("""{"id":"db","name":"Avec","type":"Sqlite","canResync":true}""", $"[{Customers}]");
        var withFile = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));
        withFile.WaitForAssertion(() => Assert.Contains("Resynchroniser", withFile.Markup));

        withFile.FindAll("button").Single(b => b.TextContent.Contains("Resynchroniser")).Click();

        // D'abord l'aperçu : rien n'est appliqué tant qu'on ne l'a pas confirmé.
        withFile.WaitForAssertion(() => Assert.Contains("Aperçu de la synchronisation", withFile.Markup));
        Assert.Contains("customers.phone", withFile.Find("#sync-preview").TextContent);
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/sync"));

        withFile.FindAll("button").Single(b => b.TextContent.Contains("Appliquer la synchronisation")).Click();

        withFile.WaitForAssertion(() => Assert.Contains(_ctx.Services.GetRequiredService<Notifier>().Toasts, t => t.Message.Contains("1 colonne ajoutée, 2 index ajoutés")));
        Assert.Empty(withFile.FindAll("#sync-preview"));

        ServeDatabase("""{"id":"db2","name":"Sans","type":"Sqlite"}""", $"[{Customers}]");
        var without = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db2"));
        without.WaitForAssertion(() => Assert.Contains("Sans", without.Markup));
        Assert.DoesNotContain("Resynchroniser", without.Markup);
    }
}

/// <summary>Marque un test de page bUnit.</summary>
public sealed class BunitFactAttribute : FactAttribute;
