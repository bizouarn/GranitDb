using GranitDb.Client.Workspace;
using System.Net;
using Bunit;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

public class ApiClientSearchAndPagingTests
{
    [Fact]
    public async Task SearchAsync_escapes_the_term_and_builds_navigation_links()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""
                [{"kind":"database","id":"d1","name":"Boutique","databaseId":"d1","databaseName":"Boutique"},
                 {"kind":"column","id":"c1","name":"email","databaseId":"d 1","databaseName":"Boutique","tableId":"t/1","tableName":"customers","description":"Adresse"}]
                """)
        };

        var hits = await api.Client.SearchAsync("a b&c");

        Assert.Equal("http://api.test/api/v1/Search?q=a%20b%26c", api.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.Equal("/databases/d1", hits[0].Href);
        Assert.Equal("/databases/d%201/tables/t%2F1?column=c1", hits[1].Href);
        Assert.Equal("Adresse", hits[1].Description);
    }

    [Fact]
    public async Task SearchAsync_turns_http_errors_into_api_exceptions()
    {
        var api = new FakeApi { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest) };

        await Assert.ThrowsAsync<ApiException>(() => api.Client.SearchAsync("ab"));
    }

    [Fact]
    public async Task GetDatabasesPageAsync_reads_the_total_from_the_header()
    {
        var api = new FakeApi
        {
            Respond = _ =>
            {
                var response = FakeApi.Json("""[{"id":"1","name":"A","type":"Sqlite"}]""");
                response.Headers.Add("X-Total-Count", "37");
                return response;
            }
        };

        var page = await api.Client.GetDatabasesPageAsync(12, 12);

        Assert.Equal("http://api.test/api/v1/DatabaseInfo?skip=12&take=12", api.Requests.Single().RequestUri!.ToString());
        Assert.Single(page.Items);
        Assert.Equal(37, page.Total);
    }

    [Fact]
    public async Task GetDatabasesPageAsync_without_the_header_falls_back_to_what_it_has_seen()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""[{"id":"1","name":"A","type":"Sqlite"},{"id":"2","name":"B","type":"Sqlite"}]""")
        };

        var page = await api.Client.GetDatabasesPageAsync(10, 12);

        Assert.Equal(12, page.Total);
    }

    [Fact]
    public async Task GetDatabasesPageAsync_throws_when_the_api_fails()
    {
        var api = new FakeApi { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };

        await Assert.ThrowsAsync<HttpRequestException>(() => api.Client.GetDatabasesPageAsync(0, 12));
    }
}

public class SearchPageTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    public SearchPageTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        _ctx.Render<MudBlazor.MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private IRenderedComponent<Search> Open(string? query)
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(query == null ? "/search" : $"/search?q={Uri.EscapeDataString(query)}");
        return _ctx.Render<Search>();
    }

    [BunitFact]
    public void Without_a_term_it_explains_how_to_search_and_calls_nothing()
    {
        var page = Open(null);

        Assert.Contains("au moins 2 caractères", page.Markup);
        Assert.Empty(_api.Requests);
    }

    [BunitFact]
    public void A_single_character_is_not_searched()
    {
        var page = Open("a");

        Assert.Contains("au moins 2 caractères", page.Markup);
        Assert.Empty(_api.Requests);
    }

    [BunitFact]
    public void Results_are_grouped_by_kind_with_context_and_links()
    {
        _api.Respond = _ => FakeApi.Json("""
            [{"kind":"database","id":"d1","name":"Boutique","databaseId":"d1","databaseName":"Boutique"},
             {"kind":"table","id":"t1","name":"customers","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers"},
             {"kind":"column","id":"c1","name":"customer_id","description":"Lien client","databaseId":"d1","databaseName":"Boutique","tableId":"t2","tableName":"orders"},
             {"kind":"index","id":"i1","name":"ix_customers","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers"}]
            """);

        var page = Open("custom");

        page.WaitForAssertion(() => Assert.Contains("4 résultat(s)", page.Markup));
        Assert.Contains("Bases (1)", page.Markup);
        Assert.Contains("Tables (1)", page.Markup);
        Assert.Contains("Colonnes (1)", page.Markup);
        Assert.Contains("Index (1)", page.Markup);
        Assert.Contains("Colonne de Boutique › orders", page.Markup);
        Assert.Contains("Lien client", page.Markup);
        var links = page.FindAll("a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.Contains("/databases/d1", links);
        Assert.Contains("/databases/d1/tables/t2?column=c1", links); // la colonne s'ouvre dans l'onglet de sa table
        Assert.Contains("/databases/d1/tables/t1", links);
        Assert.Equal("http://api.test/api/v1/Search?q=custom", _api.Requests.Single().RequestUri!.ToString());
    }

    [BunitFact]
    public void No_hit_says_so()
    {
        _api.Respond = _ => FakeApi.Json("[]");

        var page = Open("zzz");

        page.WaitForAssertion(() => Assert.Contains("Aucun résultat pour « zzz »", page.Markup));
    }

    [BunitFact]
    public void An_unreachable_api_is_reported()
    {
        _api.Respond = _ => throw new HttpRequestException("refused");

        var page = Open("custom");

        page.WaitForAssertion(() => Assert.Contains("L'API est injoignable", page.Markup));
    }

    [BunitFact]
    public void The_page_shows_a_breadcrumb_back_to_the_databases()
    {
        var page = Open(null);

        Assert.NotEmpty(page.FindAll("a[href='/']"));
        Assert.Contains("Recherche", page.Markup);
    }
}

public class MainLayoutTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    public MainLayoutTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
    }

    private NavigationManager Nav => _ctx.Services.GetRequiredService<NavigationManager>();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private IRenderedComponent<GranitDb.Client.Layout.MainLayout> RenderLayout()
    {
        return _ctx.Render<GranitDb.Client.Layout.MainLayout>(p =>
            p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "contenu"))));
    }

    [BunitFact]
    public void Enter_in_the_search_box_goes_to_the_results_page()
    {
        var layout = RenderLayout();
        var input = layout.Find("input[aria-label='Recherche globale']");

        input.Input("clients & co");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.EndsWith("/search?q=clients%20%26%20co", Nav.Uri);
    }

    [BunitFact]
    public void Other_keys_or_a_too_short_term_do_not_navigate()
    {
        var layout = RenderLayout();
        var before = Nav.Uri;
        var input = layout.Find("input[aria-label='Recherche globale']");

        input.Input("a");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        input.Input("abc");
        input.KeyDown(new KeyboardEventArgs { Key = "a" });

        Assert.Equal(before, Nav.Uri);
    }

    [BunitFact]
    public void The_stored_dark_theme_is_applied_and_toggling_persists_the_choice()
    {
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "granitdb.theme").SetResult("dark");
        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll("button[aria-label='Passer au thème clair']")));
        layout.Find("button[aria-label='Passer au thème clair']").Click();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll("button[aria-label='Passer au thème sombre']")));
        var write = _ctx.JSInterop.Invocations["localStorage.setItem"].Single(i => (string?)i.Arguments[0] == "granitdb.theme");
        Assert.Equal(new object?[] { "granitdb.theme", "light" }, write.Arguments.ToArray());
    }

    [BunitFact]
    public void Without_a_stored_choice_the_light_theme_is_the_default()
    {
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "granitdb.theme").SetResult(null);

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll("button[aria-label='Passer au thème sombre']")));
    }

    [BunitFact]
    public void A_storage_failure_does_not_break_the_layout()
    {
        _ctx.JSInterop.Setup<string?>("localStorage.getItem", "granitdb.theme")
            .SetException(new Microsoft.JSInterop.JSException("denied"));

        var layout = RenderLayout();

        Assert.Contains("contenu", layout.Markup);
        layout.Find("button[aria-label='Passer au thème sombre']").Click();
        Assert.NotEmpty(layout.FindAll("button[aria-label='Passer au thème clair']"));
    }
}
