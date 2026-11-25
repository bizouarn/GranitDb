using System.Net;
using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace GranitDb.Tests;

/// <summary>Jalon 11 côté client : suppression d'une base, domaines configurables, gestion des étiquettes et domaines, aperçu du remplacement.</summary>
public class ClientWorkspaceFeaturesTests : ShellTestBase
{
    // --- Domaines configurables ---

    private IRenderedComponent<Diagram> OpenDomain(string? domains = null)
    {
        if (domains != null)
            Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo/database/d1" ? FakeApi.Json(domains) : null;
        Nav.NavigateTo("/databases/d1/diagram?domain=g1");
        var page = Ctx.Render<Diagram>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".er-node")));
        return page;
    }

    private static string[] Shown(IRenderedComponent<Diagram> page) =>
        page.FindAll(".er-node").Select(n => n.GetAttribute("data-table")!).Order().ToArray();

    [BunitFact]
    public void The_legend_explains_the_symbols_and_can_be_closed()
    {
        var page = OpenDomain();
        Assert.Empty(page.FindAll(".er-help"));

        page.Find("button.er-legend-toggle").Click();

        var text = page.Find(".er-help").TextContent;
        Assert.Contains("clé référencée", text);
        Assert.Contains("relation multi-colonnes", text);
        Assert.Contains("Échap", text);
        page.Find("button.er-legend-toggle").Click();
        Assert.Empty(page.FindAll(".er-help"));
    }

    [BunitFact]
    public void Escape_clears_the_selection_of_the_diagram()
    {
        var page = OpenDomain();
        DiagramGestures.ClickTable(page, "orders");
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".er-legend")));

        page.Find(".er-focus").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(page.FindAll(".er-legend"));
        Assert.DoesNotContain("er-selected", page.Find(".er-node[data-table='orders']").ClassName);
    }

    [BunitFact]
    public void A_domain_that_hides_relations_says_so_and_offers_to_show_them_all()
    {
        var page = OpenDomain("""[{"id":"g1","databaseId":"d1","name":"Ventes","color":"#1976d2","hiddenRelations":["r1"],"tables":[{"tableId":"t1"},{"tableId":"t2"}]}]""");

        Assert.Contains("1 relation masquée dans ce domaine", page.Find(".er-hidden-banner").TextContent);
        page.Find(".er-hidden-show").Click();

        page.WaitForAssertion(() => Assert.Empty(BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/settings").GetProperty("hiddenRelations").EnumerateArray().ToArray()));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".er-hidden-banner")));
    }

    [BunitFact]
    public async Task Showing_every_relation_reports_how_many_were_touched_and_undoing_restores_the_hidden_list()
    {
        var page = OpenDomain("""[{"id":"g1","databaseId":"d1","name":"Ventes","color":"#1976d2","hiddenRelations":["r1"],"tables":[{"tableId":"t1"},{"tableId":"t2"}]}]""");
        page.Find(".er-hidden-show").Click();

        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "1 relation de nouveau affichée."));
        var toast = Notices.Toasts.Single(t => t.Message == "1 relation de nouveau affichée.");
        Assert.NotNull(toast.Undo);

        await Notices.UndoAsync(toast.Id);

        page.WaitForAssertion(() => Assert.Equal(["r1"], BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/settings").GetProperty("hiddenRelations")
            .EnumerateArray().Select(e => e.GetString()!).ToArray()));
        page.WaitForAssertion(() => Assert.Contains("1 relation masquée dans ce domaine", page.Find(".er-hidden-banner").TextContent));
    }

    [BunitFact]
    public void Neighbour_tables_appear_only_when_the_domain_asks_for_them_and_the_choice_is_saved()
    {
        var page = OpenDomain();
        Assert.Equal(["orders"], Shown(page));

        page.Find(".show-neighbors input").Change(true);

        page.WaitForAssertion(() => Assert.Equal(["customers", "orders"], Shown(page)));
        Assert.Contains("er-frontier", page.Find(".er-node[data-table='customers']").ClassName);
        Assert.True(BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/settings").GetProperty("showNeighbors").GetBoolean());
    }

    [BunitFact]
    public void A_domain_that_already_shows_neighbours_starts_with_them()
    {
        var page = OpenDomain("""[{"id":"g1","databaseId":"d1","name":"Ventes","showNeighbors":true,"hiddenRelations":[],"tables":[{"tableId":"t2","x":120,"y":40}]}]""");

        Assert.Equal(["customers", "orders"], Shown(page));
    }

    [BunitFact]
    public void Unchecking_a_relation_hides_it_from_the_diagram_and_saves_the_list()
    {
        var page = OpenDomain("""[{"id":"g1","databaseId":"d1","name":"Ventes","showNeighbors":true,"hiddenRelations":[],"tables":[{"tableId":"t2","x":120,"y":40},{"tableId":"t1","x":0,"y":0}]}]""");
        Assert.Single(page.FindAll("path.er-edge"));

        page.Find("#domain-relations .mud-expand-panel-header").Click();
        var item = page.Find("[data-relation='orders.customer_id → customers.id'] input");
        item.Change(false);

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("path.er-edge")));
        var body = BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/settings");
        Assert.Equal(["r1"], body.GetProperty("hiddenRelations").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    [BunitFact]
    public void A_hidden_relation_stays_listed_so_it_can_be_shown_again()
    {
        var page = OpenDomain("""[{"id":"g1","databaseId":"d1","name":"Ventes","showNeighbors":true,"hiddenRelations":["r1"],"tables":[{"tableId":"t2","x":120,"y":40},{"tableId":"t1","x":0,"y":0}]}]""");
        Assert.Empty(page.FindAll("path.er-edge"));

        page.Find("#domain-relations .mud-expand-panel-header").Click();
        page.Find("[data-relation='orders.customer_id → customers.id'] input").Change(true);

        page.WaitForAssertion(() => Assert.Single(page.FindAll("path.er-edge")));
        Assert.Empty(BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/settings").GetProperty("hiddenRelations").EnumerateArray());
    }

    // --- Suppression d'une base ---

    [BunitFact]
    public void Deleting_a_database_asks_to_retype_its_name_then_cleans_the_workspace()
    {
        State.Open("/databases/d1");
        State.Open("/databases/d1/diagram");
        State.Open("/search?q=x");
        State.Select(new GranitDb.Client.Workspace.SelectionItem("database", "d1", "d1"));
        var deleted = 0;
        var cut = Ctx.Render<DeleteDatabaseButton>(p => p
            .Add(x => x.Database, new GranitDb.Client.Models.DatabaseView { Id = "d1", Name = "Boutique", Type = "Sqlite" })
            .Add(x => x.OnDeleted, () => deleted++));

        cut.Find("button.delete-database").Click();
        Assert.True(cut.Find("button.delete-confirm-button").HasAttribute("disabled"));

        cut.Find(".delete-confirm-name input").Input("boutiq");
        Assert.True(cut.Find("button.delete-confirm-button").HasAttribute("disabled"));
        cut.Find(".delete-confirm-name input").Input("boutique"); // la casse n'est pas exigée
        Assert.False(cut.Find("button.delete-confirm-button").HasAttribute("disabled"));
        Assert.False(WasSent(HttpMethod.Delete, "/api/v1/DatabaseInfo/d1"));

        cut.Find("button.delete-confirm-button").Click();

        cut.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DatabaseInfo/d1")));
        Assert.Equal(1, deleted);
        Assert.Equal(["/search?q=x"], State.Tabs.Select(t => t.Href).ToArray());
        Assert.Null(State.Selection);
        Assert.Contains(Notices.Toasts, t => t.Message.Contains("Boutique") && t.Message.Contains("supprimée"));
    }

    [BunitFact]
    public void Cancelling_or_a_server_error_keeps_the_database()
    {
        Override = r => r.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"title":"Impossible de supprimer."}""", System.Text.Encoding.UTF8, "application/json") }
            : null;
        var cut = Ctx.Render<DeleteDatabaseButton>(p => p
            .Add(x => x.Database, new GranitDb.Client.Models.DatabaseView { Id = "d1", Name = "Boutique", Type = "Sqlite" }));

        cut.Find("button.delete-database").Click();
        cut.Find("button.delete-cancel").Click();
        Assert.Empty(cut.FindAll("button.delete-confirm-button"));

        cut.Find("button.delete-database").Click();
        cut.Find(".delete-confirm-name input").Input("Boutique");
        cut.Find("button.delete-confirm-button").Click();

        cut.WaitForAssertion(() => Assert.Contains("Impossible de supprimer.", cut.Markup));
        Assert.Empty(Notices.Toasts);
    }

    [BunitFact]
    public void Each_database_card_on_the_home_page_has_its_delete_button()
    {
        var page = Ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("button.delete-database").Count));
    }

    // --- Étiquettes et domaines ---

    private IRenderedComponent<Manage> OpenManage()
    {
        Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/TagInfo"
            ? FakeApi.Json("""[{"id":"g1","name":"RGPD","color":"#ff0000"},{"id":"g2","name":"legacy"}]""")
            : null;
        var page = Ctx.Render<Manage>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-tag]")));
        return page;
    }

    [BunitFact]
    public void The_page_lists_tags_and_the_domains_of_each_database()
    {
        var page = OpenManage();

        Assert.Equal(["legacy", "RGPD"], page.FindAll("[data-tag]").Select(r => r.GetAttribute("data-tag")!).ToArray());
        Assert.Equal(["Ventes"], page.FindAll("[data-domain]").Select(r => r.GetAttribute("data-domain")!).ToArray());
        Assert.Contains("Boutique", page.Find("[data-database='Boutique']").TextContent);
        Assert.Equal("Étiquettes et domaines", State.Tabs.Count == 0 ? "Étiquettes et domaines" : State.Tabs.Last().Title);
    }

    [BunitFact]
    public void Filtering_by_name_says_so_counts_what_is_left_and_is_cleared_in_one_click()
    {
        var page = OpenManage();
        Assert.Empty(page.FindAll(".filter-bar"));

        page.Find("input.manage-filter").Input("rgp");

        page.WaitForAssertion(() => Assert.Equal(["RGPD"], page.FindAll("[data-tag]").Select(r => r.GetAttribute("data-tag")!).ToArray()));
        Assert.Contains("nom contient « rgp »", page.Find(".filter-bar").TextContent);
        Assert.Contains("1 étiquette(s) et 0 domaine(s) affichés", page.Find(".filter-bar").TextContent);
        Assert.Empty(page.FindAll("[data-domain]"));
        Assert.Contains("Aucun domaine ne correspond au filtre", page.Markup);

        page.Find(".filter-bar-clear").Click();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-tag]").Count));
        Assert.Empty(page.FindAll(".filter-bar"));
        Assert.Single(page.FindAll("[data-domain]"));
    }

    [BunitFact]
    public void A_tag_is_renamed_recoloured_and_deleted_everywhere_after_a_confirmation()
    {
        var page = OpenManage();
        var row = page.Find("[data-tag='RGPD']");

        row.QuerySelector(".color-picker-toggle")!.Click();
        row.QuerySelector("input.color-input")!.Change("#00ff00");
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/TagInfo/g1")));
        var colour = BodyOf(HttpMethod.Put, "/api/v1/TagInfo/g1");
        Assert.Equal("#00ff00", colour.GetProperty("color").GetString());
        Assert.Equal("RGPD", colour.GetProperty("name").GetString());

        page.WaitForElement("[data-tag='RGPD'] .inline-view").Click();
        page.Find("[data-tag='RGPD'] .inline-edit input").Input("Données perso");
        page.Find("[data-tag='RGPD'] .inline-edit input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        page.WaitForAssertion(() => Assert.Equal("Données perso", BodyOf(HttpMethod.Put, "/api/v1/TagInfo/g1").GetProperty("name").GetString()));

        page.Find("[data-tag='legacy'] button.delete-tag").Click();
        Assert.False(WasSent(HttpMethod.Delete, "/api/v1/TagInfo/g2"));
        Dialogs!.WaitForAssertion(() => Assert.Contains("toutes les bases", Dialogs!.Find(".confirm-dialog-consequence").TextContent));
        Dialogs!.Find("button.confirm-dialog-confirm").Click();
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/TagInfo/g2")));
    }

    [BunitFact]
    public void A_domain_is_renamed_keeping_its_colour_and_deleted_after_a_confirmation()
    {
        var page = OpenManage();

        page.WaitForElement("[data-domain='Ventes'] .inline-view").Click();
        page.Find("[data-domain='Ventes'] .inline-edit input").Input("Commerce");
        page.Find("[data-domain='Ventes'] .inline-edit input").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/g1")));
        var body = BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1");
        Assert.Equal("Commerce", body.GetProperty("name").GetString());
        Assert.Equal("#1976d2", body.GetProperty("color").GetString());
        Assert.False(body.TryGetProperty("showNeighbors", out var neighbours) && neighbours.ValueKind == System.Text.Json.JsonValueKind.True);

        page.Find("[data-domain='Ventes'] button.delete-domain").Click();
        Dialogs!.WaitForAssertion(() => Assert.Contains("1 table(s)", Dialogs!.Find(".confirm-dialog-consequence").TextContent));
        Assert.False(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1"));
        Dialogs!.Find("button.confirm-dialog-confirm").Click();
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1")));
    }

    [BunitFact]
    public void The_tables_of_a_domain_are_edited_in_a_dialog_with_two_lists_and_a_relation_count()
    {
        var page = OpenManage();

        page.Find("[data-domain='Ventes'] button.edit-tables").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll("[data-member='orders']")));
        Assert.Equal(["customers", "tags"], Dialogs!.FindAll("[data-available]").Select(r => r.GetAttribute("data-available")!).ToArray());
        Assert.Contains("1 table dans ce domaine, 0 relation", Dialogs!.Find(".domain-dialog-preview").TextContent);

        Dialogs!.Find("input.domain-dialog-search").Input("c");
        Assert.Equal(["customers"], Dialogs!.FindAll("[data-available]").Select(r => r.GetAttribute("data-available")!).ToArray());
        Dialogs!.Find("button.domain-dialog-add-all").Click();
        Assert.Contains("2 tables dans ce domaine, 1 relation entre elles", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Dialogs!.Find("[data-member='orders'] button.domain-dialog-remove").Click();
        Dialogs!.Find("button.domain-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/g1/tables/t1")));
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1/tables/t2")));
        Dialogs!.WaitForAssertion(() => Assert.Empty(Dialogs!.FindAll(".domain-dialog")));
        Assert.Contains(Notices.Toasts, t => t.Message == "Domaine Ventes modifié : 1 table ajoutée, 1 retirée.");
    }

    [BunitFact]
    public void The_domain_dialog_colours_a_table_of_the_domain()
    {
        Override = r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.EndsWith("/color")
            ? FakeApi.Json("""{"id":"g1","databaseId":"d1","name":"Ventes","tables":[]}""")
            : null;
        var page = OpenManage();

        page.Find("[data-domain='Ventes'] button.edit-tables").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll("[data-member='orders']")));
        Dialogs!.Find("[data-member='orders'] .color-picker-toggle").Click();
        Dialogs!.Find("[data-member='orders'] .color-swatch[data-color='#c62828']").Click();
        Dialogs!.Find("button.domain-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/g1/tables/t2/color")));
        Assert.Equal("#c62828", BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g1/tables/t2/color").GetProperty("color").GetString());
    }

    [BunitFact]
    public void A_new_domain_is_created_from_a_dialog_with_its_tables_and_refuses_a_taken_name()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo"
            ? FakeApi.Json("""{"id":"g9","databaseId":"d1","name":"Achats","tables":[]}""", HttpStatusCode.Created)
            : r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/TagInfo" ? FakeApi.Json("[]") : null;
        var page = Ctx.Render<Manage>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-database='Boutique'] button.new-domain")));

        page.Find("[data-database='Boutique'] button.new-domain").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".domain-dialog")));
        Assert.True(Dialogs!.Find("button.domain-dialog-save").HasAttribute("disabled"));
        Dialogs!.Find("input.domain-dialog-name").Input("ventes"); // existe déjà (sans tenir compte de la casse)
        Assert.Contains("déjà ce nom", Dialogs!.Find(".domain-dialog-problem").TextContent);
        Assert.True(Dialogs!.Find("button.domain-dialog-save").HasAttribute("disabled"));
        Dialogs!.Find("input.domain-dialog-name").Input("Achats");
        Dialogs!.Find("[data-available='tags'] button.domain-dialog-add").Click();
        Dialogs!.Find("button.domain-dialog-save").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DomainInfo/g9/tables/t3")));
        Assert.Equal("Achats", BodyOf(HttpMethod.Post, "/api/v1/DomainInfo").GetProperty("name").GetString());
        Assert.Contains(Notices.Toasts, t => t.Message == "Domaine Achats créé avec 1 table.");
    }

    [BunitFact]
    public void A_server_error_is_shown_at_the_top_of_the_page()
    {
        var page = OpenManage();
        var previous = Override;
        Override = r => r.Method == HttpMethod.Put
            ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"title":"Ce nom existe déjà."}""", System.Text.Encoding.UTF8, "application/json") }
            : previous(r);

        page.Find("[data-tag='legacy'] .color-picker-toggle").Click();
        page.Find("[data-tag='legacy'] input.color-input").Change("#123456");

        page.WaitForAssertion(() => Assert.Contains("Ce nom existe déjà.", page.Markup));
    }

    // --- Remplacement d'un fichier ---

    [BunitFact]
    public void Choosing_a_replacement_file_shows_the_preview_and_only_applying_replaces_it()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/import/sqlite/d1/preview"
            ? FakeApi.Json("""{"changes":[{"kind":"table","name":"invoices","action":"added","detail":"","losesDocumentation":false},{"kind":"table","name":"tags","action":"removed","detail":"","losesDocumentation":true}],"hasChanges":true,"losesDocumentation":1}""")
            : r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/import/sqlite/d1"
                ? FakeApi.Json("""{"database":{"id":"d1","name":"Boutique","type":"Sqlite"},"sync":{"tablesAdded":1,"tablesRemoved":1,"columnsAdded":2,"columnsUpdated":0,"columnsRemoved":0,"relationsCreated":0}}""")
                : null;
        Nav.NavigateTo("/databases/d1");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".replace-file-input")));

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("SQLite format 3", "nouvelle.db"));

        page.WaitForAssertion(() => Assert.Contains("Aperçu du remplacement par nouvelle.db", page.Markup));
        Assert.Contains("invoices", page.Find("#sync-preview").TextContent);
        Assert.NotNull(page.Find(".loses-documentation"));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/import/sqlite/d1/preview"));
        Assert.False(WasSent(HttpMethod.Put, "/api/v1/import/sqlite/d1"));

        page.FindAll("#sync-preview button").First(b => b.TextContent.Contains("Remplacer le fichier et synchroniser")).Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/import/sqlite/d1")));
        Assert.False(WasSent(HttpMethod.Post, "/api/v1/DatabaseInfo/d1/sync"));
        Assert.Empty(page.FindAll("#sync-preview"));
    }

    [BunitFact]
    public void Cancelling_the_preview_sends_nothing_and_an_sql_server_base_has_no_replace_button()
    {
        Nav.NavigateTo("/databases/d2");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d2"));
        page.WaitForAssertion(() => Assert.Contains("RH", page.Markup));

        Assert.Empty(page.FindAll(".replace-file-input"));
    }
}
