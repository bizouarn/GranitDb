using Bunit;
using GranitDb.Client.Layout;
using GranitDb.Client.Models;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lot 2 : recherche mieux guidée (suggestions pendant la frappe, exemples, limitation à une base, raison du résultat).</summary>
public class ClientGuidedSearchTests : ShellTestBase
{
    private const string Hits = """
        [{"kind":"table","id":"t1","name":"customers","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers"},
         {"kind":"column","id":"c9","name":"mail","description":"Adresse email du customer","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers"},
         {"kind":"table","id":"t7","name":"customer_notes","databaseId":"d2","databaseName":"RH","tableId":"t7","tableName":"customer_notes"}]
        """;

    public ClientGuidedSearchTests() : base(popoverProvider: false)
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Search" ? FakeApi.Json(Hits) : null;
    }

    private IRenderedComponent<MainLayout> RenderLayout() =>
        Ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "contenu"))));

    [BunitFact]
    public void Typing_shows_grouped_suggestions_with_where_and_why()
    {
        var layout = RenderLayout();

        layout.Find("input[aria-label='Recherche globale']").Input("custom");

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".search-suggest-item")), TimeSpan.FromSeconds(3));
        var items = layout.FindAll(".search-suggest-item");
        Assert.Equal(3, items.Count);
        Assert.Contains("Table", items[0].TextContent);
        Assert.Contains("customers.mail", layout.Markup);
        Assert.Contains("dans la description", layout.Markup);
        Assert.Contains("Boutique", items[0].TextContent);
    }

    [BunitFact]
    public void Arrows_and_Enter_open_the_chosen_suggestion_and_Escape_closes_the_list()
    {
        var layout = RenderLayout();
        var input = layout.Find("input[aria-label='Recherche globale']");
        input.Input("custom");
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".search-suggest-item")), TimeSpan.FromSeconds(3));

        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        layout.WaitForAssertion(() => Assert.Equal("true", layout.FindAll(".search-suggest-item")[0].GetAttribute("aria-selected")));
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        layout.WaitForAssertion(() => Assert.EndsWith("/databases/d1/tables/t1", Nav.Uri));
        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll(".search-suggest-item")));

        input.Input("customer");
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".search-suggest-item")), TimeSpan.FromSeconds(3));
        input.KeyDown(new KeyboardEventArgs { Key = "Escape" });
        layout.WaitForAssertion(() => Assert.Empty(layout.FindAll(".search-suggest-item")));
    }

    [BunitFact]
    public void The_see_all_row_goes_to_the_results_page()
    {
        var layout = RenderLayout();
        layout.Find("input[aria-label='Recherche globale']").Input("custom");
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".search-suggest-all")), TimeSpan.FromSeconds(3));

        layout.Find(".search-suggest-all").Click();

        layout.WaitForAssertion(() => Assert.EndsWith("/search?q=custom", Nav.Uri));
    }

    [BunitFact]
    public void The_empty_results_page_suggests_examples_to_try()
    {
        var page = Ctx.Render<Search>();

        Assert.NotEmpty(page.FindAll("a.search-example"));
        Assert.Contains("/search?q=client", page.Find("a.search-example").GetAttribute("href"));
    }

    [BunitFact]
    public void Results_can_be_limited_to_one_database_and_say_why_they_match()
    {
        Nav.NavigateTo("/search?q=custom");
        var page = Ctx.Render<Search>();
        page.WaitForAssertion(() => Assert.Contains("customer_notes", page.Markup));

        Assert.Equal(3, page.FindAll(".search-scope-chip").Count); // toutes, Boutique, RH
        Assert.Contains("dans le nom", page.Markup);
        Assert.Contains("dans la description", page.Markup);

        page.FindAll(".search-scope-chip").First(c => c.TextContent.StartsWith("RH")).Click();

        Assert.DoesNotContain("customers.mail", page.Markup);
        Assert.Contains("customer_notes", page.Markup);
        Assert.Contains("1 résultat(s)", page.Markup);
    }
}
