using GranitDb.Client.Workspace;
using System.Net;
using Bunit;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace GranitDb.Tests;

/// <summary>Navigation dans le client : lien profond vers une table, fil d'Ariane, pagination de la liste.</summary>
public class ClientNavigationTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    public ClientNavigationTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        _ctx.Render<MudBlazor.MudPopoverProvider>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private const string Tables = """
        [{"id":"t1","name":"customers","tableType":"Table","columns":[{"id":"c1","name":"id","dataType":"INTEGER","position":0}],"indexes":[],"relations":[]},
         {"id":"t2","name":"orders","tableType":"Table","columns":[{"id":"c2","name":"total","dataType":"REAL","position":0}],"indexes":[],"relations":[]}]
        """;

    private void ServeDatabase()
    {
        _api.Respond = r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/TableInfo/database/")
            ? FakeApi.Json(Tables)
            : FakeApi.Json("""{"id":"db","name":"Boutique","type":"Sqlite"}""");
    }

    private IRenderedComponent<DatabaseDetail> OpenDetail(string? tableId)
    {
        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(tableId == null ? "/databases/db" : $"/databases/db?table={tableId}");
        return _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));
    }

    [BunitFact]
    public void Detail_shows_a_breadcrumb_with_the_database_name()
    {
        ServeDatabase();

        var page = OpenDetail(null);

        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        Assert.NotEmpty(page.FindAll("a[href='/']"));
        Assert.Contains("Boutique", page.Find("nav, .mud-breadcrumbs").TextContent);
    }

    [BunitFact]
    public void Without_a_table_parameter_every_panel_starts_collapsed()
    {
        ServeDatabase();

        var page = OpenDetail(null);

        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        Assert.Empty(page.FindAll(".mud-expand-panel.mud-panel-expanded"));
    }

    [BunitFact]
    public void The_table_from_a_search_result_is_expanded_and_scrolled_into_view()
    {
        ServeDatabase();

        var page = OpenDetail("t2");

        page.WaitForAssertion(() => Assert.Contains("total", page.Markup));
        var expanded = page.FindAll(".mud-expand-panel.mud-panel-expanded");
        Assert.Single(expanded);
        Assert.Contains("orders", expanded[0].TextContent);
        Assert.NotNull(page.Find("#t-t2"));
        page.WaitForAssertion(() =>
            Assert.Contains(_ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.scrollToId" && (string?)i.Arguments[0] == "t-t2"));
    }

    [BunitFact]
    public void An_unknown_table_parameter_is_ignored()
    {
        ServeDatabase();

        var page = OpenDetail("nope");

        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        Assert.Empty(page.FindAll(".mud-expand-panel.mud-panel-expanded"));
        Assert.DoesNotContain(_ctx.JSInterop.Invocations, i => i.Identifier == "granitdb.scrollToId");
    }

    private static string Databases(int count)
    {
        return "[" + string.Join(",", Enumerable.Range(0, count)
            .Select(i => $$"""{"id":"d{{i}}","name":"base_{{i:00}}","type":"Sqlite"}""")) + "]";
    }

    [BunitFact]
    public void The_list_is_paged_and_changing_page_requests_the_next_slice()
    {
        _api.Respond = r =>
        {
            var skip = int.Parse(System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["skip"] ?? "0");
            var response = FakeApi.Json(skip == 0 ? Databases(12) : "[{\"id\":\"last\",\"name\":\"derniere\",\"type\":\"Sqlite\"}]");
            response.Headers.Add("X-Total-Count", "13");
            return response;
        };

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("base_00", page.Markup));
        Assert.Equal(12, page.FindAll(".mud-card").Count);
        Assert.NotEmpty(page.FindAll(".mud-pagination"));

        page.FindAll(".mud-pagination button").Single(b => b.TextContent.Trim() == "2").Click();

        page.WaitForAssertion(() => Assert.Contains("derniere", page.Markup));
        Assert.DoesNotContain("base_00", page.Markup);
        Assert.Contains(_api.Requests, r => r.RequestUri!.Query == "?skip=12&take=12");
    }

    [BunitFact]
    public void A_single_page_has_no_pagination()
    {
        _api.Respond = _ =>
        {
            var response = FakeApi.Json(Databases(3));
            response.Headers.Add("X-Total-Count", "3");
            return response;
        };

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("base_02", page.Markup));
        Assert.Empty(page.FindAll(".mud-pagination"));
    }

    [BunitFact]
    public void A_failing_api_on_the_list_is_reported()
    {
        _api.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var page = _ctx.Render<Databases>();

        page.WaitForAssertion(() => Assert.Contains("L'API est injoignable", page.Markup));
    }
}
