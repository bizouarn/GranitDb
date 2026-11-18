using System.Net;
using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Pages;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

/// <summary>Édition en place : enregistrement automatique en quittant le champ, « Annuler », erreurs.</summary>
public class ClientInlineTextTests : ShellTestBase
{
    private readonly List<string?> _saved = [];
    private Func<string?, Task>? _onSave;

    private IRenderedComponent<InlineText> Render(string? value, bool multiline = true, bool silent = false)
    {
        return Ctx.Render<InlineText>(p => p
            .Add(x => x.Value, value)
            .Add(x => x.Multiline, multiline)
            .Add(x => x.Silent, silent)
            .Add(x => x.OnSave, text =>
            {
                _saved.Add(text);
                return _onSave != null ? _onSave(text) : Task.CompletedTask;
            }));
    }

    private static void Open(IRenderedComponent<InlineText> text) => text.Find(".inline-view").Click();

    private static AngleSharp.Dom.IElement Field(IRenderedComponent<InlineText> text, bool multiline = true) =>
        text.Find(multiline ? ".inline-edit textarea" : ".inline-edit input");

    [BunitFact]
    public void It_shows_the_text_or_a_placeholder_and_a_click_opens_the_field_with_the_current_value()
    {
        var empty = Render(null);
        Assert.Contains("Ajouter une description", empty.Find(".inline-placeholder").TextContent);

        var filled = Render("Les clients");
        Assert.Equal("Les clients", filled.Find(".inline-text").TextContent);
        Open(filled);

        Assert.Equal("Les clients", Field(filled).GetAttribute("value") ?? Field(filled).TextContent);
        Assert.Contains("Entrée pour enregistrer", filled.Markup);
        Assert.Empty(filled.FindAll("button.inline-save")); // plus de bouton : l'enregistrement est automatique
    }

    [BunitFact]
    public void It_opens_from_the_keyboard()
    {
        var text = Render("x");

        text.Find(".inline-view").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.NotEmpty(text.FindAll(".inline-edit"));
    }

    [BunitFact]
    public void Leaving_the_field_saves_the_trimmed_text_and_offers_to_undo()
    {
        var text = Render("avant");
        Open(text);

        Field(text).Input("  après  ");
        Field(text).Blur();

        text.WaitForAssertion(() => Assert.Equal(["après"], _saved));
        Assert.Empty(text.FindAll(".inline-edit"));
        var toast = Assert.Single(Notices.Toasts);
        Assert.Equal("Modification enregistrée.", toast.Message);
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.Equal(["après", "avant"], _saved); // annuler rétablit l'ancien texte
    }

    [BunitFact]
    public void Enter_saves_a_single_line_field_and_shift_enter_only_adds_a_line_in_a_multiline_one()
    {
        var single = Render("a", multiline: false);
        Open(single);
        Field(single, multiline: false).Input("b");
        Field(single, multiline: false).KeyDown(new KeyboardEventArgs { Key = "Enter" });
        single.WaitForAssertion(() => Assert.Equal(["b"], _saved));

        var multi = Render("a");
        Open(multi);
        Field(multi).Input("a\nb");
        Field(multi).KeyDown(new KeyboardEventArgs { Key = "Enter", ShiftKey = true });
        Assert.Equal(["b"], _saved); // rien de plus
        Field(multi).KeyDown(new KeyboardEventArgs { Key = "Enter" });
        multi.WaitForAssertion(() => Assert.Equal(["b", "a\nb"], _saved));
    }

    [BunitFact]
    public void An_unchanged_text_is_not_sent_and_the_field_just_closes()
    {
        var text = Render("pareil");
        Open(text);

        Field(text).Blur();

        Assert.Empty(_saved);
        Assert.Empty(Notices.Toasts);
        Assert.Empty(text.FindAll(".inline-edit"));
    }

    [BunitFact]
    public void A_blank_text_after_an_existing_one_clears_it_and_a_blank_over_nothing_is_unchanged()
    {
        var text = Render("à effacer");
        Open(text);
        Field(text).Input("   ");
        Field(text).Blur();
        text.WaitForAssertion(() => Assert.Equal([(string?)null], _saved));
        Assert.Equal("Description effacée.", Assert.Single(Notices.Toasts).Message);

        _saved.Clear();
        var nothing = Render("   ");
        Open(nothing);
        Field(nothing).Blur();
        Assert.Empty(_saved);
    }

    [BunitFact]
    public void Escape_cancels_without_saving_even_if_the_field_then_loses_focus()
    {
        var text = Render("a");
        Open(text);
        Field(text).Input("brouillon");

        Field(text).KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(text.FindAll(".inline-edit"));
        Assert.Empty(_saved);
        Assert.Equal("a", text.Find(".inline-text").TextContent);
        Open(text); // on peut rouvrir : le brouillon n'a pas été gardé
        Assert.Equal("a", Field(text).GetAttribute("value") ?? Field(text).TextContent);
    }

    [BunitFact]
    public void A_refusal_shows_the_message_keeps_the_field_open_and_shows_no_confirmation()
    {
        var fail = true;
        _onSave = _ => fail ? throw new ApiException("Le texte est trop long.") : Task.CompletedTask;
        var text = Render("a");
        Open(text);
        Field(text).Input("trop long");

        Field(text).Blur();

        text.WaitForAssertion(() => Assert.Contains("Le texte est trop long.", text.Find(".inline-error").TextContent));
        Assert.NotEmpty(text.FindAll(".inline-edit"));
        Assert.Empty(Notices.Toasts);
        fail = false;
        Field(text).Blur(); // un nouvel essai fonctionne
        text.WaitForAssertion(() => Assert.Empty(text.FindAll(".inline-edit")));
        Assert.Single(Notices.Toasts);
    }

    [BunitFact]
    public void A_silent_field_shows_no_confirmation()
    {
        var text = Render("a", silent: true);
        Open(text);

        Field(text).Input("b");
        Field(text).Blur();

        text.WaitForAssertion(() => Assert.Equal(["b"], _saved));
        Assert.Empty(Notices.Toasts);
    }
}

/// <summary>La page d'une base et son onglet « une table » : titres, colonne mise en évidence, annulations.</summary>
public class ClientTableTabTests : ShellTestBase
{
    private IRenderedComponent<DatabaseDetail> OpenDatabase(string? query = null)
    {
        Nav.NavigateTo("/databases/d1" + query);
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        return page;
    }

    private IRenderedComponent<DatabaseDetail> OpenTable(string table = "t1", string? query = null)
    {
        Nav.NavigateTo($"/databases/d1/tables/{table}{query}");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1").Add(x => x.TableRoute, table));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-column]")));
        return page;
    }

    [BunitFact]
    public void The_database_page_titles_its_tab_with_the_database_name()
    {
        OpenDatabase();

        Assert.Equal("Boutique", State.Tabs.Single(t => t.Href == "/databases/d1").Title);
    }

    [BunitFact]
    public void A_table_tab_shows_only_that_table_open_with_its_breadcrumb_and_no_database_chrome()
    {
        var page = OpenTable("t1");

        Assert.Equal("customers", State.Tabs.Single(t => t.Href == "/databases/d1/tables/t1").Title);
        Assert.Single(page.FindAll(".mud-expand-panel"));
        Assert.Equal(["id", "email"], page.FindAll("tr[data-column]").Select(r => r.GetAttribute("data-column")!).ToArray());
        Assert.Empty(page.FindAll("#completeness"));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Resynchroniser"));
        Assert.Empty(page.FindAll("input[type=text]"));
        var crumbs = page.Find(".mud-breadcrumbs").TextContent;
        Assert.Contains("Boutique", crumbs);
        Assert.Contains("customers", crumbs);
        Assert.Contains("/databases/d1", page.FindAll(".mud-breadcrumbs a").Select(a => a.GetAttribute("href")));
    }

    [BunitFact]
    public void A_table_tab_for_an_unknown_table_says_so_and_a_database_without_it_is_unaffected()
    {
        Nav.NavigateTo("/databases/d1/tables/nope");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1").Add(x => x.TableRoute, "nope"));

        page.WaitForAssertion(() => Assert.Contains("Table introuvable dans cette base", page.Markup));
        Assert.Empty(page.FindAll(".mud-expand-panel"));
    }

    [BunitFact]
    public void The_column_from_a_link_is_highlighted_and_scrolled_into_view()
    {
        var page = OpenTable("t1", "?column=c2");

        Assert.Contains("column-focus", page.Find("tr[data-column='email']").ClassName);
        Assert.DoesNotContain("column-focus", page.Find("tr[data-column='id']").ClassName ?? "");
        page.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.scrollToId" && (string)i.Arguments[0]! == "c-c2"));
        Assert.NotNull(page.Find("#c-c2"));
    }

    [BunitFact]
    public void The_table_deep_link_of_the_database_page_still_opens_and_scrolls_to_the_table()
    {
        var page = OpenDatabase("?table=t2");

        Assert.Single(page.FindAll(".mud-expand-panel.mud-panel-expanded"));
        page.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.scrollToId" && (string)i.Arguments[0]! == "t-t2"));
    }

    // --- Effacer une description, informer les autres vues ---

    [BunitFact]
    public void Clearing_a_table_description_sends_null_while_keeping_its_notes()
    {
        var page = OpenTable("t1");

        page.Find("#t-t1 .inline-view").Click(); // « Les clients »
        page.Find(".inline-edit textarea").Input("");
        page.Find(".inline-edit textarea").Blur();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/TableInfo/t1/documentation")));
        var body = BodyOf(HttpMethod.Put, "/api/v1/TableInfo/t1/documentation");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("description").ValueKind);
    }

    [BunitFact]
    public void An_edit_tells_the_other_views_but_the_page_does_not_reread_its_own_change()
    {
        var seen = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => seen.Add((db, source));
        var page = OpenTable("t1");
        var reads = () => Requests(HttpMethod.Get, "/api/v1/TableInfo/database/d1").Count();
        var before = reads();

        page.Find("#t-t1 .inline-view").Click();
        page.Find(".inline-edit textarea").Input("Nouveau");
        page.Find(".inline-edit textarea").Blur();

        page.WaitForAssertion(() => Assert.Contains(seen, s => s.Item1 == "d1" && ReferenceEquals(s.Item2, page.Instance)));
        Assert.Equal(before, reads());
    }

    [BunitFact]
    public void A_change_made_elsewhere_is_reread_without_a_reload_flash()
    {
        var page = OpenTable("t1");
        var reads = () => Requests(HttpMethod.Get, "/api/v1/TableInfo/database/d1").Count();
        var before = reads();

        State.NotifyCatalogChanged("d2"); // une autre base : sans effet
        State.NotifyCatalogChanged("d1", new object());

        page.WaitForAssertion(() => Assert.Equal(before + 1, reads()));
        Assert.NotEmpty(page.FindAll("tr[data-column]")); // le contenu n'a pas disparu pendant la relecture
    }

    // --- Annulations ---

    [BunitFact]
    public void Deleting_a_manual_relation_can_be_undone_by_recreating_it()
    {
        Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/database/d1"
            ? FakeApi.Json(BoutiqueTables.Replace("""{"id":"r1","firstId":"c1","secondId":"c3","relationType":"One-to-Many","metaInfo":"auto:fk"}""",
                """{"id":"r9","firstId":"c1","secondId":"c3","relationType":"One-to-One","description":"Un seul"}"""))
            : null;
        var page = OpenTable("t1");

        page.Find("#t-t1 [data-relation='r9'] button.relation-delete").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/RelationInfo/r9")));
        var toast = Assert.Single(Notices.Toasts, t => t.Message == "Relation supprimée.");
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        var recreated = BodyOf(HttpMethod.Post, "/api/v1/RelationInfo");
        Assert.Equal("c1", recreated.GetProperty("firstId").GetString());
        Assert.Equal("c3", recreated.GetProperty("secondId").GetString());
        Assert.Equal("One-to-One", recreated.GetProperty("relationType").GetString());
        Assert.Equal("Un seul", recreated.GetProperty("description").GetString());
    }

    [BunitFact]
    public void Changing_a_relation_type_can_be_undone()
    {
        var page = OpenTable("t1");

        page.Find("#t-t1 [data-relation='r1'] button.relation-edit").Click();
        page.Find("#t-t1 [data-relation='r1'] select").Change("One-to-One");

        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Type de la relation modifié."));
        Assert.Equal("One-to-One", BodyOf(HttpMethod.Put, "/api/v1/RelationInfo/r1").GetProperty("relationType").GetString());
        var toast = Notices.Toasts.Single(t => t.Message == "Type de la relation modifié.");
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.Equal("One-to-Many", BodyOf(HttpMethod.Put, "/api/v1/RelationInfo/r1").GetProperty("relationType").GetString());
    }

    [BunitFact]
    public void Removing_a_tag_and_toggling_sensitivity_can_both_be_undone()
    {
        var page = OpenTable("t1");

        page.Find("#t-t1 .tag-chip .mud-chip-close-button").Click();
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Étiquette RGPD retirée."));
        Notices.UndoAsync(Notices.Toasts.Single(t => t.Message == "Étiquette RGPD retirée.").Id).GetAwaiter().GetResult();
        Assert.Equal("RGPD", BodyOf(HttpMethod.Post, "/api/v1/TagInfo/attach").GetProperty("name").GetString());

        page.Find("tr[data-column='email'] button.sensitive-toggle").Click();
        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message.Contains("n'est plus marquée comme sensible")));
        Assert.False(BodyOf(HttpMethod.Put, "/api/v1/ColumnInfo/c2/sensitivity").GetProperty("isSensitive").GetBoolean());
        Notices.UndoAsync(Notices.Toasts.Single(t => t.Message.Contains("n'est plus marquée")).Id).GetAwaiter().GetResult();
        Assert.True(BodyOf(HttpMethod.Put, "/api/v1/ColumnInfo/c2/sensitivity").GetProperty("isSensitive").GetBoolean());
    }
}

/// <summary>Le diagramme ouvert depuis l'explorateur : domaine préchoisi, titre, annulations.</summary>
public class ClientDiagramWorkspaceTests : ShellTestBase
{
    private IRenderedComponent<Diagram> Open(string? query = null)
    {
        Nav.NavigateTo("/databases/d1/diagram" + query);
        var page = Ctx.Render<Diagram>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("g.er-node")));
        return page;
    }

    private static string[] Shown(IRenderedComponent<Diagram> page) =>
        page.FindAll("g.er-node").Select(n => n.GetAttribute("data-table")!).Order().ToArray();

    [BunitFact]
    public void The_tab_is_titled_with_the_database_and_all_tables_are_shown_by_default()
    {
        var page = Open();

        Assert.Equal("Diagramme · Boutique", State.Tabs.Single(t => t.Href == "/databases/d1/diagram").Title);
        Assert.Equal(["customers", "orders", "tags"], Shown(page));
        Assert.Contains("domain-selected", page.Find(".domain-all").ClassName);
    }

    [BunitFact]
    public void A_domain_link_opens_the_diagram_on_that_domain()
    {
        var page = Open("?domain=g1");

        Assert.Equal(["customers", "orders"], Shown(page)); // le domaine (orders) et sa table liée (customers, en pointillés)
        Assert.Contains("domain-selected", page.Find("[data-domain='Ventes']").ClassName);
        Assert.Contains("er-frontier", page.Find("g.er-node[data-table='customers']").ClassName);
        page.WaitForAssertion(() => Assert.Contains(Api.Requests, r => r.RequestUri!.PathAndQuery == "/api/v1/Completeness/database/d1?domainId=g1"));
    }

    [BunitFact]
    public void An_unknown_domain_in_the_link_falls_back_to_all_tables()
    {
        var page = Open("?domain=inconnu");

        Assert.Equal(3, page.FindAll("g.er-node").Count);
        Assert.Contains("domain-selected", page.Find(".domain-all").ClassName);
    }

    [BunitFact]
    public void Deleting_a_domain_can_be_undone_with_its_tables_and_their_places()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo"
            ? FakeApi.Json("""{"id":"g2","databaseId":"d1","name":"Ventes","tables":[]}""", HttpStatusCode.Created)
            : null;
        var page = Open();

        page.Find("[data-domain='Ventes'] button.domain-delete").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1")));
        var toast = Assert.Single(Notices.Toasts, t => t.Message.Contains("Domaine Ventes supprimé"));
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        var created = BodyOf(HttpMethod.Post, "/api/v1/DomainInfo");
        Assert.Equal("Ventes", created.GetProperty("name").GetString());
        Assert.Equal("#1976d2", created.GetProperty("color").GetString());
        var place = BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/g2/tables/t2"); // le domaine recréé a un nouvel identifiant
        Assert.Equal(120, place.GetProperty("x").GetDouble());
    }

    [BunitFact]
    public void Filing_a_table_in_a_domain_can_be_undone()
    {
        var page = Open();

        page.Find("[data-table-item='tags']").DragStart(new DragEventArgs());
        page.Find("[data-domain='Ventes']").Drop(new DragEventArgs());

        page.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "tags ajoutée au domaine Ventes."));
        Notices.UndoAsync(Notices.Toasts.Single(t => t.Message == "tags ajoutée au domaine Ventes.").Id).GetAwaiter().GetResult();
        Assert.True(WasSent(HttpMethod.Delete, "/api/v1/DomainInfo/g1/tables/t3"));
    }

    [BunitFact]
    public void Domain_changes_are_announced_to_the_explorer_and_the_other_tabs()
    {
        var seen = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => seen.Add((db, source));
        var page = Open();

        page.Find(".new-domain-name input").Input("RH");
        page.Find("button.new-domain-add").Click();

        page.WaitForAssertion(() => Assert.Contains(seen, s => s.Item1 == "d1" && ReferenceEquals(s.Item2, page.Instance)));
    }
}

/// <summary>Accueil : parcours guidé, import confirmé, recherche titrée.</summary>
public class ClientHomeWorkspaceTests : ShellTestBase
{
    [BunitFact]
    public void An_empty_catalog_shows_the_three_steps_to_get_started()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo" ? FakeApi.Json("[]") : null;

        var page = Ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("Aucune base pour le moment", page.Markup));
        var steps = page.FindAll(".welcome-steps li").Select(li => li.QuerySelector("strong")!.TextContent).ToArray();
        Assert.Equal(["1. Importez", "2. Explorez et documentez", "3. Partagez"], steps);
        Assert.Contains("Ctrl+K", page.Markup);
    }

    [BunitFact]
    public void A_catalog_with_databases_hides_the_steps_and_offers_their_diagrams()
    {
        var page = Ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("Boutique", page.Markup));
        Assert.Empty(page.FindAll(".welcome-steps"));
        Assert.Equal(["/databases/d1/diagram", "/databases/d2/diagram"], page.FindAll("a[href$='/diagram']").Select(a => a.GetAttribute("href")!).Order().ToArray());
    }

    [BunitFact]
    public void A_successful_import_confirms_with_what_was_found_and_refreshes_the_explorer()
    {
        var seen = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => seen.Add((db, source));
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/import/sqlite"
            ? FakeApi.Json("""{"database":{"id":"d5","name":"nouvelle","type":"Sqlite"},"sync":{"tablesAdded":4,"columnsAdded":17}}""", HttpStatusCode.Created)
            : null;
        var page = Ctx.Render<Databases>();
        page.WaitForAssertion(() => Assert.Contains("Boutique", page.Markup));

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "nouvelle.db"));

        page.WaitForAssertion(() => Assert.EndsWith("/databases/d5", Nav.Uri));
        Assert.Contains(Notices.Toasts, t => t.Message == "Base nouvelle importée : 4 table(s), 17 colonne(s).");
        Assert.Contains(seen, s => s.Item1 == null); // la liste des bases a changé
    }

    [BunitFact]
    public void The_search_page_titles_its_tab_with_the_term()
    {
        Nav.NavigateTo("/search?q=custom");

        Ctx.Render<Search>();

        Assert.Equal("Recherche : custom", State.Tabs.Single(t => t.Href == "/search?q=custom").Title);
    }
}
