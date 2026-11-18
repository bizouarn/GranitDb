using GranitDb.Client.Workspace;
using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Diagram;
using GranitDb.Client.Models;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using ApiClient = GranitDb.Client.api.Client;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

/// <summary>Jalon 5 côté client : client HTTP (documentation, étiquettes, domaines, aperçu) et modèle de diagramme par domaine.</summary>
public class ClientGovernanceApiTests
{
    private static async Task<(FakeApi Api, string Body, HttpRequestMessage Request)> RunAsync(
        Func<ApiClient, Task> call, Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var api = new FakeApi { Respond = respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }) };
        await call(api.Client);
        return (api, api.Bodies.Last(), api.Requests.Last());
    }

    [Fact]
    public async Task UpdateDocumentationAsync_puts_the_description_and_notes_and_escapes_the_id()
    {
        var (_, body, request) = await RunAsync(c => c.UpdateDocumentationAsync("TableInfo", "a/b", "Les commandes", null));

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("http://api.test/api/v1/TableInfo/a%2Fb/documentation", request.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Les commandes", json.RootElement.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("metaInfo").ValueKind);
    }

    [Fact]
    public async Task A_validation_problem_shows_the_first_field_message_rather_than_the_generic_title()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => RunAsync(c => c.UpdateDocumentationAsync("TableInfo", "t", "x", null),
            _ => FakeApi.Json("""{"title":"One or more validation errors occurred.","status":400,"errors":{"Description":["Le texte est trop long."]}}""", HttpStatusCode.BadRequest)));

        Assert.Equal("Le texte est trop long.", ex.Message);
    }

    [Fact]
    public async Task A_server_problem_title_is_used_when_there_are_no_field_errors()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => RunAsync(c => c.SetSensitiveAsync("c", true),
            _ => FakeApi.Json("""{"title":"Colonne inconnue","status":404}""", HttpStatusCode.NotFound)));

        Assert.Equal("Colonne inconnue", ex.Message);
    }

    [Fact]
    public async Task SetSensitiveAsync_puts_the_flag()
    {
        var (_, body, request) = await RunAsync(c => c.SetSensitiveAsync("col 1", true));

        Assert.Equal("http://api.test/api/v1/ColumnInfo/col%201/sensitivity", request.RequestUri!.AbsoluteUri);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("isSensitive").GetBoolean());
    }

    [Fact]
    public async Task Tags_are_attached_by_name_and_detached_by_identifier()
    {
        var (api, _, _) = await RunAsync(async c =>
        {
            await c.AttachTagAsync("RGPD", "column", "c1");
            await c.DetachTagAsync("g1", "column", "c1");
        });

        Assert.Equal("http://api.test/api/v1/TagInfo/attach", api.Requests[0].RequestUri!.ToString());
        using var attach = JsonDocument.Parse(api.Bodies[0]);
        Assert.Equal("RGPD", attach.RootElement.GetProperty("name").GetString());
        Assert.Equal("column", attach.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(HttpMethod.Delete, api.Requests[1].Method);
        Assert.Equal("http://api.test/api/v1/TagInfo/g1/links/column/c1", api.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_manual_relation_is_posted_with_the_referenced_column_first()
    {
        var (_, body, request) = await RunAsync(c => c.CreateRelationAsync("referenced", "referencing", "One-to-One"));

        Assert.Equal("http://api.test/api/v1/RelationInfo", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("referenced", json.RootElement.GetProperty("firstId").GetString());
        Assert.Equal("referencing", json.RootElement.GetProperty("secondId").GetString());
        Assert.Equal("One-to-One", json.RootElement.GetProperty("relationType").GetString());
    }

    [Fact]
    public async Task Domain_calls_use_the_domain_endpoints()
    {
        var (api, _, _) = await RunAsync(async c =>
        {
            await c.AddTableToDomainAsync("d1", "t1", moveFrom: "d0", x: 10, y: 20);
            await c.RemoveTableFromDomainAsync("d1", "t1");
            await c.SaveLayoutAsync("d1", [("t1", 5, 6)]);
            await c.DeleteDomainAsync("d1");
        });

        Assert.Equal("http://api.test/api/v1/DomainInfo/d1/tables/t1?moveFrom=d0", api.Requests[0].RequestUri!.ToString());
        using var place = JsonDocument.Parse(api.Bodies[0]);
        Assert.Equal(10, place.RootElement.GetProperty("x").GetDouble());
        Assert.Equal(HttpMethod.Delete, api.Requests[1].Method);
        Assert.Equal("http://api.test/api/v1/DomainInfo/d1/layout", api.Requests[2].RequestUri!.ToString());
        using var layout = JsonDocument.Parse(api.Bodies[2]);
        Assert.Equal("t1", layout.RootElement[0].GetProperty("tableId").GetString());
        Assert.Equal(6, layout.RootElement[0].GetProperty("y").GetDouble());
        Assert.Equal("http://api.test/api/v1/DomainInfo/d1", api.Requests[3].RequestUri!.ToString());
    }

    [Fact]
    public async Task Optional_reads_fall_back_to_nothing_on_errors_and_unexpected_bodies()
    {
        var notFound = new FakeApi();
        var unexpected = new FakeApi { Respond = _ => FakeApi.Json("""{"not":"an array"}""") };

        Assert.Empty(await notFound.Client.GetDomainsAsync("x"));
        Assert.Empty(await notFound.Client.GetTagLinksAsync("x"));
        Assert.Empty(await notFound.Client.GetTagsAsync());
        Assert.Null(await notFound.Client.GetCompletenessAsync("x"));
        Assert.Empty(await unexpected.Client.GetDomainsAsync("x"));
        Assert.Empty(await unexpected.Client.GetTagLinksAsync("x"));
    }

    [Fact]
    public async Task PreviewSyncAsync_reads_the_changes_and_refuses_on_error()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"changes":[{"kind":"table","action":"removed","table":"tags","name":null,"detail":"2 colonne(s)","losesDocumentation":true}],"hasChanges":true,"losesDocumentation":1}""")
        };

        var preview = await api.Client.PreviewSyncAsync("db");

        Assert.Equal("http://api.test/api/v1/DatabaseInfo/db/sync/preview", api.Requests.Single().RequestUri!.ToString());
        Assert.True(preview.HasChanges);
        Assert.True(preview.Changes.Single().LosesDocumentation);
        var refused = new FakeApi { Respond = _ => FakeApi.Json("""{"title":"Aucun fichier source","status":400}""", HttpStatusCode.BadRequest) };
        Assert.Equal("Aucun fichier source", (await Assert.ThrowsAsync<ApiException>(() => refused.Client.PreviewSyncAsync("db"))).Message);
    }
}

public class ErDiagramDomainTests
{
    [Fact]
    public void Frontier_tables_only_show_the_columns_of_their_relations_and_are_flagged()
    {
        var relation = new RelationInfo { Id = "r", FirstId = "c-id", SecondId = "o-cust", RelationType = "One-to-Many" };
        var tables = new[]
        {
            new TableInfo { Id = "c", Name = "customers", Relations = [relation], Columns = [new ColumnInfo { Id = "c-id", Name = "id" }, new ColumnInfo { Id = "c-x", Name = "email", Position = 1 }] },
            new TableInfo { Id = "o", Name = "orders", Relations = [relation], Columns = [new ColumnInfo { Id = "o-id", Name = "id" }, new ColumnInfo { Id = "o-cust", Name = "customer_id", Position = 1 }, new ColumnInfo { Id = "o-n", Name = "note", Position = 2 }] }
        };

        var diagram = ErDiagram.Build(tables, new HashSet<string> { "c", "o" }, new HashSet<string> { "o" });

        Assert.False(diagram.Node("c")!.IsFrontier);
        Assert.Equal(["id", "email"], diagram.Node("c")!.Rows.Select(r => r.Name).ToArray());
        Assert.True(diagram.Node("o")!.IsFrontier);
        Assert.Equal(["customer_id"], diagram.Node("o")!.Rows.Select(r => r.Name).ToArray());
        Assert.Single(diagram.Edges);
        Assert.NotNull(diagram.Route(diagram.Edges[0]));
    }
}

/// <summary>Page d'une base : édition en place, sensibilité, étiquettes, relations manuelles, complétude, aperçu de synchronisation.</summary>
public class ClientDetailGovernanceTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    /// <summary>Remplace la réponse d'une requête (null : laisser la réponse par défaut).</summary>
    private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

    private string _database = """{"id":"db","name":"Boutique","type":"Sqlite","description":"Ma boutique","canResync":true}""";
    private string _preview = """{"changes":[],"hasChanges":false,"losesDocumentation":0}""";

    /// <summary>Le fournisseur de fenêtres : le contenu d'une fenêtre ouverte y est rendu, pas dans la page.</summary>
    private readonly IRenderedComponent<MudBlazor.MudDialogProvider> _dialogs;

    public ClientDetailGovernanceTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        _ctx.Render<MudBlazor.MudPopoverProvider>();
        _dialogs = _ctx.Render<MudBlazor.MudDialogProvider>();
        _api.Respond = Serve;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private const string Relations = """
        [{"id":"r1","firstId":"c0","secondId":"c2","relationType":"One-to-Many","metaInfo":"auto:fk"},
         {"id":"r2","firstId":"c0","secondId":"c3","relationType":"One-to-One"}]
        """;

    private static readonly string Tables = $$"""
        [{"id":"t0","name":"customers","tableType":"Table","description":"Les clients","columns":[
            {"id":"c0","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0},
            {"id":"c1","name":"email","dataType":"TEXT","position":1,"isSensitive":true,"metaInfo":"pii"}],
          "indexes":[],"relations":{{Relations}}},
         {"id":"t1","name":"orders","tableType":"Table","columns":[
            {"id":"c2","name":"customer_id","dataType":"INTEGER","position":0},
            {"id":"c3","name":"reference","dataType":"TEXT","position":1,"description":"Numéro"}],
          "indexes":[],"relations":{{Relations}}}]
        """;

    private const string Completeness = """
        {"databaseId":"db","tables":2,"tablesDescribed":1,"columns":4,"columnsDescribed":1,"sensitiveColumns":1,"sensitiveUndescribed":1,"percent":33,
         "perTable":[{"tableId":"t0","tableName":"customers","tableDescribed":true,"columns":2,"columnsDescribed":0,"sensitiveColumns":1,"sensitiveUndescribed":1,"percent":33},
                     {"tableId":"t1","tableName":"orders","tableDescribed":false,"columns":2,"columnsDescribed":1,"sensitiveColumns":0,"sensitiveUndescribed":0,"percent":85}]}
        """;

    private const string TagLinks = """[{"tagId":"g1","tagName":"RGPD","color":null,"entityType":"table","entityId":"t0"}]""";

    private const string Domains = """[{"id":"d1","databaseId":"db","name":"Ventes","tables":[{"tableId":"t1","x":null,"y":null}]}]""";

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        if (_override(request) is { } custom) return custom;
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get && !path.EndsWith("/sync/preview"))
            return FakeApi.Json("{}");
        return path switch
        {
            _ when path.StartsWith("/api/v1/TableInfo/database/") => FakeApi.Json(Tables),
            _ when path.StartsWith("/api/v1/Completeness/") => FakeApi.Json(Completeness),
            _ when path.StartsWith("/api/v1/TagInfo/database/") => FakeApi.Json(TagLinks),
            _ when path.StartsWith("/api/v1/DomainInfo/database/") => FakeApi.Json(Domains),
            _ when path.EndsWith("/sync/preview") => FakeApi.Json(_preview),
            _ when path.StartsWith("/api/v1/DatabaseInfo/") => FakeApi.Json(_database),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private IRenderedComponent<DatabaseDetail> Open()
    {
        var page = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        page.ExpandAllTables();
        return page;
    }

    private (HttpRequestMessage Request, JsonElement Body) Sent(HttpMethod method, string path)
    {
        var request = _api.Requests.Last(r => r.Method == method && r.RequestUri!.AbsolutePath == path);
        var body = _api.Bodies[_api.Requests.IndexOf(request)];
        return (request, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private Notifier Notices => _ctx.Services.GetRequiredService<Notifier>();

    private bool Sent(HttpMethod method, string path, bool any) =>
        _api.Requests.Any(r => r.Method == method && r.RequestUri!.AbsolutePath == path);

    private static void TypeAndSave(IRenderedComponent<DatabaseDetail> page, string text)
    {
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".inline-edit")));
        page.Find(".inline-edit textarea").Input(text);
        page.Find(".inline-edit textarea").Blur(); // quitter le champ enregistre
    }

    // --- Complétude ---

    [BunitFact]
    public void The_completeness_bar_and_the_undescribed_sensitive_columns_are_shown()
    {
        var page = Open();

        page.WaitForAssertion(() => Assert.Contains("33 %", page.Find("#completeness").TextContent));
        Assert.Contains("1 / 2 table(s) et 1 / 4 colonne(s) décrites", page.Find("#completeness").TextContent);
        Assert.Contains("1 colonne(s) sensible(s) sans description", page.Find(".sensitive-warning").TextContent);
        var chips = page.FindAll(".table-percent").Select(c => c.TextContent.Trim()).ToArray();
        Assert.Equal(["33 %", "85 %"], chips);
    }

    [BunitFact]
    public void Without_completeness_data_the_page_still_shows_the_tables()
    {
        _override = r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/Completeness/") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;

        var page = Open();

        Assert.Empty(page.FindAll("#completeness"));
        Assert.Empty(page.FindAll(".table-percent"));
    }

    // --- Édition en place ---

    [BunitFact]
    public void Editing_the_database_description_saves_it_and_shows_the_new_text()
    {
        var page = Open();

        page.Find(".inline-view").Click();
        TypeAndSave(page, "  Boutique en ligne  ");

        page.WaitForAssertion(() => Assert.Contains("Boutique en ligne", page.Markup));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/DatabaseInfo/db/documentation");
        Assert.Equal("Boutique en ligne", body.GetProperty("description").GetString());
        Assert.Empty(page.FindAll(".inline-edit"));
    }

    [BunitFact]
    public void Clearing_a_description_sends_null()
    {
        var page = Open();

        page.Find(".inline-view").Click();
        TypeAndSave(page, "   ");

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Put, "/api/v1/DatabaseInfo/db/documentation", true)));
        Assert.Equal(JsonValueKind.Null, Sent(HttpMethod.Put, "/api/v1/DatabaseInfo/db/documentation").Body.GetProperty("description").ValueKind);
    }

    [BunitFact]
    public void Escape_cancels_the_edit_without_calling_the_api()
    {
        var page = Open();

        page.Find(".inline-view").Click();
        page.Find(".inline-edit textarea").Input("brouillon");
        page.Find(".inline-edit textarea").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(page.FindAll(".inline-edit"));
        Assert.Contains("Ma boutique", page.Markup);
        Assert.DoesNotContain(_api.Requests, r => r.Method == HttpMethod.Put);
    }

    [BunitFact]
    public void A_refused_edit_shows_the_message_and_keeps_the_field_open()
    {
        _override = r => r.Method == HttpMethod.Put
            ? FakeApi.Json("""{"title":"Le texte est trop long.","status":400}""", HttpStatusCode.BadRequest)
            : null;
        var page = Open();

        page.Find(".inline-view").Click();
        TypeAndSave(page, "x");

        page.WaitForAssertion(() => Assert.Contains("Le texte est trop long.", page.Find(".inline-error").TextContent));
        Assert.NotEmpty(page.FindAll(".inline-edit"));
        Assert.Contains("Ma boutique", _database); // l'ancien texte n'a pas été remplacé à l'écran
    }

    [BunitFact]
    public void Editing_a_table_description_keeps_its_notes_and_the_other_way_round()
    {
        var page = Open();

        page.Find("#t-t0 .inline-view").Click(); // description de la table (« Les clients »)
        TypeAndSave(page, "Clients de la boutique");
        page.WaitForAssertion(() => Assert.Contains("Clients de la boutique", page.Markup));

        var (_, first) = Sent(HttpMethod.Put, "/api/v1/TableInfo/t0/documentation");
        Assert.Equal("Clients de la boutique", first.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("metaInfo").ValueKind);

        page.FindAll("#t-t0 .inline-view")[1].Click(); // notes
        TypeAndSave(page, "propriétaire : ventes");
        page.WaitForAssertion(() => Assert.Contains("propriétaire : ventes", page.Markup));
        var (_, second) = Sent(HttpMethod.Put, "/api/v1/TableInfo/t0/documentation");
        Assert.Equal("Clients de la boutique", second.GetProperty("description").GetString()); // inchangée
        Assert.Equal("propriétaire : ventes", second.GetProperty("metaInfo").GetString());
    }

    [BunitFact]
    public void Editing_a_column_description_keeps_its_notes_and_refreshes_the_completeness()
    {
        var page = Open();
        var before = _api.Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/Completeness/"));

        page.Find("tr[data-column='email'] .inline-view").Click();
        TypeAndSave(page, "Adresse de contact");

        page.WaitForAssertion(() => Assert.Contains("Adresse de contact", page.Markup));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/ColumnInfo/c1/documentation");
        Assert.Equal("Adresse de contact", body.GetProperty("description").GetString());
        Assert.Equal("pii", body.GetProperty("metaInfo").GetString()); // les notes existantes sont renvoyées telles quelles
        Assert.True(_api.Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/Completeness/")) > before);
    }

    [BunitFact]
    public void The_notes_of_a_column_can_be_opened_and_edited()
    {
        var page = Open();
        Assert.Empty(page.FindAll("tr[data-column='reference'] .column-notes"));

        page.Find("tr[data-column='reference'] button.notes-toggle").Click();
        Assert.Single(page.FindAll("tr[data-column='reference'] .column-notes"));
        page.Find("tr[data-column='reference'] .column-notes .inline-view").Click();
        TypeAndSave(page, "interne");

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Put, "/api/v1/ColumnInfo/c3/documentation", true)));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/ColumnInfo/c3/documentation");
        Assert.Equal("Numéro", body.GetProperty("description").GetString());
        Assert.Equal("interne", body.GetProperty("metaInfo").GetString());
    }

    // --- Colonnes sensibles ---

    [BunitFact]
    public void A_sensitive_column_is_marked_and_the_toggle_flips_it()
    {
        var page = Open();
        Assert.Contains("column-sensitive", page.Find("tr[data-column='email']").ClassName);
        Assert.DoesNotContain("column-sensitive", page.Find("tr[data-column='id']").ClassName ?? "");

        page.Find("tr[data-column='id'] button.sensitive-toggle").Click();

        page.WaitForAssertion(() => Assert.Contains("column-sensitive", page.Find("tr[data-column='id']").ClassName));
        Assert.True(Sent(HttpMethod.Put, "/api/v1/ColumnInfo/c0/sensitivity").Body.GetProperty("isSensitive").GetBoolean());
        page.Find("tr[data-column='email'] button.sensitive-toggle").Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("column-sensitive", page.Find("tr[data-column='email']").ClassName ?? ""));
        Assert.False(Sent(HttpMethod.Put, "/api/v1/ColumnInfo/c1/sensitivity").Body.GetProperty("isSensitive").GetBoolean());
    }

    [BunitFact]
    public void A_refused_sensitivity_change_leaves_the_column_unchanged_and_says_why()
    {
        _override = r => r.RequestUri!.AbsolutePath.EndsWith("/sensitivity")
            ? FakeApi.Json("""{"title":"Colonne inconnue","status":404}""", HttpStatusCode.NotFound)
            : null;
        var page = Open();

        page.Find("tr[data-column='id'] button.sensitive-toggle").Click();

        page.WaitForAssertion(() => Assert.Contains("Colonne inconnue", page.Markup));
        Assert.DoesNotContain("column-sensitive", page.Find("tr[data-column='id']").ClassName ?? "");
    }

    // --- Étiquettes ---

    [BunitFact]
    public void Existing_tags_are_shown_on_the_table_and_a_new_one_is_attached_with_enter()
    {
        var page = Open();
        Assert.Contains("RGPD", page.Find("#t-t0").TextContent);

        page.Find("#t-t1 button.tag-add").Click();
        page.Find("#t-t1 .tag-input input").Input("legacy");
        page.Find("#t-t1 .tag-input input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Post, "/api/v1/TagInfo/attach", true)));
        var (_, body) = Sent(HttpMethod.Post, "/api/v1/TagInfo/attach");
        Assert.Equal("legacy", body.GetProperty("name").GetString());
        Assert.Equal("table", body.GetProperty("entityType").GetString());
        Assert.Equal("t1", body.GetProperty("entityId").GetString());
    }

    [BunitFact]
    public void Removing_a_tag_detaches_it()
    {
        var page = Open();

        page.Find("#t-t0 .tag-editor .tag-chip .mud-chip-close-button").Click();

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Delete, "/api/v1/TagInfo/g1/links/table/t0", true)));
    }

    [BunitFact]
    public void A_refused_tag_shows_the_server_message_next_to_the_editor()
    {
        _override = r => r.RequestUri!.AbsolutePath.EndsWith("/attach")
            ? FakeApi.Json("""{"title":"Élément introuvable","status":404}""", HttpStatusCode.NotFound)
            : null;
        var page = Open();

        page.Find("#t-t1 button.tag-add").Click();
        page.Find("#t-t1 .tag-input input").Input("x");
        page.Find("#t-t1 .tag-input input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.Contains("Élément introuvable", page.Find("#t-t1 .tag-error").TextContent));
    }

    // --- Domaines (filtre) ---

    [BunitFact]
    public void Tables_can_be_filtered_by_domain_and_their_domains_are_shown()
    {
        var page = Open();
        Assert.Contains("Ventes", page.Find("#t-t1").TextContent);

        page.Find(".domain-filter select").Change("d1");

        Assert.Single(page.FindAll(".mud-expand-panel"));
        Assert.NotEmpty(page.FindAll("#t-t1"));
        Assert.Empty(page.FindAll("#t-t0"));
    }

    // --- Relations manuelles ---

    [BunitFact]
    public void Only_manual_relations_can_be_deleted()
    {
        var page = Open();

        var auto = page.FindAll("#t-t0 [data-relation='r1']").Single();
        var manual = page.FindAll("#t-t0 [data-relation='r2']").Single();
        Assert.Empty(auto.QuerySelectorAll("button.relation-delete"));
        Assert.Contains("manuelle", manual.TextContent);
        Assert.DoesNotContain("manuelle", auto.TextContent);

        manual.QuerySelector("button.relation-delete")!.Click();

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Delete, "/api/v1/RelationInfo/r2", true)));
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Relation supprimée." && t.Undo != null));
    }

    [BunitFact]
    public void The_type_of_a_detected_relation_can_be_corrected()
    {
        var page = Open();

        page.Find("#t-t0 [data-relation='r1'] button.relation-edit").Click();
        page.Find("#t-t0 [data-relation='r1'] select").Change("One-to-One");

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Put, "/api/v1/RelationInfo/r1", true)));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/RelationInfo/r1");
        Assert.Equal("One-to-One", body.GetProperty("relationType").GetString());
        Assert.Equal("c0", body.GetProperty("firstId").GetString());
        Assert.Equal("c2", body.GetProperty("secondId").GetString());
        Assert.Contains("customers.id → orders.customer_id (One-to-One)", page.Find("#t-t0 [data-relation='r1']").TextContent);
    }

    private void OpenRelationDialog(IRenderedComponent<DatabaseDetail> page)
    {
        page.Find("#t-t1 button.relation-add").Click();
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll(".relation-dialog")));
    }

    private void FillRelation(string sourceColumn = "c3", string targetTable = "t0", string targetColumn = "c1")
    {
        _dialogs.Find(".relation-dialog-target-table select").Change(targetTable);
        _dialogs.Find("[data-pair='0'] .relation-dialog-pair-source select").Change(sourceColumn);
        _dialogs.Find("[data-pair='0'] .relation-dialog-pair-target select").Change(targetColumn);
    }

    [BunitFact]
    public void A_manual_relation_is_added_from_a_dialog_with_a_preview_before_saving()
    {
        var page = Open();

        OpenRelationDialog(page);
        Assert.Equal("t1", _dialogs.Find(".relation-dialog-source-table select").GetAttribute("value")); // on part de la table courante
        Assert.True(_dialogs.Find("button.relation-dialog-save").HasAttribute("disabled"));
        FillRelation();
        _dialogs.Find(".relation-dialog-type select").Change("Many-to-Many");
        Assert.Contains("pointe vers", _dialogs.Find(".relation-dialog-preview").TextContent);
        Assert.False(_dialogs.Find("button.relation-dialog-save").HasAttribute("disabled"));
        _dialogs.Find("button.relation-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Post, "/api/v1/RelationInfo", true)));
        var (_, body) = Sent(HttpMethod.Post, "/api/v1/RelationInfo");
        Assert.Equal("c1", body.GetProperty("firstId").GetString()); // la colonne référencée
        Assert.Equal("c3", body.GetProperty("secondId").GetString()); // la colonne de la table, qui référence
        Assert.Equal("Many-to-Many", body.GetProperty("relationType").GetString());
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Relation ajoutée."));
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".relation-dialog")));
    }

    [BunitFact]
    public void A_manual_relation_is_modified_in_the_same_dialog_and_a_detected_one_has_no_such_button()
    {
        var page = Open();
        Assert.Empty(page.FindAll("#t-t1 [data-relation='r1'] button.relation-modify")); // détectée : colonnes non modifiables

        page.Find("#t-t1 [data-relation='r2'] button.relation-modify").Click();
        _dialogs.WaitForAssertion(() => Assert.NotEmpty(_dialogs.FindAll(".relation-dialog")));
        Assert.Equal("t0", _dialogs.Find(".relation-dialog-target-table select").GetAttribute("value")); // préremplie
        Assert.Equal("c3", _dialogs.Find("[data-pair='0'] .relation-dialog-pair-source select").GetAttribute("value"));
        _dialogs.Find(".relation-dialog-type select").Change("One-to-Many");
        _dialogs.Find(".relation-dialog-description").Input("  Une référence par commande  ");
        _dialogs.Find("button.relation-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Put, "/api/v1/RelationInfo/r2", true)));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/RelationInfo/r2");
        Assert.Equal("One-to-Many", body.GetProperty("relationType").GetString());
        Assert.Equal("Une référence par commande", body.GetProperty("description").GetString());
        Assert.Equal("c0", body.GetProperty("firstId").GetString());
        Assert.Equal(0, body.GetProperty("extraColumns").GetArrayLength()); // liste envoyée : la relation est remplacée en entier
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Relation modifiée."));
    }

    [BunitFact]
    public void A_composite_relation_takes_several_pairs_and_sends_the_extra_ones()
    {
        var page = Open();
        OpenRelationDialog(page);
        FillRelation();

        _dialogs.Find("button.relation-dialog-add-pair").Click();
        _dialogs.Find("[data-pair='1'] .relation-dialog-pair-source select").Change("c2");
        _dialogs.Find("[data-pair='1'] .relation-dialog-pair-target select").Change("c0");
        Assert.Contains("(", _dialogs.Find(".relation-dialog-preview").TextContent);
        _dialogs.Find("button.relation-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(Sent(HttpMethod.Post, "/api/v1/RelationInfo", true)));
        var extra = Sent(HttpMethod.Post, "/api/v1/RelationInfo").Item2.GetProperty("extraColumns").EnumerateArray().Single();
        Assert.Equal("c0", extra.GetProperty("firstId").GetString());
        Assert.Equal("c2", extra.GetProperty("secondId").GetString());
    }

    [BunitFact]
    public void The_dialog_warns_about_a_duplicate_before_anything_is_sent()
    {
        var page = Open();
        OpenRelationDialog(page);

        FillRelation(sourceColumn: "c2", targetTable: "t0", targetColumn: "c0"); // r1 relie déjà customers.id à orders.customer_id

        Assert.Contains("déjà reliées", _dialogs.Find(".relation-dialog-problem").TextContent);
        Assert.True(_dialogs.Find("button.relation-dialog-save").HasAttribute("disabled"));
        Assert.False(Sent(HttpMethod.Post, "/api/v1/RelationInfo", true));
    }

    [BunitFact]
    public void A_refused_relation_shows_the_server_message_and_keeps_the_dialog_open()
    {
        _override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/RelationInfo")
            ? FakeApi.Json("""{"title":"Relation refusée par le serveur.","status":400}""", HttpStatusCode.BadRequest)
            : null;
        var page = Open();
        OpenRelationDialog(page);
        FillRelation();

        _dialogs.Find("button.relation-dialog-save").Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("Relation refusée par le serveur.", _dialogs.Find(".relation-dialog-error").TextContent));
        Assert.NotEmpty(_dialogs.FindAll(".relation-dialog"));
    }

    [BunitFact]
    public void Escape_style_cancel_closes_the_dialog_without_saving()
    {
        var page = Open();
        OpenRelationDialog(page);
        FillRelation();

        _dialogs.Find("button.relation-dialog-cancel").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".relation-dialog")));
        Assert.False(Sent(HttpMethod.Post, "/api/v1/RelationInfo", true));
    }

    // --- Aperçu de synchronisation ---

    [BunitFact]
    public void Resynchronize_shows_the_preview_with_a_warning_for_losses_and_applies_only_on_confirmation()
    {
        _preview = """
            {"changes":[{"kind":"column","action":"removed","table":"customers","name":"nickname","detail":"TEXT","losesDocumentation":true},
                        {"kind":"column","action":"added","table":"customers","name":"phone","detail":"TEXT","losesDocumentation":false},
                        {"kind":"relation","action":"added","table":"invoices","name":"invoices.order_id → orders.id","detail":"clé étrangère","losesDocumentation":false}],
             "hasChanges":true,"losesDocumentation":1}
            """;
        var page = Open();

        page.FindAll("button").Single(b => b.TextContent.Contains("Resynchroniser")).Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#sync-preview")));
        var preview = page.Find("#sync-preview");
        Assert.Contains("2 ajout(s)", preview.TextContent);
        Assert.Contains("1 suppression(s)", preview.TextContent);
        Assert.Contains("1 suppression(s) feront perdre une documentation", page.Find(".loses-documentation").TextContent);
        Assert.Contains("customers.nickname", preview.TextContent);
        Assert.Contains("invoices.order_id → orders.id", preview.TextContent); // une relation s'affiche par son libellé
        Assert.Single(page.FindAll("tr.change-removed"));
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/sync"));
    }

    [BunitFact]
    public void Cancelling_the_preview_applies_nothing()
    {
        _preview = """{"changes":[{"kind":"table","action":"removed","table":"tags","name":null,"detail":"2 colonne(s)","losesDocumentation":false}],"hasChanges":true,"losesDocumentation":0}""";
        var page = Open();
        page.FindAll("button").Single(b => b.TextContent.Contains("Resynchroniser")).Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#sync-preview")));

        page.FindAll("#sync-preview button").Single(b => b.TextContent.Trim() == "Annuler").Click();

        Assert.Empty(page.FindAll("#sync-preview"));
        Assert.DoesNotContain(_api.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/sync"));
        Assert.Empty(page.FindAll(".loses-documentation"));
    }

    [BunitFact]
    public void An_up_to_date_source_says_so_and_offers_nothing_to_apply()
    {
        var page = Open();

        page.FindAll("button").Single(b => b.TextContent.Contains("Resynchroniser")).Click();

        page.WaitForAssertion(() => Assert.Contains("Le catalogue est déjà à jour", page.Find("#sync-preview").TextContent));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Appliquer la synchronisation"));
    }

    [BunitFact]
    public void A_refused_preview_shows_the_server_message()
    {
        _override = r => r.RequestUri!.AbsolutePath.EndsWith("/sync/preview")
            ? FakeApi.Json("""{"title":"Aucun fichier source n'est conservé pour cette base","status":400}""", HttpStatusCode.BadRequest)
            : null;
        var page = Open();

        page.FindAll("button").Single(b => b.TextContent.Contains("Resynchroniser")).Click();

        page.WaitForAssertion(() => Assert.Contains("Aucun fichier source", page.Markup));
        Assert.Empty(page.FindAll("#sync-preview"));
    }
}
