using Bunit;
using GranitDb.Client.Layout;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lot 1 : le bandeau « Filtre actif » sur les listes filtrées (explorateur, tables d'une base, journal d'audit).</summary>
public class ClientActiveFilterTests : ShellTestBase
{
    [BunitFact]
    public void The_explorer_says_it_is_filtered_and_clears_the_filter()
    {
        var explorer = Ctx.Render<Explorer>();
        explorer.WaitForAssertion(() => Assert.Contains("Boutique", explorer.Markup));
        Assert.Empty(explorer.FindAll(".filter-bar"));

        explorer.Find("input[aria-label=\"Filtrer l'explorateur\"]").Input("cust");

        explorer.WaitForAssertion(() => Assert.Contains("nom contient « cust »", explorer.Find(".filter-bar").TextContent));
        Assert.Contains("table(s) affichée(s)", explorer.Find(".filter-bar").TextContent);

        explorer.Find("button.filter-bar-clear").Click();

        explorer.WaitForAssertion(() => Assert.Empty(explorer.FindAll(".filter-bar")));
    }

    [BunitFact]
    public void A_database_page_shows_the_text_and_domain_filters_and_clears_them_together()
    {
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        Assert.Empty(page.FindAll(".filter-bar"));

        page.Find(".mud-input-slot[type=text], input[type=text]").Input("cust");
        page.Find("select").Change(page.Find("select option:nth-child(2)").GetAttribute("value")!);

        page.WaitForAssertion(() => Assert.Contains("nom contient « cust »", page.Find(".filter-bar").TextContent));
        Assert.Contains("domaine Ventes", page.Find(".filter-bar").TextContent);
        Assert.Contains("table(s) sur 3", page.Find(".filter-bar").TextContent);

        page.Find("button.filter-bar-clear").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".filter-bar")));
    }

    private static HttpResponseMessage Empty()
    {
        var response = FakeApi.Json("[]");
        response.Headers.Add("X-Total-Count", "0");
        return response;
    }

    [BunitFact]
    public void The_audit_banner_shows_only_the_applied_filter()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Audit" ? Empty() : null;
        var page = Ctx.Render<Audit>();
        page.WaitForAssertion(() => Assert.Contains("Journal d'audit", page.Markup));
        Assert.Empty(page.FindAll(".filter-bar"));

        page.Find("input.audit-actor").Input("alice"); // saisi, pas encore appliqué
        Assert.Empty(page.FindAll(".filter-bar"));
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("auteur « alice »", page.Find(".filter-bar").TextContent));
        page.Find("button.filter-bar-clear").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".filter-bar")));
    }
}
