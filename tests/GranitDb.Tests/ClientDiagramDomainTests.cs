using GranitDb.Client.Workspace;
using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace GranitDb.Tests;

/// <summary>Diagramme par domaine : regroupement de tables, tables frontière, places mémorisées, glisser-déposer.</summary>
public class ClientDiagramDomainTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();
    private Func<HttpRequestMessage, HttpResponseMessage?> _override = _ => null;

    public ClientDiagramDomainTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        _popovers = _ctx.Render<MudBlazor.MudPopoverProvider>();
        _api.Respond = Serve;
    }

    private IRenderedComponent<MudBlazor.MudPopoverProvider> _popovers = null!;

    /// <summary>Ouvre le menu « Exporter » et choisit l'entrée de classe donnée.</summary>
    private void Export(IRenderedComponent<Diagram> page, string item)
    {
        page.Find(".export-menu button").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(_popovers.FindAll("." + item)));
        _popovers.Find("." + item).Click();
        _popovers.WaitForAssertion(() => Assert.Empty(_popovers.FindAll("." + item)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private const string R1 = """{"id":"r1","firstId":"c1","secondId":"c2b","relationType":"One-to-Many"}""";
    private const string R2 = """{"id":"r2","firstId":"c2","secondId":"c4b","relationType":"One-to-Many"}""";

    // customers ← orders ← invoices, plus tags (sans relation).
    private static readonly string Tables = $$"""
        [{"id":"t1","name":"customers","tableType":"Table","columns":[{"id":"c1","name":"id","dataType":"INTEGER","position":0},{"id":"c1b","name":"email","dataType":"TEXT","position":1}],"indexes":[],"relations":[{{R1}}]},
         {"id":"t2","name":"orders","tableType":"Table","columns":[{"id":"c2","name":"id","dataType":"INTEGER","position":0},{"id":"c2b","name":"customer_id","dataType":"INTEGER","position":1}],"indexes":[],"relations":[{{R1}},{{R2}}]},
         {"id":"t3","name":"tags","tableType":"Table","columns":[{"id":"c3","name":"label","dataType":"TEXT","position":0}],"indexes":[],"relations":[]},
         {"id":"t4","name":"invoices","tableType":"Table","columns":[{"id":"c4","name":"id","dataType":"INTEGER","position":0},{"id":"c4b","name":"order_id","dataType":"INTEGER","position":1},{"id":"c4c","name":"total","dataType":"REAL","position":2}],"indexes":[],"relations":[{{R2}}]}]
        """;

    private string _domains = """
        [{"id":"d1","databaseId":"db","name":"Ventes","showNeighbors":true,"tables":[{"tableId":"t2","x":300,"y":50}]},
         {"id":"d2","databaseId":"db","name":"Facturation","color":"#00aa00","tables":[{"tableId":"t4","x":null,"y":null}]}]
        """;

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        if (_override(request) is { } custom) return custom;
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get) return FakeApi.Json("{}");
        return path switch
        {
            _ when path.StartsWith("/api/v1/TableInfo/database/") => FakeApi.Json(Tables),
            _ when path.StartsWith("/api/v1/DomainInfo/database/") => FakeApi.Json(_domains),
            _ when path.StartsWith("/api/v1/Completeness/") => FakeApi.Json(
                """{"databaseId":"db","tables":1,"tablesDescribed":1,"columns":2,"columnsDescribed":0,"sensitiveColumns":0,"sensitiveUndescribed":0,"percent":50,"perTable":[]}"""),
            "/api/v1/Impact/table/t2" => FakeApi.Json(
                """{"root":{"tableId":"t2","tableName":"orders","columnId":null,"columnName":null,"depth":0},"dependents":[],"dependencies":[]}"""),
            _ when path.StartsWith("/api/v1/DatabaseInfo/") => FakeApi.Json("""{"id":"db","name":"Boutique","type":"Sqlite"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private IRenderedComponent<Diagram> Open()
    {
        var page = _ctx.Render<Diagram>(p => p.Add(x => x.Id, "db"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("g.er-node")));
        return page;
    }

    private static string[] DomainNames(IRenderedComponent<Diagram> page) =>
        page.FindAll("[data-domain]").Select(n => n.GetAttribute("data-domain")!).ToArray();

    [BunitFact]
    public void Domains_can_be_searched_by_name_and_the_search_says_when_nothing_matches()
    {
        var page = Open();
        Assert.Equal(["Ventes", "Facturation"], DomainNames(page));

        page.Find(".domain-search input").Input("fact");

        Assert.Equal(["Facturation"], DomainNames(page));
        Assert.NotEmpty(page.FindAll(".domain-all")); // « Toutes les tables » reste toujours accessible
        page.Find(".domain-search input").Input("zzz");
        Assert.Empty(DomainNames(page));
        Assert.Contains("Aucun domaine ne contient « zzz »", page.Find(".domain-search-empty").TextContent);
        page.Find(".domain-search input").Input("");
        Assert.Equal(["Ventes", "Facturation"], DomainNames(page));
    }

    [BunitFact]
    public void The_domains_panel_folds_to_its_title_to_leave_room_for_the_table_list()
    {
        var page = Open();
        Assert.Equal("true", page.Find("button.domains-toggle").GetAttribute("aria-expanded"));
        Assert.Contains("45vh", page.Find(".table-picker").GetAttribute("style"));

        page.Find("button.domains-toggle").Click();

        Assert.Equal("false", page.Find("button.domains-toggle").GetAttribute("aria-expanded"));
        Assert.Empty(page.FindAll("[data-domain]"));
        Assert.Empty(page.FindAll(".domain-search"));
        Assert.Contains("Toutes les tables", page.Find(".domains-current").TextContent); // le domaine courant reste indiqué
        Assert.Contains("72vh", page.Find(".table-picker").GetAttribute("style"));
        Assert.NotEmpty(page.FindAll("[data-table-item]")); // la liste des tables est toujours là
        page.Find("button.domains-toggle").Click();
        Assert.Equal(["Ventes", "Facturation"], DomainNames(page));
    }

    private static string[] Shown(IRenderedComponent<Diagram> page) =>
        page.FindAll("g.er-node").Select(n => n.GetAttribute("data-table")!).Order().ToArray();

    private static void SelectDomain(IRenderedComponent<Diagram> page, string name)
    {
        page.Find($"[data-domain='{name}']").Click();
        page.WaitForAssertion(() => Assert.Contains(name, page.Find("h4, .mud-typography-h4").TextContent));
    }

    private static void Check(IRenderedComponent<Diagram> page, string table, bool on)
    {
        page.FindAll("label").First(l => l.TextContent.Trim() == table).QuerySelector("input")!.Change(on);
    }

    private Notifier Notices => _ctx.Services.GetRequiredService<Notifier>();

    private (HttpRequestMessage Request, JsonElement Body) Sent(HttpMethod method, string pathAndQuery)
    {
        var request = _api.Requests.Last(r => r.Method == method && r.RequestUri!.PathAndQuery == pathAndQuery);
        var body = _api.Bodies[_api.Requests.IndexOf(request)];
        return (request, body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private bool WasSent(HttpMethod method, string pathAndQuery) =>
        _api.Requests.Any(r => r.Method == method && r.RequestUri!.PathAndQuery == pathAndQuery);

    // --- Liste des domaines et vue d'un domaine ---

    [BunitFact]
    public void Domains_are_listed_with_their_table_count_and_all_tables_are_shown_by_default()
    {
        var page = Open();

        Assert.Equal(["customers", "invoices", "orders", "tags"], Shown(page));
        Assert.Contains("domain-selected", page.Find(".domain-all").ClassName);
        Assert.Contains("1", page.Find("[data-domain='Ventes']").TextContent);
        Assert.Contains("#00aa00", page.Find("[data-domain='Facturation']").GetAttribute("style"));
        Assert.Contains("Tables affichées (4 / 4)", page.Markup);
    }

    [BunitFact]
    public void A_domain_shows_its_tables_and_the_related_tables_of_other_domains_as_reduced_frontier_tables()
    {
        var page = Open();

        SelectDomain(page, "Ventes");

        Assert.Equal(["customers", "invoices", "orders"], Shown(page)); // tags : sans lien avec le domaine, absent
        Assert.DoesNotContain("er-frontier", page.Find("g.er-node[data-table='orders']").ClassName);
        Assert.Contains("er-frontier", page.Find("g.er-node[data-table='customers']").ClassName);
        Assert.Contains("er-frontier", page.Find("g.er-node[data-table='invoices']").ClassName);
        // Une table frontière ne montre que les colonnes de ses relations.
        Assert.Empty(page.FindAll("g.er-node[data-table='customers'] g.er-row[data-column='email']"));
        Assert.NotEmpty(page.FindAll("g.er-node[data-table='customers'] g.er-row[data-column='id']"));
        Assert.Empty(page.FindAll("g.er-node[data-table='invoices'] g.er-row[data-column='total']"));
        Assert.Equal(2, page.FindAll("path.er-edge").Count);
        Assert.Contains("Tables du domaine (1 / 4)", page.Markup);
    }

    [BunitFact]
    public void The_remembered_place_of_a_member_table_is_restored_and_the_domain_completeness_is_shown()
    {
        var page = Open();

        SelectDomain(page, "Ventes");

        Assert.Equal("translate(300 50)", page.Find("g.er-node[data-table='orders']").GetAttribute("transform"));
        page.WaitForAssertion(() => Assert.Contains("Documentation du domaine : 50 %", page.Markup));
        Assert.Contains(_api.Requests, r => r.RequestUri!.PathAndQuery == "/api/v1/Completeness/database/db?domainId=d1");
    }

    [BunitFact]
    public void Going_back_to_all_tables_shows_every_table_again_without_frontier_styling()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        page.Find(".domain-all").Click();

        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll("g.er-node").Count));
        Assert.Empty(page.FindAll(".er-frontier"));
    }

    [BunitFact]
    public void An_empty_domain_explains_how_to_fill_it()
    {
        _domains = """[{"id":"d9","databaseId":"db","name":"Vide","tables":[]}]""";
        var page = Open();

        page.Find("[data-domain='Vide']").Click();

        page.WaitForAssertion(() => Assert.Contains("Ce domaine est vide", page.Markup));
        Assert.Empty(page.FindAll("g.er-node"));
    }

    // --- Place mémorisée ---

    [BunitFact]
    public void Moving_a_member_table_saves_its_place_for_the_domain_but_a_frontier_table_is_not_saved()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        var orders = page.Find("g.er-node[data-table='orders']");
        orders.PointerDown(new PointerEventArgs { ClientX = 100, ClientY = 100 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 160, ClientY = 130 });
        page.Find("g.er-node[data-table='orders']").PointerUp(new PointerEventArgs { ClientX = 160, ClientY = 130 });

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d1/layout")));
        var (_, body) = Sent(HttpMethod.Put, "/api/v1/DomainInfo/d1/layout");
        var place = Assert.Single(body.EnumerateArray());
        Assert.Equal("t2", place.GetProperty("tableId").GetString());
        Assert.Equal(360, place.GetProperty("x").GetDouble());
        Assert.Equal(80, place.GetProperty("y").GetDouble());

        var before = _api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/layout"));
        var customers = page.Find("g.er-node[data-table='customers']");
        customers.PointerDown(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 90, ClientY = 90 });
        page.Find("g.er-node[data-table='customers']").PointerUp(new PointerEventArgs { ClientX = 90, ClientY = 90 });
        Assert.Equal(before, _api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/layout")));
    }

    [BunitFact]
    public void Moving_a_table_in_the_all_tables_view_saves_nothing()
    {
        var page = Open();

        page.Find("g.er-node[data-table='tags']").PointerDown(new PointerEventArgs { ClientX = 1, ClientY = 1 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 80, ClientY = 80 });
        page.Find("g.er-node[data-table='tags']").PointerUp(new PointerEventArgs { ClientX = 80, ClientY = 80 });

        Assert.DoesNotContain(_api.Requests, r => r.Method == HttpMethod.Put);
    }

    // --- Couleur d'une table dans le domaine ---

    [BunitFact]
    public void A_table_of_the_viewed_domain_can_be_given_a_colour_that_changes_its_header()
    {
        var page = Open();
        SelectDomain(page, "Ventes");
        var orders = page.Find("g.er-node[data-table='orders']");
        Assert.Null(orders.QuerySelector("rect.er-head")!.GetAttribute("style")); // bleu par défaut
        orders.PointerDown(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        orders.PointerUp(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".table-colour .color-picker-toggle")));

        page.Find(".table-colour .color-picker-toggle").Click();
        page.Find(".table-colour .color-swatch[data-color='#2e7d32']").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d1/tables/t2/color")));
        Assert.Equal("#2e7d32", Sent(HttpMethod.Put, "/api/v1/DomainInfo/d1/tables/t2/color").Body.GetProperty("color").GetString());
        page.WaitForAssertion(() => Assert.Contains("fill:#2e7d32", page.Find("g.er-node[data-table='orders'] rect.er-head").GetAttribute("style")));
    }

    [BunitFact]
    public void The_colour_choice_is_offered_only_inside_a_domain_for_one_of_its_own_tables()
    {
        var page = Open();
        var orders = page.Find("g.er-node[data-table='orders']");
        orders.PointerDown(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        orders.PointerUp(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".er-legend")));

        Assert.Empty(page.FindAll(".table-colour")); // vue libre : pas de domaine, donc pas de couleur par domaine
    }

    // --- Appartenance par case à cocher ---

    [BunitFact]
    public void In_a_domain_checking_a_table_adds_it_and_unchecking_removes_it()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        Check(page, "tags", true);
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d1/tables/t3")));

        Check(page, "orders", false);
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/d1/tables/t2")));
    }

    [BunitFact]
    public void The_all_tables_view_checkboxes_only_filter_the_diagram_without_calling_the_api()
    {
        var page = Open();

        Check(page, "tags", false);

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("g.er-node").Count));
        Assert.DoesNotContain(_api.Requests, r => r.Method != HttpMethod.Get);
    }

    // --- Création, renommage, suppression ---

    [BunitFact]
    public void A_new_domain_is_created_and_selected()
    {
        var page = Open();
        _domains = """
            [{"id":"d1","databaseId":"db","name":"Ventes","showNeighbors":true,"tables":[]},{"id":"d3","databaseId":"db","name":"RH","tables":[]}]
            """;

        page.Find(".new-domain-name input").Input("RH");
        page.Find("button.new-domain-add").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Post, "/api/v1/DomainInfo")));
        Assert.Equal("RH", Sent(HttpMethod.Post, "/api/v1/DomainInfo").Body.GetProperty("name").GetString());
        Assert.Equal("db", Sent(HttpMethod.Post, "/api/v1/DomainInfo").Body.GetProperty("databaseId").GetString());
    }

    [BunitFact]
    public void The_create_button_is_disabled_for_a_blank_name_and_a_duplicate_is_reported()
    {
        _override = r => r.Method == HttpMethod.Post
            ? FakeApi.Json("""{"title":"Conflit avec les données du catalogue (contrainte non respectée).","status":409}""", HttpStatusCode.Conflict)
            : null;
        var page = Open();
        Assert.True(page.Find("button.new-domain-add").HasAttribute("disabled"));

        page.Find(".new-domain-name input").Input("Ventes");
        page.Find("button.new-domain-add").Click();

        page.WaitForAssertion(() => Assert.Contains("Conflit avec les données", page.Markup));
    }

    [BunitFact]
    public void A_domain_is_renamed_with_enter_and_escape_cancels()
    {
        var page = Open();

        page.Find("[data-domain='Ventes'] button.domain-rename").Click();
        page.Find(".domain-rename-input input").Input("Commerce");
        page.Find(".domain-rename-input input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d1")));
        Assert.Equal("Commerce", Sent(HttpMethod.Put, "/api/v1/DomainInfo/d1").Body.GetProperty("name").GetString());

        page.Find("[data-domain='Facturation'] button.domain-rename").Click();
        page.Find(".domain-rename-input input").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(page.FindAll(".domain-rename-input"));
        Assert.False(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d2"));
    }

    [BunitFact]
    public void Deleting_the_selected_domain_goes_back_to_all_tables_and_says_the_tables_are_kept()
    {
        var page = Open();
        SelectDomain(page, "Ventes");
        _domains = """[{"id":"d2","databaseId":"db","name":"Facturation","tables":[{"tableId":"t4","x":null,"y":null}]}]""";

        page.Find("[data-domain='Ventes'] button.domain-delete").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/d1")));
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message.Contains("ses tables sont conservées") && t.Undo != null));
        Assert.Equal(4, page.FindAll("g.er-node").Count);
        Assert.Empty(page.FindAll("[data-domain='Ventes']"));
    }

    [BunitFact]
    public void Clicking_the_rename_or_delete_button_does_not_also_select_the_domain()
    {
        var page = Open();

        page.Find("[data-domain='Facturation'] button.domain-rename").Click();

        Assert.Contains("domain-selected", page.Find(".domain-all").ClassName);
    }

    // --- Glisser-déposer ---

    [BunitFact]
    public void Dragging_a_table_from_the_list_onto_a_domain_adds_it()
    {
        var page = Open();

        page.Find("[data-table-item='tags']").DragStart(new DragEventArgs());
        Assert.Contains("domain-target", page.Find("[data-domain='Ventes']").ClassName);
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "tags ajoutée au domaine Ventes." && t.Undo != null));
        Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d1/tables/t3"));
        Assert.Empty(page.FindAll(".domain-target"));
    }

    [BunitFact]
    public void Dropping_onto_another_domain_while_viewing_one_moves_the_table()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        page.Find("[data-table-item='orders']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Facturation']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d2/tables/t2?moveFrom=d1")));
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "orders déplacée vers le domaine Facturation." && t.Undo != null));
    }

    [BunitFact]
    public void A_table_that_is_not_in_the_viewed_domain_is_added_not_moved()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        page.Find("[data-table-item='tags']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Facturation']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d2/tables/t3")));
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.Query.Contains("moveFrom"));
    }

    [BunitFact]
    public void Dragging_a_table_out_of_the_diagram_makes_the_domains_drop_targets_and_a_drop_files_it()
    {
        var page = Open();

        page.Find("g.er-node[data-table='tags']").PointerDown(new PointerEventArgs { ClientX = 5, ClientY = 5 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 60, ClientY = 5 });
        Assert.Empty(page.FindAll(".domain-target"));
        page.Find("svg.er-svg").PointerLeave(new PointerEventArgs());
        Assert.Equal(2, page.FindAll(".domain-target").Count);

        page.Find("[data-domain='Facturation']").PointerUp(new PointerEventArgs());

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/d2/tables/t3")));
        Assert.Empty(page.FindAll(".domain-target"));
        // Un dépôt hors diagramme ne déplace rien dans le domaine « Toutes les tables » : rien n'est mémorisé.
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/layout"));
    }

    [BunitFact]
    public void Releasing_outside_every_domain_cancels_the_drag_and_files_nothing()
    {
        var page = Open();
        page.Find("g.er-node[data-table='tags']").PointerDown(new PointerEventArgs { ClientX = 5, ClientY = 5 });
        page.Find("svg.er-svg").PointerLeave(new PointerEventArgs());
        Assert.NotEmpty(page.FindAll(".domain-target"));

        page.Find("#domains").PointerUp(new PointerEventArgs()); // le dépôt retombe sur le conteneur, pas sur un domaine

        Assert.Empty(page.FindAll(".domain-target"));
        Assert.DoesNotContain(_api.Requests, r => r.Method == HttpMethod.Put);
    }

    [BunitFact]
    public void A_refused_drop_shows_the_server_message()
    {
        _override = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.Contains("/tables/")
            ? FakeApi.Json("""{"title":"La table n'appartient pas à la base de ce domaine.","status":400}""", HttpStatusCode.BadRequest)
            : null;
        var page = Open();

        page.Find("[data-table-item='tags']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.Contains("n'appartient pas à la base de ce domaine", page.Markup));
    }

    // --- Exports ---

    [BunitFact]
    public void Exports_of_a_domain_include_the_frontier_tables_and_carry_the_domain_in_the_file_name()
    {
        var page = Open();
        SelectDomain(page, "Ventes");

        Export(page, "export-mermaid");

        var call = _ctx.JSInterop.Invocations.Single(c => c.Identifier == "granitdb.download");
        Assert.Equal("Boutique_Ventes.mmd", (string)call.Arguments[0]!);
        var mermaid = (string)call.Arguments[2]!;
        Assert.Contains("orders {", mermaid);
        Assert.Contains("customers {", mermaid); // table frontière
        Assert.DoesNotContain("tags {", mermaid);
    }

    // --- Jalon 11 : relations indirectes, plan d'origine, édition du domaine, liste dans la barre latérale ---

    private static void OpenRelationsPanel(IRenderedComponent<Diagram> page) =>
        page.Find("#domain-relations .mud-expand-panel-header").Click();

    [BunitFact]
    public void Indirect_relations_are_hidden_in_one_click_and_the_count_says_how_many()
    {
        var page = Open();
        SelectDomain(page, "Ventes");
        OpenRelationsPanel(page);

        // Les deux relations du domaine (avec ses voisines) ne sont pas des clés étrangères détectées : elles sont indirectes.
        Assert.Contains("(2)", page.Find("button.hide-indirect").TextContent);
        Assert.Empty(page.FindAll("button.reset-dataedo")); // pas de documentation d'origine connue
        page.Find("button.hide-indirect").Click();

        page.WaitForAssertion(() => Assert.True(_api.Requests.Any(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo/d1/settings")));
        var body = JsonDocument.Parse(_api.Bodies[_api.Requests.FindIndex(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo/d1/settings")]).RootElement;
        Assert.True(body.GetProperty("hideIndirectRelations").GetBoolean());
    }

    [BunitFact]
    public void A_domain_from_a_documentation_can_be_reset_to_its_original_plan()
    {
        _domains = """[{"id":"d1","databaseId":"db","name":"Ventes","showNeighbors":true,"hasDocumentationOrigin":true,"hiddenRelations":["r1"],"tables":[{"tableId":"t2","x":300,"y":50}]}]""";
        var page = Open();
        SelectDomain(page, "Ventes");
        OpenRelationsPanel(page);

        page.Find("button.reset-dataedo").Click();

        page.WaitForAssertion(() => Assert.True(_api.Requests.Any(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo/d1/settings")));
        var body = JsonDocument.Parse(_api.Bodies[_api.Requests.FindIndex(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo/d1/settings")]).RootElement;
        Assert.True(body.GetProperty("resetToDocumentation").GetBoolean());
    }

    [BunitFact]
    public void The_tables_of_a_domain_are_edited_from_the_diagram_in_the_same_dialog_as_the_manage_screen()
    {
        var dialogs = _ctx.Render<MudBlazor.MudDialogProvider>();
        var page = Open();
        Assert.Empty(page.FindAll("button.edit-domain")); // pas dans la vue « toutes les tables »
        SelectDomain(page, "Ventes");

        page.Find("button.edit-domain").Click();

        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll(".domain-dialog")));
        Assert.Contains("Ventes", dialogs.Find("input.domain-dialog-name").GetAttribute("value"));
        Assert.Contains("table dans ce domaine", dialogs.Find(".domain-dialog-preview").TextContent);
        Assert.NotEmpty(dialogs.FindAll("button.domain-dialog-add-linked")); // customers et invoices sont liées à orders
    }

    [BunitFact]
    public void The_diagram_page_keeps_its_own_table_picker_and_nothing_is_added_to_the_sidebar()
    {
        var page = Open();

        Assert.NotEmpty(page.FindAll(".table-picker"));
        Assert.Empty(page.FindAll(".picker-in-sidebar"));

        SelectDomain(page, "Ventes");
        Assert.NotEmpty(page.FindAll(".table-picker"));
    }
}
