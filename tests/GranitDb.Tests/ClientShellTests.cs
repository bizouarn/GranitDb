using System.Net;
using Bunit;
using GranitDb.Client.Layout;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

namespace GranitDb.Tests;

/// <summary>Onglets : une page ouverte = un onglet.</summary>
public class ClientTabStripTests : ShellTestBase
{
    private IRenderedComponent<TabStrip> Render()
    {
        State.Open("/databases/d1");
        State.Describe("/databases/d1", "Boutique");
        State.Open("/databases/d1/tables/t1");
        State.Describe("/databases/d1/tables/t1", "customers");
        State.Open("/search?q=ab");
        State.Open("/databases/d1/tables/t1");
        return Ctx.Render<TabStrip>();
    }

    private static string[] Titles(IRenderedComponent<TabStrip> strip) =>
        strip.FindAll(".tab").Select(t => t.GetAttribute("data-tab")!).ToArray();

    [BunitFact]
    public void Each_open_page_is_a_tab_in_an_accessible_tablist_and_the_shown_one_is_selected()
    {
        var strip = Render();

        Assert.Equal(["Boutique", "customers", "Recherche"], Titles(strip));
        Assert.Equal("tablist", strip.Find("[role=tablist]").GetAttribute("role"));
        var tabs = strip.FindAll("button[role=tab]");
        Assert.Equal(["/databases/d1", "/databases/d1/tables/t1", "/search?q=ab"], tabs.Select(t => t.GetAttribute("data-href")!).ToArray());
        Assert.Equal(["false", "true", "false"], tabs.Select(t => t.GetAttribute("aria-selected")!).ToArray());
        Assert.Equal(["-1", "0", "-1"], tabs.Select(t => t.GetAttribute("tabindex")!).ToArray());
        Assert.Contains("tab-active", strip.Find("[data-tab='customers']").ClassName);
    }

    [BunitFact]
    public void Clicking_a_tab_opens_its_page()
    {
        var strip = Render();

        strip.Find("[data-tab='Boutique'] button[role=tab]").Click();

        Assert.EndsWith("/databases/d1", Nav.Uri);
    }

    [BunitFact]
    public void The_strip_follows_the_workspace_when_a_title_or_a_tab_changes()
    {
        var strip = Render();

        State.Describe("/search?q=ab", "Recherche : ab");
        State.Open("/databases/d2");

        strip.WaitForAssertion(() => Assert.Equal(["Boutique", "customers", "Recherche : ab", "Base"], Titles(strip)));
    }

    [BunitFact]
    public void Closing_the_shown_tab_goes_to_its_neighbour_and_an_other_tab_leaves_the_user_where_they_are()
    {
        var strip = Render();
        Nav.NavigateTo("/databases/d1/tables/t1");

        strip.Find("button[aria-label='Fermer Boutique']").Click();
        Assert.EndsWith("/databases/d1/tables/t1", Nav.Uri);
        Assert.Equal(["customers", "Recherche"], Titles(strip));

        strip.Find("button[aria-label='Fermer customers']").Click();
        Assert.EndsWith("/search?q=ab", Nav.Uri); // l'onglet affiché a été fermé : on passe au voisin
    }

    [BunitFact]
    public void Closing_the_last_tab_goes_home()
    {
        State.Open("/databases/d1");
        var strip = Ctx.Render<TabStrip>();

        strip.Find("button[aria-label^='Fermer']").Click();

        Assert.EndsWith("/", Nav.Uri);
        Assert.Empty(strip.FindAll(".tab"));
    }

    [BunitFact]
    public void A_pinned_tab_has_no_close_button_and_can_be_unpinned()
    {
        var strip = Render();

        strip.Find("button[aria-label='Épingler Boutique']").Click();

        Assert.Empty(strip.FindAll("button[aria-label='Fermer Boutique']"));
        Assert.Contains("tab-pinned", strip.Find("[data-tab='Boutique']").ClassName);
        strip.Find("button[aria-label='Désépingler Boutique']").Click();
        Assert.NotEmpty(strip.FindAll("button[aria-label='Fermer Boutique']"));
    }

    [BunitFact]
    public void Middle_click_closes_a_tab_and_other_buttons_do_not()
    {
        var strip = Render();

        strip.Find("[data-tab='Recherche'] button[role=tab]").MouseDown(new MouseEventArgs { Button = 2 });
        strip.Find("[data-tab='Recherche'] button[role=tab]").MouseDown(new MouseEventArgs { Button = 0 });
        Assert.Equal(3, strip.FindAll(".tab").Count);
        strip.Find("[data-tab='Recherche'] button[role=tab]").MouseDown(new MouseEventArgs { Button = 1 });

        Assert.DoesNotContain("Recherche", Titles(strip));
    }

    [BunitFact]
    public void Arrow_keys_switch_tabs_circularly_and_delete_closes_the_tab()
    {
        var strip = Render();

        strip.Find("[data-tab='customers'] button[role=tab]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.EndsWith("/search?q=ab", Nav.Uri);
        strip.Find("[data-tab='Recherche'] button[role=tab]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.EndsWith("/databases/d1", Nav.Uri); // retour au premier
        strip.Find("[data-tab='Boutique'] button[role=tab]").KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        Assert.EndsWith("/search?q=ab", Nav.Uri);

        strip.Find("[data-tab='Recherche'] button[role=tab]").KeyDown(new KeyboardEventArgs { Key = "Delete" });
        Assert.DoesNotContain("Recherche", Titles(strip));
    }

    [BunitFact]
    public void Tabs_are_reordered_by_dragging()
    {
        var strip = Render();

        strip.Find("[data-tab='Recherche']").DragStart(new DragEventArgs());
        strip.Find("[data-tab='Boutique']").Drop(new DragEventArgs());

        Assert.Equal(["Recherche", "Boutique", "customers"], Titles(strip));
        strip.Find("[data-tab='customers']").Drop(new DragEventArgs()); // rien n'est accroché : sans effet
        Assert.Equal(["Recherche", "Boutique", "customers"], Titles(strip));
    }
}

/// <summary>Notifications affichées, avec « Annuler ».</summary>
public class ClientToastHostTests : ShellTestBase
{
    [BunitFact]
    public void A_message_is_shown_in_a_polite_live_region_and_can_be_closed()
    {
        var host = Ctx.Render<ToastHost>();
        Assert.Equal("status", host.Find(".toast-host").GetAttribute("role"));
        Assert.Equal("polite", host.Find(".toast-host").GetAttribute("aria-live"));

        var toast = Notices.Show("Enregistré.");

        host.WaitForAssertion(() => Assert.Contains("Enregistré.", host.Markup));
        Assert.Empty(host.FindAll("button.toast-undo"));
        Notices.Dismiss(toast.Id);
        host.WaitForAssertion(() => Assert.DoesNotContain("Enregistré.", host.Markup));
    }

    [BunitFact]
    public void The_undo_button_runs_the_undo_and_confirms()
    {
        var undone = 0;
        var host = Ctx.Render<ToastHost>();
        Notices.Show("Relation supprimée.", Severity.Success, () =>
        {
            undone++;
            return Task.CompletedTask;
        });
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("button.toast-undo")));

        host.Find("button.toast-undo").Click();

        host.WaitForAssertion(() => Assert.Contains("Action annulée.", host.Markup));
        Assert.Equal(1, undone);
        Assert.DoesNotContain("Relation supprimée.", host.Markup);
    }

    [BunitFact]
    public void Errors_are_shown_as_alerts()
    {
        var host = Ctx.Render<ToastHost>();

        Notices.Error("Conflit");

        host.WaitForAssertion(() => Assert.Contains("Conflit", host.Find(".toast").TextContent));
        Assert.Equal("alert", host.Find(".toast").GetAttribute("role"));
    }
}

/// <summary>Panneau de détail : documenter l'élément sélectionné sans quitter la page.</summary>
public class ClientDetailPanelTests : ShellTestBase
{
    private IRenderedComponent<DetailPanel> Open(SelectionItem? selection = null)
    {
        var panel = Ctx.Render<DetailPanel>();
        if (selection != null) State.Select(selection);
        return panel;
    }

    private static void Edit(IRenderedComponent<DetailPanel> panel, string text)
    {
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".inline-edit")));
        panel.Find(".inline-edit textarea").Input(text);
        panel.Find(".inline-edit textarea").Blur();
    }

    [BunitFact]
    public void Without_a_selection_it_explains_what_to_do()
    {
        var panel = Open();

        Assert.Contains("Sélectionnez une base, une table ou une colonne", panel.Markup);
        Assert.Equal("Détails de la sélection", panel.Find("aside").GetAttribute("aria-label"));
    }

    [BunitFact]
    public void A_database_shows_its_description_completeness_and_shortcuts()
    {
        var panel = Open(new SelectionItem("database", "d1", "d1"));

        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=database]")));
        Assert.Contains("Boutique", panel.Find("[data-detail=database]").TextContent);
        Assert.Contains("Ma boutique", panel.Markup);
        Assert.Contains("Documentation : 14 % (1 / 7)", panel.Find(".detail-completeness").TextContent);
        Assert.Contains("3 table(s)", panel.Markup);
        Assert.Contains("/databases/d1/diagram", panel.FindAll("a").Select(a => a.GetAttribute("href")));
    }

    [BunitFact]
    public void A_table_shows_its_description_tags_completeness_and_impact()
    {
        var panel = Open(new SelectionItem("table", "t1", "d1", "t1"));

        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=table]")));
        var detail = panel.Find("[data-detail=table]").TextContent;
        Assert.Contains("customers", detail);
        Assert.Contains("Boutique · Table · 2 colonne(s)", detail);
        Assert.Contains("Les clients", detail);
        Assert.Contains("RGPD", detail);
        Assert.Contains("Documentation : 33 % (1 / 3)", detail);
        Assert.Contains("1 colonne(s) sensible(s) sans description", detail);
        Assert.Contains("1 élément(s) en dépendent, il dépend de 0.", panel.Find(".impact-summary").TextContent);
        Assert.Equal(["orders"], panel.FindAll(".impact-list li").Select(li => li.TextContent).ToArray());
    }

    [BunitFact]
    public void A_table_shows_the_domains_it_belongs_to_as_links()
    {
        var panel = Open(new SelectionItem("table", "t2", "d1", "t2"));

        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=table]")));
        var chip = Assert.Single(panel.FindAll("a.mud-chip"), c => c.TextContent.Contains("Ventes"));
        Assert.Equal("/databases/d1/diagram?domain=g1", chip.GetAttribute("href"));
    }

    [BunitFact]
    public void A_column_shows_its_type_flags_sensitivity_and_a_link_to_its_table()
    {
        var panel = Open(new SelectionItem("column", "c2", "d1", "t1"));

        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=column]")));
        var detail = panel.Find("[data-detail=column]").TextContent;
        Assert.Contains("email", detail);
        Assert.Contains("Boutique › customers", detail);
        Assert.Contains("TEXT", detail);
        Assert.Contains("non nullable", detail);
        Assert.Equal("true", panel.Find("button.detail-sensitive").GetAttribute("aria-pressed"));
        Assert.Contains("Données sensibles", panel.Find("button.detail-sensitive").TextContent);
        Assert.Contains("Aucune relation", panel.Find(".impact-summary").TextContent);
        Assert.Contains("/databases/d1/tables/t1?column=c2", panel.FindAll("a").Select(a => a.GetAttribute("href")));
    }

    [BunitFact]
    public void The_primary_key_flag_is_shown_for_a_key_column()
    {
        var panel = Open(new SelectionItem("column", "c1", "d1", "t1"));

        panel.WaitForAssertion(() => Assert.Contains("clé primaire", panel.Markup));
        Assert.Contains("Marquer comme sensible", panel.Find("button.detail-sensitive").TextContent);
    }

    [BunitFact]
    public void Editing_the_description_saves_it_tells_the_other_views_and_offers_an_undo()
    {
        var changes = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => changes.Add((db, source));
        var panel = Open(new SelectionItem("table", "t1", "d1", "t1"));
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=table]")));

        panel.Find("[data-detail=table] .inline-view").Click();
        Edit(panel, "Clients de la boutique");

        panel.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/TableInfo/t1/documentation")));
        Assert.Equal("Clients de la boutique", BodyOf(HttpMethod.Put, "/api/v1/TableInfo/t1/documentation").GetProperty("description").GetString());
        Assert.Contains(changes, c => c.Item1 == "d1" && c.Item2 != null);
        var toast = Assert.Single(Notices.Toasts, t => t.Message == "Modification enregistrée.");
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.Equal("Les clients", BodyOf(HttpMethod.Put, "/api/v1/TableInfo/t1/documentation").GetProperty("description").GetString());
    }

    [BunitFact]
    public void The_sensitive_button_toggles_the_mark_and_its_undo_restores_it()
    {
        var panel = Open(new SelectionItem("column", "c2", "d1", "t1"));
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("button.detail-sensitive")));

        panel.Find("button.detail-sensitive").Click();

        panel.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/ColumnInfo/c2/sensitivity")));
        Assert.False(BodyOf(HttpMethod.Put, "/api/v1/ColumnInfo/c2/sensitivity").GetProperty("isSensitive").GetBoolean());
        Assert.Equal("false", panel.Find("button.detail-sensitive").GetAttribute("aria-pressed"));
        var toast = Assert.Single(Notices.Toasts, t => t.Message.Contains("n'est plus marquée"));
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.True(BodyOf(HttpMethod.Put, "/api/v1/ColumnInfo/c2/sensitivity").GetProperty("isSensitive").GetBoolean());
    }

    [BunitFact]
    public void A_refused_sensitivity_change_is_reported_and_leaves_the_mark()
    {
        Override = r => r.RequestUri!.AbsolutePath.EndsWith("/sensitivity")
            ? FakeApi.Json("""{"title":"Colonne inconnue","status":404}""", HttpStatusCode.NotFound)
            : null;
        var panel = Open(new SelectionItem("column", "c2", "d1", "t1"));
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("button.detail-sensitive")));

        panel.Find("button.detail-sensitive").Click();

        panel.WaitForAssertion(() => Assert.Contains(Notices.Toasts, t => t.Message == "Colonne inconnue"));
        Assert.Equal("true", panel.Find("button.detail-sensitive").GetAttribute("aria-pressed"));
    }

    [BunitFact]
    public void Tags_are_attached_and_detached_from_the_panel_with_an_undo_for_the_removal()
    {
        var panel = Open(new SelectionItem("table", "t1", "d1", "t1"));
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=table]")));

        panel.Find("button.tag-add").Click();
        panel.Find(".tag-input input").Input("legacy");
        panel.Find(".tag-input input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        panel.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Post, "/api/v1/TagInfo/attach")));
        Assert.Equal("legacy", BodyOf(HttpMethod.Post, "/api/v1/TagInfo/attach").GetProperty("name").GetString());

        panel.Find(".tag-chip .mud-chip-close-button").Click();
        panel.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Delete, "/api/v1/TagInfo/g1/links/table/t1")));
        var toast = Assert.Single(Notices.Toasts, t => t.Message == "Étiquette RGPD retirée.");
        Notices.UndoAsync(toast.Id).GetAwaiter().GetResult();
        Assert.Equal("RGPD", BodyOf(HttpMethod.Post, "/api/v1/TagInfo/attach").GetProperty("name").GetString());
    }

    [BunitFact]
    public void A_selection_that_no_longer_exists_says_so()
    {
        var panel = Open(new SelectionItem("table", "t-disparue", "d1", "t-disparue"));

        panel.WaitForAssertion(() => Assert.Contains("Cet élément n'existe plus", panel.Markup));
        State.Select(new SelectionItem("database", "d-inconnue", "d-inconnue"));
        panel.WaitForAssertion(() => Assert.Contains("Cet élément n'existe plus", panel.Markup));
    }

    [BunitFact]
    public void A_change_made_elsewhere_is_reread_but_the_panels_own_changes_are_not()
    {
        var panel = Open(new SelectionItem("database", "d1", "d1"));
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[data-detail=database]")));
        var reads = () => Requests(HttpMethod.Get, "/api/v1/DatabaseInfo/d1").Count();
        var before = reads();

        State.NotifyCatalogChanged("d2", null); // une autre base : sans effet
        State.NotifyCatalogChanged("d1", this); // émetteur inconnu : il faut relire
        panel.WaitForAssertion(() => Assert.Equal(before + 1, reads()));
        State.NotifyCatalogChanged("d1", panel.Instance); // le panneau lui-même : rien à relire
        Assert.Equal(before + 1, reads());
    }

    [BunitFact]
    public void Switching_the_selection_shows_the_latest_one()
    {
        var panel = Open(new SelectionItem("table", "t1", "d1", "t1"));
        panel.WaitForAssertion(() => Assert.Contains("customers", panel.Find("[data-detail=table]").TextContent));

        State.Select(new SelectionItem("table", "t2", "d1", "t2"));

        panel.WaitForAssertion(() => Assert.Contains("orders", panel.Find("[data-detail=table]").TextContent));
        Assert.DoesNotContain("Les clients", panel.Markup);
    }
}

/// <summary>Palette de commandes (Ctrl+K).</summary>
public class ClientCommandPaletteTests : ShellTestBase
{
    private int _themeToggles;

    private IRenderedComponent<CommandPalette> Render()
    {
        return Ctx.Render<CommandPalette>(p => p.Add(x => x.OnToggleTheme, EventCallback.Factory.Create(this, () => _themeToggles++)));
    }

    private IRenderedComponent<CommandPalette> Open()
    {
        var palette = Render();
        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        palette.WaitForAssertion(() => Assert.Contains("Ouvrir Boutique", palette.Markup)); // les bases sont chargées
        return palette;
    }

    private static string[] Labels(IRenderedComponent<CommandPalette> palette) =>
        palette.FindAll(".palette-label").Select(l => l.TextContent).ToArray();

    private static void Type(IRenderedComponent<CommandPalette> palette, string text) => palette.Find("#palette-input").Input(text);

    /// <summary>Choisit une proposition par son libellé (clic).</summary>
    private static void Choose(IRenderedComponent<CommandPalette> palette, string label) =>
        palette.FindAll(".palette-item").First(i => i.QuerySelector(".palette-label")!.TextContent == label).Click();

    private static void Press(IRenderedComponent<CommandPalette> palette, string key) =>
        palette.Find(".palette").KeyDown(new KeyboardEventArgs { Key = key });

    [BunitFact]
    public void It_is_closed_until_asked_and_then_is_an_accessible_dialog_with_the_focus_in_the_field()
    {
        var palette = Render();
        Assert.Empty(palette.FindAll(".palette"));

        State.SetPalette(true);

        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll("[role=dialog]")));
        Assert.Equal("true", palette.Find("[role=dialog]").GetAttribute("aria-modal"));
        Assert.Equal("combobox", palette.Find("#palette-input").GetAttribute("role"));
        Assert.Equal("listbox", palette.Find("#palette-list").GetAttribute("role"));
        palette.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.focus" && (string)i.Arguments[0]! == "#palette-input"));
    }

    [BunitFact]
    public void Actions_and_databases_are_listed_with_their_shortcuts()
    {
        var palette = Open();

        var labels = Labels(palette);
        Assert.Contains("Importer une base SQLite", labels);
        Assert.Contains("Aller à l'accueil", labels);
        Assert.Contains("Fermer l'onglet", labels);
        Assert.Contains("Basculer le thème clair / sombre", labels);
        Assert.Contains("Ouvrir Boutique", labels);
        Assert.Contains("Ouvrir RH", labels);
        Assert.Contains("Alt+W", palette.Markup);
        Assert.Contains("Ctrl+B", palette.Markup);
        Assert.Equal(["Actions", "Bases"], palette.FindAll(".palette-group").Select(g => g.TextContent).ToArray());
        Assert.Equal("true", palette.Find("#palette-item-0").GetAttribute("aria-selected"));
    }

    [BunitFact]
    public void Typing_filters_the_actions_and_the_databases()
    {
        var palette = Open();

        Type(palette, "thème");

        Assert.Contains("Basculer le thème clair / sombre", Labels(palette));
        Assert.DoesNotContain("Importer une base SQLite", Labels(palette));
        Assert.DoesNotContain("Ouvrir Boutique", Labels(palette));
        Type(palette, "rh");
        Assert.Contains("Ouvrir RH", Labels(palette));
        Assert.Contains("Diagramme de RH", Labels(palette));
        Assert.DoesNotContain("Ouvrir Boutique", Labels(palette));
    }

    [BunitFact]
    public void The_arrows_move_the_selection_circularly_and_enter_runs_the_chosen_item()
    {
        var palette = Open();
        Type(palette, "Boutique"); // « Rechercher… », « Ouvrir Boutique », « Diagramme de Boutique »

        Press(palette, "ArrowDown");
        Assert.Equal("true", palette.Find("#palette-item-1").GetAttribute("aria-selected"));
        Press(palette, "ArrowDown");
        Press(palette, "ArrowDown"); // dernier : on revient au premier
        Assert.Equal("true", palette.Find("#palette-item-0").GetAttribute("aria-selected"));
        Press(palette, "ArrowUp"); // et en remontant on passe au dernier
        Assert.Equal("true", palette.Find("#palette-item-2").GetAttribute("aria-selected"));
        Press(palette, "Enter");

        Assert.EndsWith("/databases/d1/diagram", Nav.Uri);
        Assert.False(State.PaletteOpen);
        Assert.Empty(palette.FindAll(".palette"));
    }

    [BunitFact]
    public void Hovering_selects_an_item_and_clicking_runs_it()
    {
        var palette = Open();
        Type(palette, "RH");

        palette.Find("#palette-item-1").TriggerEvent("onmouseover", new MouseEventArgs());
        Assert.Equal("true", palette.Find("#palette-item-1").GetAttribute("aria-selected"));
        Assert.Contains("Ouvrir RH", palette.Find("#palette-item-1").TextContent);
        palette.Find("#palette-item-1").Click();

        Assert.EndsWith("/databases/d2", Nav.Uri);
    }

    [BunitFact]
    public void Search_results_are_listed_after_a_short_pause_and_open_the_table_tab()
    {
        var palette = Open();

        Type(palette, "custom");

        palette.WaitForAssertion(() => Assert.Contains("orders.customer_id", Labels(palette)), TimeSpan.FromSeconds(3));
        Assert.Contains("Rechercher « custom » dans tout le catalogue", Labels(palette));
        Assert.Equal(["Actions", "Tables", "Colonnes"], palette.FindAll(".palette-group").Select(g => g.TextContent).ToArray());
        palette.FindAll(".palette-item").First(i => i.TextContent.Contains("orders.customer_id")).Click();
        Assert.EndsWith("/databases/d1/tables/t2?column=c3", Nav.Uri);
    }

    [BunitFact]
    public void The_search_item_goes_to_the_results_page_and_a_hit_opens_its_table()
    {
        var palette = Open();
        Type(palette, "custom");
        palette.WaitForAssertion(() => Assert.Contains("customers", Labels(palette)), TimeSpan.FromSeconds(3));

        palette.FindAll(".palette-item").First(i => i.TextContent.Contains("Rechercher")).Click();

        palette.WaitForAssertion(() => Assert.EndsWith("/search?q=custom", Nav.Uri));
    }

    [BunitFact]
    public void A_single_character_is_not_searched_and_a_failed_search_leaves_the_actions()
    {
        var palette = Open();
        Type(palette, "c");
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath == "/api/v1/Search");

        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Search" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        Type(palette, "accueil");
        palette.WaitForAssertion(() => Assert.Contains(Api.Requests, r => r.RequestUri!.AbsolutePath == "/api/v1/Search"), TimeSpan.FromSeconds(3));

        Assert.Contains("Aller à l'accueil", Labels(palette));
    }

    [BunitFact]
    public void Nothing_matching_says_so()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Search" ? FakeApi.Json("[]") : null;
        var palette = Open();

        Type(palette, "z"); // aucune commande ni base ne contient « z », et un seul caractère ne lance pas de recherche

        Assert.Contains("Aucun résultat.", palette.Find(".palette-empty").TextContent);
        Type(palette, "zzzqq"); // à partir de 2 caractères, la recherche dans tout le catalogue est toujours proposée
        palette.WaitForAssertion(() => Assert.Contains(Api.Requests, r => r.RequestUri!.AbsolutePath == "/api/v1/Search"), TimeSpan.FromSeconds(3));
        Assert.Equal(["Rechercher « zzzqq » dans tout le catalogue"], Labels(palette));
    }

    [BunitFact]
    public void Escape_and_the_backdrop_close_it_and_reopening_starts_clean()
    {
        var palette = Open();
        Type(palette, "RH");

        Press(palette, "Escape");
        Assert.Empty(palette.FindAll(".palette"));

        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        Assert.Equal("", palette.Find("#palette-input").GetAttribute("value") ?? "");
        palette.Find(".palette-backdrop").Click();
        Assert.False(State.PaletteOpen);
    }

    [BunitFact]
    public void The_commands_of_the_current_database_appear_only_inside_one()
    {
        var palette = Open();
        Assert.DoesNotContain("Ouvrir le diagramme de cette base", Labels(palette));

        State.Open("/databases/d1/tables/t1");
        palette.WaitForAssertion(() => Assert.Contains("Ouvrir le diagramme de cette base", Labels(palette)));
        palette.FindAll(".palette-item").First(i => i.TextContent.Contains("Ouvrir le diagramme de cette base")).Click();

        Assert.EndsWith("/databases/d1/diagram", Nav.Uri);
    }

    [BunitFact]
    public void The_workspace_commands_toggle_the_theme_the_panels_and_close_the_tab()
    {
        State.Open("/databases/d1");
        State.Open("/databases/d2");
        var palette = Open();

        Choose(palette, "Basculer le thème clair / sombre");
        Assert.Equal(1, _themeToggles);

        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        Choose(palette, "Afficher ou masquer l'explorateur");
        Assert.False(State.ExplorerOpen);

        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        Choose(palette, "Afficher ou masquer les détails");
        Assert.False(State.DetailOpen);

        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        Choose(palette, "Fermer l'onglet");
        Assert.Equal(["/databases/d1"], State.Tabs.Select(t => t.Href).ToArray());
        Assert.EndsWith("/databases/d1", Nav.Uri);
    }

    [BunitFact]
    public void An_unreachable_api_leaves_the_actions_available()
    {
        Override = _ => throw new HttpRequestException("refused");
        var palette = Render();

        State.SetPalette(true);

        palette.WaitForAssertion(() => Assert.NotEmpty(palette.FindAll(".palette")));
        Assert.Contains("Importer une base SQLite", Labels(palette));
        Assert.DoesNotContain("Ouvrir Boutique", Labels(palette));
    }
}

/// <summary>La mise en page : repères d'accessibilité, onglet de la page courante, persistance, raccourcis.</summary>
public class ClientWorkspaceLayoutTests : ShellTestBase
{
    public ClientWorkspaceLayoutTests() : base(popoverProvider: false)
    {
    }

    private IRenderedComponent<MainLayout> RenderLayout()
    {
        return Ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "contenu"))));
    }

    [BunitFact]
    public void It_offers_a_skip_link_landmarks_and_the_workspace_panels()
    {
        var layout = RenderLayout();

        Assert.Equal("#main-content", layout.Find("a.skip-link").GetAttribute("href"));
        var main = layout.Find("main#main-content");
        Assert.Equal("-1", main.GetAttribute("tabindex"));
        Assert.Contains("contenu", main.TextContent);
        Assert.NotNull(layout.Find("nav[aria-label='Explorateur']"));
        Assert.NotNull(layout.Find("[role=tablist]"));
        Assert.NotNull(layout.Find("aside[aria-label='Détails de la sélection']"));
        Assert.NotNull(layout.Find(".toast-host[role=status]"));
        Assert.NotNull(layout.Find("button[aria-label='Ouvrir la palette de commandes']"));
        Assert.NotNull(layout.Find("button[aria-label='Afficher ou masquer l\\'explorateur']"));
        Assert.NotNull(layout.Find("button[aria-label='Afficher ou masquer les détails']"));
    }

    [BunitFact]
    public void The_current_page_and_every_page_visited_get_a_tab()
    {
        var layout = RenderLayout();
        Assert.Contains(State.Tabs, t => t.Href == "/");

        Nav.NavigateTo("/databases/d1/tables/t1");
        Nav.NavigateTo("/search?q=abc");

        layout.WaitForAssertion(() => Assert.Equal(["/", "/databases/d1/tables/t1", "/search?q=abc"], State.Tabs.Select(t => t.Href).ToArray()));
        Assert.Equal("/search?q=abc", State.ActiveHref);
        layout.WaitForAssertion(() => Assert.Equal(3, layout.FindAll(".tab").Count));
    }

    [BunitFact]
    public void The_palette_button_opens_the_palette_and_the_toggles_flip_the_panels()
    {
        var layout = RenderLayout();

        layout.Find("button[aria-label='Ouvrir la palette de commandes']").Click();
        Assert.True(State.PaletteOpen);
        // L'état de départ dépend de la taille d'écran vue par la mise en page : on vérifie la bascule, pas la valeur.
        var explorer = State.ExplorerOpen;
        layout.Find("button[aria-label='Afficher ou masquer l\\'explorateur']").Click();
        Assert.Equal(!explorer, State.ExplorerOpen);
        var detail = State.DetailOpen;
        layout.Find("button[aria-label='Afficher ou masquer les détails']").Click();
        Assert.Equal(!detail, State.DetailOpen);
    }

    [BunitFact]
    public async Task Keyboard_shortcuts_arrive_from_the_browser_script()
    {
        var layout = RenderLayout();
        layout.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.registerShortcuts"));
        State.Open("/databases/d1");
        State.Open("/databases/d2");

        await layout.InvokeAsync(() => layout.Instance.OnShortcut("palette"));
        Assert.True(State.PaletteOpen);
        await layout.InvokeAsync(() => layout.Instance.OnShortcut("palette")); // Ctrl+K de nouveau : referme
        Assert.False(State.PaletteOpen);
        var explorer = State.ExplorerOpen;
        await layout.InvokeAsync(() => layout.Instance.OnShortcut("toggleExplorer"));
        Assert.Equal(!explorer, State.ExplorerOpen);
        await layout.InvokeAsync(() => layout.Instance.OnShortcut("closeTab"));
        Assert.DoesNotContain(State.Tabs, t => t.Href == "/databases/d2");
        await layout.InvokeAsync(() => layout.Instance.OnShortcut("inconnu")); // sans effet
    }

    [BunitFact]
    public void The_workspace_is_restored_from_the_browser_and_saved_after_every_change()
    {
        Ctx.JSInterop.Setup<string?>("localStorage.getItem", "granitdb.workspace").SetResult(
            """{"Tabs":[{"Href":"/databases/d1","Title":"Boutique","Kind":2,"Pinned":true}],"Favorites":[{"Kind":"database","Id":"d1","Name":"Boutique","Href":"/databases/d1"}],"ExplorerOpen":true,"DetailOpen":false}""");

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.Contains(State.Tabs, t => t.Href == "/databases/d1" && t.Pinned && t.Title == "Boutique"));
        Assert.Contains(State.Tabs, t => t.Href == "/"); // la page affichée a son onglet malgré la reprise
        Assert.True(State.IsFavorite("database", "d1"));

        State.Open("/search?q=zz");
        layout.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations["localStorage.setItem"],
            i => (string?)i.Arguments[0] == "granitdb.workspace" && ((string)i.Arguments[1]!).Contains("/search?q=zz")));
    }

    [BunitFact]
    public void Nothing_is_saved_before_the_stored_workspace_has_been_read()
    {
        // La mise en page n'est pas encore rendue : un changement ne doit pas écraser l'état mémorisé.
        State.Open("/avant");

        Assert.DoesNotContain(Ctx.JSInterop.Invocations, i => i.Identifier == "localStorage.setItem");
    }

    [BunitFact]
    public void A_browser_without_storage_still_gives_a_working_workspace()
    {
        Ctx.JSInterop.Setup<string?>("localStorage.getItem", "granitdb.workspace").SetException(new Microsoft.JSInterop.JSException("denied"));

        var layout = RenderLayout();

        Assert.Contains("contenu", layout.Markup);
        Nav.NavigateTo("/databases/d1");
        layout.WaitForAssertion(() => Assert.Contains(State.Tabs, t => t.Href == "/databases/d1"));
    }

    [BunitFact]
    public async Task Leaving_the_layout_detaches_the_shortcuts()
    {
        var layout = RenderLayout();
        layout.WaitForAssertion(() => Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.registerShortcuts"));

        await Ctx.DisposeComponentsAsync();

        Assert.Contains(Ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.unregisterShortcuts");
    }
}
