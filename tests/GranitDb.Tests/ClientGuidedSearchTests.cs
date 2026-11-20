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

    [BunitFact]
    public void Searching_from_a_database_page_limits_the_results_to_that_database_until_widened()
    {
        Nav.NavigateTo("/databases/d1/tables/t1");
        var layout = RenderLayout();
        layout.Find("input[aria-label='Recherche globale']").Input("custom");
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".search-suggest-all")), TimeSpan.FromSeconds(3));

        layout.Find(".search-suggest-all").Click();
        layout.WaitForAssertion(() => Assert.EndsWith("/search?q=custom&db=d1", Nav.Uri));

        var page = Ctx.Render<Search>();
        page.WaitForAssertion(() => Assert.Contains("Adresse email du customer", page.Markup));
        Assert.DoesNotContain("customer_notes", page.Markup);
        Assert.Contains("2 résultat(s)", page.Markup);

        page.FindAll(".search-scope-chip").First(c => c.TextContent.StartsWith("Toutes")).Click();

        Assert.Contains("customer_notes", page.Markup);
    }

    [BunitFact]
    public void A_scoped_search_without_result_offers_to_search_every_database()
    {
        Nav.NavigateTo("/search?q=custom&db=d3");
        var page = Ctx.Render<Search>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".search-scope-empty")));

        page.Find(".search-widen").Click();

        Assert.Contains("customer_notes", page.Markup);
        Assert.Empty(page.FindAll(".search-scope-empty"));
    }
}

/// <summary>Domaines et étiquettes dans la recherche : un domaine est un résultat, une étiquette ou un domaine explique un élément trouvé.</summary>
public class ClientSearchDomainsAndTagsTests : ShellTestBase
{
    private const string Hits = """
        [{"kind":"domain","id":"g1","name":"Ventes","databaseId":"d1","databaseName":"Boutique"},
         {"kind":"table","id":"t1","name":"customers","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers","via":"domaine « Ventes »"},
         {"kind":"column","id":"c9","name":"email","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers","via":"étiquette « RGPD »"}]
        """;

    public ClientSearchDomainsAndTagsTests() : base(popoverProvider: false)
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Search" ? FakeApi.Json(Hits) : null;
    }

    [BunitFact]
    public void A_domain_is_a_result_that_opens_the_diagram_on_it_and_the_reason_names_the_tag_or_domain()
    {
        Nav.NavigateTo("/search?q=ventes");
        var page = Ctx.Render<Search>();
        page.WaitForAssertion(() => Assert.Contains("Domaines (1)", page.Markup));

        Assert.Contains("/databases/d1/diagram?domain=g1", page.Find("a[href*='diagram']").GetAttribute("href"));
        Assert.Contains("domaine « Ventes »", page.Markup);
        Assert.Contains("étiquette « RGPD »", page.Markup);
    }

    [BunitFact]
    public void The_kinds_are_labelled_and_a_domain_comes_after_the_tables_and_columns()
    {
        Assert.Equal("Domaine", SearchHit.KindLabel("domain"));
        Assert.True(SearchHit.KindOrder("domain") > SearchHit.KindOrder("database"));
    }
}
