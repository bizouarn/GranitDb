using System.Net;
using Bunit;
using GranitDb.Client.Layout;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components.Web;

namespace GranitDb.Tests;

/// <summary>Explorateur : arbre bases → tables → colonnes, domaines, favoris, clavier.</summary>
public class ClientExplorerTests : ShellTestBase
{
    private IRenderedComponent<Explorer> Open()
    {
        var page = Ctx.Render<Explorer>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-db]")));
        return page;
    }

    [BunitFact]
    public void The_management_pages_are_reached_from_one_labelled_menu_not_from_bare_icons()
    {
        var page = Open();

        var menu = page.Find(".manage-menu");
        Assert.Contains("Gérer", menu.TextContent);
        Assert.Empty(page.FindAll("a.manage-link")); // rien n'est affiché tant que le menu est fermé
    }

    private static void ExpandDb(IRenderedComponent<Explorer> page, string name)
    {
        page.Find($"[data-db='{name}'] button.chev").Click();
        page.WaitForAssertion(() => Assert.Equal("true", page.Find($"[data-db='{name}']").GetAttribute("aria-expanded")));
    }

    [BunitFact]
    public void Databases_are_listed_by_name_as_the_roots_of_an_accessible_tree()
    {
        var page = Open();

        Assert.Equal(["Boutique", "RH"], page.FindAll("[data-db]").Select(n => n.GetAttribute("data-db")!).ToArray());
        Assert.Equal("tree", page.Find("[role=tree]").GetAttribute("role"));
        Assert.All(page.FindAll("[data-db]"), n =>
        {
            Assert.Equal("treeitem", n.GetAttribute("role"));
            Assert.Equal("1", n.GetAttribute("aria-level"));
            Assert.Equal("false", n.GetAttribute("aria-expanded"));
        });
        Assert.Equal("/databases/d1", page.Find("[data-db='Boutique'] a.node-label").GetAttribute("href"));
        Assert.Equal("Explorateur", page.Find("nav").GetAttribute("aria-label"));
        // Seul le premier élément est dans l'ordre de tabulation.
        Assert.Equal("0", page.Find("[data-db='Boutique']").GetAttribute("tabindex"));
        Assert.Equal("-1", page.Find("[data-db='RH']").GetAttribute("tabindex"));
    }

    [BunitFact]
    public void Tables_are_only_read_when_a_database_is_expanded_and_only_once()
    {
        var page = Open();
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath.Contains("/TableInfo/"));

        ExpandDb(page, "Boutique");
        page.Find("[data-db='Boutique'] button.chev").Click(); // replier
        page.Find("[data-db='Boutique'] button.chev").Click(); // déplier de nouveau

        page.WaitForAssertion(() => Assert.Equal(["customers", "orders", "tags"], page.FindAll("[data-table]").Select(n => n.GetAttribute("data-table")!).ToArray()));
        Assert.Single(Api.Requests.Where(r => r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/database/d1"));
        Assert.Equal("2", page.Find("[data-table='customers']").GetAttribute("aria-level"));
    }

    [BunitFact]
    public void A_table_shows_its_columns_with_key_and_sensitivity_marks_when_expanded()
    {
        var page = Open();
        ExpandDb(page, "Boutique");

        page.Find("[data-table='customers'] button.chev").Click();

        Assert.Equal(["id", "email"], page.FindAll("[data-column]").Select(n => n.GetAttribute("data-column")!).ToArray());
        Assert.Equal("3", page.Find("[data-column='id']").GetAttribute("aria-level"));
        Assert.NotEmpty(page.FindAll("[data-column='email'] svg")); // bouclier des données sensibles
        Assert.Contains("TEXT", page.Find("[data-column='email']").TextContent);
        Assert.Equal("/databases/d1/tables/t1?column=c2", page.Find("[data-column='email'] a.node-label").GetAttribute("href"));
        Assert.Equal("/databases/d1/tables/t1", page.Find("[data-table='customers'] a.node-label").GetAttribute("href"));
    }

    [BunitFact]
    public void Domains_appear_as_collections_with_their_tables()
    {
        var page = Open();
        ExpandDb(page, "Boutique");

        Assert.Contains("Domaines", page.Markup);
        var domain = page.Find("[data-domain='Ventes']");
        Assert.Equal("/databases/d1/diagram?domain=g1", domain.QuerySelector("a.node-label")!.GetAttribute("href"));
        Assert.Contains("1", domain.TextContent);

        domain.QuerySelector("button.chev")!.Click();

        Assert.Equal("orders", Assert.Single(page.FindAll("[data-member]")).GetAttribute("data-member"));
    }

    [BunitFact]
    public void Clicking_an_element_selects_it_for_the_detail_panel()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        page.Find("[data-table='customers'] button.chev").Click();

        page.Find("[data-db='Boutique'] a.node-label").Click();
        Assert.Equal(new SelectionItem("database", "d1", "d1"), State.Selection);
        page.Find("[data-table='orders'] a.node-label").Click();
        Assert.Equal(new SelectionItem("table", "t2", "d1", "t2"), State.Selection);
        page.Find("[data-column='email'] a.node-label").Click();
        Assert.Equal(new SelectionItem("column", "c2", "d1", "t1"), State.Selection);
        Assert.Contains("node-selected", page.Find("[data-column='email']").ClassName);
    }

    [BunitFact]
    public void The_page_being_shown_is_marked_in_the_tree()
    {
        State.Open("/databases/d1/tables/t1");
        var page = Open();
        ExpandDb(page, "Boutique");

        Assert.Contains("node-current", page.Find("[data-table='customers']").ClassName);
        Assert.DoesNotContain("node-current", page.Find("[data-table='orders']").ClassName);
    }

    // --- Filtre ---

    [BunitFact]
    public void Filtering_searches_the_tables_of_every_database_and_hides_what_does_not_match()
    {
        var page = Open();

        page.Find("input[aria-label='Filtrer l\\'explorateur']").Input("orde");

        page.WaitForAssertion(() => Assert.Equal(["Boutique"], page.FindAll("[data-db]").Select(n => n.GetAttribute("data-db")!).ToArray()));
        Assert.Equal(["orders"], page.FindAll("[data-table]").Select(n => n.GetAttribute("data-table")!).ToArray());
        Assert.True(WasSent(HttpMethod.Get, "/api/v1/TableInfo/database/d2?summary=true")); // RH aussi a été lu pour chercher
        Assert.Empty(page.FindAll("[data-domain]")); // pas de collections pendant une recherche
    }

    [BunitFact]
    public void A_database_name_match_keeps_all_its_tables_and_no_match_says_so()
    {
        var page = Open();
        var filter = page.Find("input[aria-label='Filtrer l\\'explorateur']");

        filter.Input("rh");
        page.WaitForAssertion(() => Assert.Equal(["employees"], page.FindAll("[data-table]").Select(n => n.GetAttribute("data-table")!).ToArray()));

        filter.Input("zzz");
        page.WaitForAssertion(() => Assert.Contains("Aucun résultat pour « zzz »", page.Markup));
        filter.Input("");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-db]").Count));
    }

    [BunitFact]
    public void A_single_character_does_not_filter()
    {
        var page = Open();

        page.Find("input[aria-label='Filtrer l\\'explorateur']").Input("o");

        Assert.Equal(2, page.FindAll("[data-db]").Count);
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath.Contains("/TableInfo/"));
    }

    // --- Clavier ---

    [BunitFact]
    public void Right_expands_left_collapses_and_the_arrows_move_the_focus_through_the_script()
    {
        var page = Open();
        var db = page.Find("[data-db='Boutique']");

        db.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        page.WaitForAssertion(() => Assert.Equal("true", page.Find("[data-db='Boutique']").GetAttribute("aria-expanded")));
        db.KeyDown(new KeyboardEventArgs { Key = "ArrowRight" }); // déjà déplié : descend d'un cran
        db.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        db.KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        db.KeyDown(new KeyboardEventArgs { Key = "Home" });
        db.KeyDown(new KeyboardEventArgs { Key = "End" });
        page.Find("[data-db='Boutique']").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });

        page.WaitForAssertion(() => Assert.Equal("false", page.Find("[data-db='Boutique']").GetAttribute("aria-expanded")));
        var moves = Ctx.JSInterop.Invocations.Where(i => i.Identifier == "granitdb.moveTreeFocus").Select(i => (string)i.Arguments[0]!).ToArray();
        Assert.Equal(["next", "next", "prev", "first", "last"], moves);
    }

    [BunitFact]
    public void Left_on_a_collapsed_item_moves_to_its_parent_and_enter_opens_the_page()
    {
        var page = Open();
        ExpandDb(page, "Boutique");

        page.Find("[data-table='orders']").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        page.Find("[data-table='orders']").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.moveTreeFocus" && (string)i.Arguments[0]! == "parent");
        Assert.EndsWith("/databases/d1/tables/t2", Nav.Uri);
        page.Find("[data-column], [data-table='customers']").KeyDown(new KeyboardEventArgs { Key = " " });
        Assert.EndsWith("/databases/d1/tables/t1", Nav.Uri);
    }

    [BunitFact]
    public void Enter_on_a_database_opens_it_and_F_toggles_the_favorite()
    {
        var page = Open();

        page.Find("[data-db='RH']").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.EndsWith("/databases/d2", Nav.Uri);

        page.Find("[data-db='RH']").KeyDown(new KeyboardEventArgs { Key = "f" });
        Assert.True(State.IsFavorite("database", "d2"));
        page.Find("[data-db='RH']").KeyDown(new KeyboardEventArgs { Key = "F" });
        Assert.False(State.IsFavorite("database", "d2"));
    }

    // --- Favoris ---

    [BunitFact]
    public void The_star_adds_a_favorite_listed_at_the_top_and_removes_it()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        Assert.Empty(page.FindAll("[data-favorite]"));

        page.Find("[data-table='customers'] button.node-star").Click();
        page.Find("[data-db='RH'] button.node-star").Click();

        Assert.Equal(["customers (Boutique)", "RH"], page.FindAll("[data-favorite]").Select(n => n.GetAttribute("data-favorite")!).ToArray());
        Assert.Equal("/databases/d1/tables/t1", page.Find("[data-favorite='customers (Boutique)'] a").GetAttribute("href"));
        Assert.Equal("true", page.Find("[data-table='customers'] button.node-star").GetAttribute("aria-pressed"));

        page.Find("[data-favorite='RH'] button").Click();
        Assert.False(State.IsFavorite("database", "d2"));
        Assert.Single(page.FindAll("[data-favorite]"));
    }

    [BunitFact]
    public void Favorites_are_reordered_by_dragging_and_a_domain_can_be_a_favorite()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        page.Find("[data-db='Boutique'] button.node-star").Click();
        page.Find("[data-db='RH'] button.node-star").Click();
        page.Find("[data-domain='Ventes'] button.node-star").Click();
        Assert.Equal(["Boutique", "RH", "Ventes (Boutique)"], State.Favorites.Select(f => f.Name).ToArray());

        page.Find("[data-favorite='Ventes (Boutique)']").DragStart(new DragEventArgs());
        page.Find("[data-favorite='Boutique']").Drop(new DragEventArgs());

        Assert.Equal(["Ventes (Boutique)", "Boutique", "RH"], State.Favorites.Select(f => f.Name).ToArray());
        Assert.Equal("/databases/d1/diagram?domain=g1", State.Favorites[0].Href);
    }

    // --- Domaines : déposer une table ---

    [BunitFact]
    public void A_table_dropped_on_a_domain_joins_it_and_the_undo_takes_it_out_again()
    {
        var page = Open();
        ExpandDb(page, "Boutique");

        page.Find("[data-table='customers']").DragStart(new DragEventArgs());
        Assert.Contains("domain-target", page.Find("[data-domain='Ventes']").ClassName);
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/g1/tables/t1")));
        var toast = Assert.Single(Notices.Toasts, t => t.Message == "customers ajoutée au domaine Ventes.");
        Assert.NotNull(toast.Undo);

        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1/tables/t1"));
    }

    [BunitFact]
    public void Dropping_a_table_already_in_the_domain_or_from_another_database_changes_nothing()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        ExpandDb(page, "RH");

        page.Find("[data-table='orders']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());
        page.Find("[data-table='employees']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        Assert.Contains(Notices.Toasts, t => t.Message == "orders est déjà dans le domaine Ventes.");
        Assert.Contains(Notices.Toasts, t => t.Message.Contains("n'appartient pas à la base de ce domaine"));
        Assert.DoesNotContain(Api.Requests, r => r.Method == HttpMethod.Put);
    }

    [BunitFact]
    public void A_refused_drop_is_reported_as_an_error()
    {
        Override = r => r.Method == HttpMethod.Put
            ? FakeApi.Json("""{"title":"Conflit","status":409}""", HttpStatusCode.Conflict)
            : null;
        var page = Open();
        ExpandDb(page, "Boutique");

        page.Find("[data-table='tags']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Conflit" && t.Severity == MudBlazor.Severity.Error));
    }

    [BunitFact]
    public void A_table_is_removed_from_a_domain_with_an_undo_that_restores_its_place()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        page.Find("[data-domain='Ventes'] button.chev").Click();

        page.Find("[data-member='orders'] button.node-action").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1/tables/t2")));
        var toast = Assert.Single(Notices.Toasts, t => t.Message == "orders retirée du domaine Ventes.");
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        var restored = BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/tables/t2");
        Assert.Equal(120, restored.GetProperty("x").GetDouble());
        Assert.Equal(40, restored.GetProperty("y").GetDouble());
    }

    // --- Rechargements ---

    [BunitFact]
    public void A_catalog_change_reloads_what_is_expanded_and_a_global_one_also_the_list()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        var tables = Requests(HttpMethod.Get, "/api/v1/TableInfo/database/d1?summary=true").Count();
        var list = Requests(HttpMethod.Get, "/api/v1/DatabaseInfo").Count();

        State.NotifyCatalogChanged("d2"); // une base fermée : rien à relire
        State.NotifyCatalogChanged("d1");
        page.WaitForAssertion(() => Assert.Equal(tables + 1, Requests(HttpMethod.Get, "/api/v1/TableInfo/database/d1?summary=true").Count()));
        Assert.Equal(list, Requests(HttpMethod.Get, "/api/v1/DatabaseInfo").Count());

        State.NotifyCatalogChanged();
        page.WaitForAssertion(() => Assert.Equal(list + 1, Requests(HttpMethod.Get, "/api/v1/DatabaseInfo").Count()));
    }

    [BunitFact]
    public void The_reload_button_rereads_the_list_and_the_expanded_databases()
    {
        var page = Open();
        ExpandDb(page, "Boutique");
        var before = Api.Requests.Count;

        page.Find("button[aria-label='Recharger l\\'explorateur']").Click();

        page.WaitForAssertion(() => Assert.True(Api.Requests.Count >= before + 3)); // liste, tables, domaines
    }

    [BunitFact]
    public void An_unreachable_api_is_reported_without_breaking_the_tree()
    {
        Override = _ => throw new HttpRequestException("refused");

        var page = Ctx.Render<Explorer>();

        page.WaitForAssertion(() => Assert.Contains("L'API est injoignable", page.Markup));
        Assert.Empty(page.FindAll("[data-db]"));
    }

    [BunitFact]
    public void Without_any_database_the_tree_explains_how_to_start()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo" ? FakeApi.Json("[]") : null;

        var page = Ctx.Render<Explorer>();

        page.WaitForAssertion(() => Assert.Contains("Aucune base pour le moment", page.Markup));
    }

    [BunitFact]
    public void The_diagram_button_of_a_database_opens_its_diagram()
    {
        var page = Open();

        page.Find("[data-db='Boutique'] button[aria-label='Diagramme de Boutique']").Click();

        Assert.EndsWith("/databases/d1/diagram", Nav.Uri);
    }

    [BunitFact]
    public void The_explorer_asks_for_the_light_summary_and_reads_the_columns_of_a_table_only_when_it_is_opened()
    {
        Override = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/v1/TableInfo/database/d1" => FakeApi.Json("""[{"id":"t1","name":"orders","tableType":"Table","databaseId":"d1","columns":[]}]"""),
            "/api/v1/TableInfo/t1/columns" => FakeApi.Json("""[{"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0},{"id":"c2","name":"total","dataType":"REAL","position":1}]"""),
            _ => null
        };
        var page = Open();

        ExpandDb(page, "Boutique");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-table='orders']")));
        Assert.Contains(Api.Requests, r => r.RequestUri!.PathAndQuery == "/api/v1/TableInfo/database/d1?summary=true");
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/columns"));

        page.Find("[data-table='orders'] button.chev").Click();

        page.WaitForAssertion(() => Assert.Equal(["id", "total"], page.FindAll("[data-column]").Select(n => n.GetAttribute("data-column")!).ToArray()));
        page.Find("[data-table='orders'] button.chev").Click(); // replier
        page.Find("[data-table='orders'] button.chev").Click(); // rouvrir : pas de seconde lecture
        Assert.Single(Api.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("/columns")));
    }

    [BunitFact]
    public void A_database_with_hundreds_of_tables_shows_them_by_pages()
    {
        var tables = string.Join(",", Enumerable.Range(0, 650).Select(i => $$"""{"id":"t{{i}}","name":"table_{{i:000}}","tableType":"Table","databaseId":"d1","columns":[]}"""));
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/database/d1" ? FakeApi.Json($"[{tables}]") : null;
        var page = Open();

        ExpandDb(page, "Boutique");

        page.WaitForAssertion(() => Assert.Equal(300, page.FindAll("[data-table]").Count));
        Assert.Contains("300 tables affichées sur 650", page.Markup);
        page.Find(".show-more-tables").Click();
        Assert.Equal(600, page.FindAll("[data-table]").Count);
        page.Find(".show-more-tables").Click();
        Assert.Equal(650, page.FindAll("[data-table]").Count);
        Assert.Empty(page.FindAll(".show-more-tables"));
    }
}
