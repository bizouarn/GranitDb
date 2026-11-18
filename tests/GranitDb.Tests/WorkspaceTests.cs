using System.Net;
using GranitDb.Client.Workspace;
using MudBlazor;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

/// <summary>Jalon 11 : état de l'espace de travail (onglets, favoris, panneaux, persistance).</summary>
public class WorkspaceStateTests
{
    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("databases/x", "/databases/x")]
    [InlineData("/databases/x/", "/databases/x")]
    [InlineData("/databases/x?table=t1#haut", "/databases/x?table=t1")]
    [InlineData("http://localhost:41248/databases/x?table=t1", "/databases/x?table=t1")]
    [InlineData("  /search?q=a%20b  ", "/search?q=a%20b")]
    public void Addresses_are_normalised_to_a_relative_path_and_query(string input, string expected)
    {
        Assert.Equal(expected, WorkspaceState.Normalize(input));
    }

    [Theory]
    [InlineData("/", TabKind.Home)]
    [InlineData("/databases", TabKind.Home)]
    [InlineData("/search?q=abc", TabKind.Search)]
    [InlineData("/databases/d1", TabKind.Database)]
    [InlineData("/databases/d1?table=t1", TabKind.Database)]
    [InlineData("/databases/d1/diagram?domain=x", TabKind.Diagram)]
    [InlineData("/databases/d1/tables/t1?column=c1", TabKind.Table)]
    [InlineData("/ailleurs", TabKind.Other)]
    public void The_kind_of_a_page_follows_its_route(string href, TabKind kind)
    {
        Assert.Equal(kind, WorkspaceState.KindOf(href));
        Assert.False(string.IsNullOrWhiteSpace(WorkspaceState.DefaultTitle(href)));
    }

    [Fact]
    public void Opening_a_page_creates_its_tab_once_and_activates_it()
    {
        var state = new WorkspaceState();

        state.Open("/databases/d1");
        state.Open("/databases/d2/tables/t1");
        state.Open("/databases/d1");

        Assert.Equal(["/databases/d1", "/databases/d2/tables/t1"], state.Tabs.Select(t => t.Href).ToArray());
        Assert.Equal("/databases/d1", state.ActiveHref);
        Assert.Equal(TabKind.Table, state.Tabs[1].Kind);
    }

    [Fact]
    public void A_query_string_does_not_create_another_tab_but_updates_the_address()
    {
        var state = new WorkspaceState();
        state.Open("/databases/d1/diagram");
        state.Describe("/databases/d1/diagram", "Diagramme · Boutique");

        state.Open("/databases/d1/diagram?domain=d9");
        state.Open("/search?q=a");
        state.Open("/search?q=bb");

        Assert.Equal(["/databases/d1/diagram?domain=d9", "/search?q=bb"], state.Tabs.Select(t => t.Href).ToArray());
        Assert.Equal("Diagramme · Boutique", state.Tabs[0].Title); // le titre déjà donné est gardé
    }

    [Fact]
    public void Describing_gives_a_title_creates_a_missing_tab_without_activating_it_and_is_quiet_when_unchanged()
    {
        var state = new WorkspaceState();
        var changes = 0;
        state.Changed += () => changes++;
        state.Open("/databases/d1");
        var before = changes;

        state.Describe("/databases/d1", "Boutique");
        state.Describe("/databases/d1", "Boutique"); // identique : aucun changement signalé
        state.Describe("/databases/d2", "RH");

        Assert.Equal(before + 2, changes);
        Assert.Equal("Boutique", state.Tabs[0].Title);
        Assert.Equal("RH", state.Tabs[1].Title);
        Assert.Equal("/databases/d1", state.ActiveHref);
    }

    [Fact]
    public void A_new_page_starts_with_its_default_title()
    {
        var state = new WorkspaceState();

        state.Open("/databases/d1/tables/t1");

        Assert.Equal("Table", state.Tabs.Single().Title);
    }

    [Fact]
    public void Closing_the_active_tab_goes_to_its_neighbour_and_closing_the_last_one_goes_home()
    {
        var state = new WorkspaceState();
        state.Open("/a");
        state.Open("/b");
        state.Open("/c");
        state.Open("/b");

        Assert.Equal("/c", state.Close("/b"));
        Assert.Equal("/c", state.ActiveHref);
        Assert.Equal("/a", state.Close("/c")); // dernier de la liste : on retombe sur l'avant-dernier
        Assert.Equal("/", state.Close("/a")); // plus aucun onglet : l'accueil
        Assert.Empty(state.Tabs);
    }

    [Fact]
    public void Closing_an_inactive_tab_does_not_move_the_user_and_an_unknown_one_is_ignored()
    {
        var state = new WorkspaceState();
        state.Open("/a");
        state.Open("/b");

        Assert.Null(state.Close("/a"));
        Assert.Null(state.Close("/inconnu"));

        Assert.Equal(["/b"], state.Tabs.Select(t => t.Href).ToArray());
        Assert.Equal("/b", state.ActiveHref);
    }

    [Fact]
    public void A_pinned_tab_cannot_be_closed_until_it_is_unpinned()
    {
        var state = new WorkspaceState();
        state.Open("/a");
        state.TogglePin("/a");

        Assert.Null(state.Close("/a"));
        Assert.Single(state.Tabs);
        state.TogglePin("/a");
        Assert.Equal("/", state.Close("/a"));
        Assert.Empty(state.Tabs);
    }

    [Fact]
    public void Tabs_can_be_reordered_and_moved_to_the_end()
    {
        var state = new WorkspaceState();
        foreach (var href in new[] { "/a", "/b", "/c" }) state.Open(href);

        state.MoveTab("/c", "/a");
        Assert.Equal(["/c", "/a", "/b"], state.Tabs.Select(t => t.Href).ToArray());
        state.MoveTab("/c", null);
        Assert.Equal(["/a", "/b", "/c"], state.Tabs.Select(t => t.Href).ToArray());
        state.MoveTab("/inconnu", "/a");
        Assert.Equal(3, state.Tabs.Count);
    }

    [Fact]
    public void Too_many_tabs_drop_the_oldest_unpinned_one_but_never_the_pinned_or_the_active_one()
    {
        var state = new WorkspaceState();
        state.Open("/p0");
        state.TogglePin("/p0");
        for (var i = 1; i <= WorkspaceState.MaxTabs + 3; i++) state.Open($"/p{i}");

        Assert.Equal(WorkspaceState.MaxTabs, state.Tabs.Count);
        Assert.Contains(state.Tabs, t => t.Href == "/p0"); // épinglé : conservé
        Assert.DoesNotContain(state.Tabs, t => t.Href == "/p1"); // le plus ancien non épinglé est parti
        Assert.Contains(state.Tabs, t => t.Href == $"/p{WorkspaceState.MaxTabs + 3}"); // l'onglet affiché reste
    }

    [Fact]
    public void Favorites_are_toggled_listed_once_and_reordered()
    {
        var state = new WorkspaceState();
        var a = new FavoriteItem("table", "t1", "customers", "/databases/d/tables/t1");
        var b = new FavoriteItem("database", "d1", "Boutique", "/databases/d1");
        var c = new FavoriteItem("domain", "g1", "Ventes", "/databases/d1/diagram?domain=g1");

        Assert.True(state.ToggleFavorite(a));
        state.ToggleFavorite(b);
        state.ToggleFavorite(c);
        Assert.True(state.IsFavorite("table", "t1"));
        Assert.False(state.IsFavorite("table", "d1")); // même identifiant, autre type
        state.MoveFavorite("domain", "g1", "t1");
        Assert.Equal(["g1", "t1", "d1"], state.Favorites.Select(f => f.Id).ToArray());
        state.MoveFavorite("domain", "g1", null);
        Assert.Equal(["t1", "d1", "g1"], state.Favorites.Select(f => f.Id).ToArray());
        state.MoveFavorite("domain", "g1", "g1"); // sur lui-même : sans effet
        Assert.False(state.ToggleFavorite(a));
        Assert.Equal(["d1", "g1"], state.Favorites.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Selecting_an_item_keeps_the_panels_as_they_are_and_the_panels_toggle()
    {
        var state = new WorkspaceState();
        state.SetDetailOpen(false);

        state.Select(new SelectionItem("table", "t1", "d1", "t1"));

        Assert.False(state.DetailOpen); // un tiroir fermé n'est pas rouvert à chaque sélection
        Assert.Equal("t1", state.Selection!.Id);
        state.Select(null);
        Assert.Null(state.Selection);
        state.SetExplorerOpen(false);
        state.SetPalette(true);
        Assert.False(state.ExplorerOpen);
        Assert.True(state.PaletteOpen);
    }

    [Fact]
    public void An_automatic_layout_change_is_not_remembered_as_the_users_choice()
    {
        var state = new WorkspaceState();
        state.SetExplorerOpen(false, remember: false); // petit écran : le tiroir se referme tout seul
        state.SetDetailOpen(false, remember: false);

        var restored = new WorkspaceState();
        restored.TryRestore(state.ToJson());

        Assert.False(state.ExplorerOpen);
        Assert.True(restored.ExplorerOpen); // sur un grand écran, il reste ouvert
        Assert.True(restored.DetailOpen);
        state.SetExplorerOpen(false); // un choix explicite, lui, est mémorisé
        restored.TryRestore(state.ToJson());
        Assert.False(restored.ExplorerOpen);
    }

    [Fact]
    public void The_workspace_survives_a_save_and_a_restore()
    {
        var source = new WorkspaceState();
        source.Open("/databases/d1");
        source.Describe("/databases/d1", "Boutique");
        source.Open("/databases/d1/tables/t1");
        source.TogglePin("/databases/d1");
        source.ToggleFavorite(new FavoriteItem("table", "t1", "customers", "/databases/d1/tables/t1"));
        source.SetExplorerOpen(false);
        source.SetDetailOpen(false);

        var restored = new WorkspaceState();
        Assert.True(restored.TryRestore(source.ToJson()));

        Assert.Equal(source.Tabs, restored.Tabs);
        Assert.True(restored.Tabs[0].Pinned);
        Assert.Equal("Boutique", restored.Tabs[0].Title);
        Assert.Equal(source.Favorites, restored.Favorites);
        Assert.False(restored.ExplorerOpen);
        Assert.False(restored.DetailOpen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("null")]
    public void An_unreadable_saved_state_is_ignored_and_changes_nothing(string? json)
    {
        var state = new WorkspaceState();
        state.Open("/a");

        Assert.False(state.TryRestore(json));

        Assert.Equal(["/a"], state.Tabs.Select(t => t.Href).ToArray());
    }

    [Fact]
    public void A_restored_state_is_cleaned_duplicates_and_empty_entries_are_dropped_and_addresses_normalised()
    {
        var state = new WorkspaceState();

        state.TryRestore("""
            {"Tabs":[{"Href":"databases/d1/","Title":"A","Kind":0,"Pinned":false},{"Href":"/databases/d1?x=1","Title":"B","Kind":0,"Pinned":false},{"Href":"","Title":"vide","Kind":0,"Pinned":false}],
             "Favorites":[{"Kind":"table","Id":"t1","Name":"n","Href":"/h"},{"Kind":"table","Id":"t1","Name":"n","Href":"/h"},{"Kind":"table","Id":"","Name":"sans id","Href":"/h"}]}
            """);

        Assert.Equal(["/databases/d1"], state.Tabs.Select(t => t.Href).ToArray());
        Assert.Equal(TabKind.Database, state.Tabs[0].Kind);
        Assert.Single(state.Favorites);
        Assert.True(state.ExplorerOpen); // valeurs par défaut quand elles manquent
    }

    [Fact]
    public void Catalog_changes_are_broadcast_with_their_database_and_source()
    {
        var state = new WorkspaceState();
        var seen = new List<(string?, object?)>();
        state.CatalogChanged += (db, source) => seen.Add((db, source));
        var me = new object();

        state.NotifyCatalogChanged("d1", me);
        state.NotifyCatalogChanged();

        Assert.Equal([("d1", me), (null, null)], seen);
    }
}

/// <summary>Notifications brèves avec annulation.</summary>
public class NotifierTests
{
    [Fact]
    public void A_message_is_shown_then_dismissed()
    {
        var notifier = new Notifier();
        var changes = 0;
        notifier.Changed += () => changes++;

        var toast = notifier.Show("Fait.");
        notifier.Dismiss(toast.Id);
        notifier.Dismiss(toast.Id); // déjà parti : sans effet

        Assert.Empty(notifier.Toasts);
        Assert.Equal(2, changes);
        Assert.Equal(Severity.Success, toast.Severity);
    }

    [Fact]
    public void Only_the_latest_messages_are_kept()
    {
        var notifier = new Notifier();

        for (var i = 1; i <= 6; i++) notifier.Show($"m{i}");

        Assert.Equal(["m3", "m4", "m5", "m6"], notifier.Toasts.Select(t => t.Message).ToArray());
    }

    [Fact]
    public void A_message_disappears_by_itself_after_its_duration()
    {
        var notifier = new Notifier { Duration = TimeSpan.FromMilliseconds(30) };

        notifier.Show("éphémère");

        SpinWait.SpinUntil(() => notifier.Toasts.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Empty(notifier.Toasts);
    }

    [Fact]
    public async Task Undo_runs_the_action_removes_the_message_and_confirms()
    {
        var notifier = new Notifier();
        var undone = 0;
        var toast = notifier.Show("Supprimé.", Severity.Success, () =>
        {
            undone++;
            return Task.CompletedTask;
        });

        await notifier.UndoAsync(toast.Id);

        Assert.Equal(1, undone);
        Assert.Equal(["Action annulée."], notifier.Toasts.Select(t => t.Message).ToArray());
        await notifier.UndoAsync(toast.Id); // déjà annulé : sans effet
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task A_failing_undo_is_reported_as_an_error()
    {
        var notifier = new Notifier();
        var toast = notifier.Show("Supprimé.", Severity.Success, () => throw new ApiException("Conflit"));

        await notifier.UndoAsync(toast.Id);

        var error = Assert.Single(notifier.Toasts);
        Assert.Equal(Severity.Error, error.Severity);
        Assert.Contains("Conflit", error.Message);
    }

    [Fact]
    public async Task A_message_without_undo_ignores_the_undo_request()
    {
        var notifier = new Notifier();
        var toast = notifier.Show("Simple.");

        await notifier.UndoAsync(toast.Id);

        Assert.Single(notifier.Toasts);
    }
}

/// <summary>Cache du catalogue : une lecture par base jusqu'à une modification.</summary>
public class CatalogCacheTests
{
    private const string Tables = """[{"id":"t2","name":"orders","tableType":"Table","columns":[],"relations":[],"indexes":[]},{"id":"t1","name":"Customers","tableType":"Table","columns":[],"relations":[],"indexes":[]}]""";

    private static (CatalogCache Cache, WorkspaceState State, FakeApi Api) Create()
    {
        var api = new FakeApi
        {
            Respond = r => r.RequestUri!.AbsolutePath.Contains("/DomainInfo/")
                ? FakeApi.Json("""[{"id":"g1","databaseId":"d1","name":"Ventes","tables":[]}]""")
                : FakeApi.Json(Tables)
        };
        var state = new WorkspaceState();
        return (new CatalogCache(api.Client, state), state, api);
    }

    [Fact]
    public async Task Tables_are_read_once_sorted_by_name_ignoring_case()
    {
        var (cache, _, api) = Create();

        var first = await cache.GetTablesAsync("d1");
        var second = await cache.GetTablesAsync("d1");
        await cache.GetTablesAsync("d2");

        Assert.Equal(["Customers", "orders"], first.Select(t => t.Name).ToArray());
        Assert.Same(first, second);
        Assert.Equal(2, api.Requests.Count); // une lecture par base
    }

    [Fact]
    public async Task Domains_are_read_once_per_database()
    {
        var (cache, _, api) = Create();

        await cache.GetDomainsAsync("d1");
        var domains = await cache.GetDomainsAsync("d1");

        Assert.Equal("Ventes", Assert.Single(domains).Name);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_catalog_change_makes_the_next_read_fresh_for_that_database_only()
    {
        var (cache, state, api) = Create();
        await cache.GetTablesAsync("d1");
        await cache.GetTablesAsync("d2");

        state.NotifyCatalogChanged("d1");
        await cache.GetTablesAsync("d1");
        await cache.GetTablesAsync("d2");

        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task A_global_change_empties_everything_and_a_disposed_cache_stops_listening()
    {
        var (cache, state, api) = Create();
        await cache.GetTablesAsync("d1");
        await cache.GetDomainsAsync("d1");

        state.NotifyCatalogChanged();
        await cache.GetTablesAsync("d1");
        await cache.GetDomainsAsync("d1");
        Assert.Equal(4, api.Requests.Count);

        cache.Dispose();
        state.NotifyCatalogChanged();
        await cache.GetTablesAsync("d1");
        Assert.Equal(4, api.Requests.Count); // plus d'abonnement : la lecture précédente reste valable
    }

    [Fact]
    public async Task An_unreachable_database_gives_no_tables_rather_than_failing()
    {
        var api = new FakeApi { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var cache = new CatalogCache(api.Client, new WorkspaceState());

        Assert.Empty(await cache.GetTablesAsync("nope"));
    }
}
