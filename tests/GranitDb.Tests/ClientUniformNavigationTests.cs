using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lot 2 : navigation uniforme (même fil d'Ariane partout, liens croisés entre relations, impact et tables).</summary>
public class ClientUniformNavigationTests : ShellTestBase
{
    /// <summary>Les liens réels (le dernier maillon, la page courante, est un lien désactivé « # »).</summary>
    private static string[] Links(IEnumerable<AngleSharp.Dom.IElement> anchors) =>
        anchors.Select(a => a.GetAttribute("href")!).Where(h => !h.EndsWith("#")).ToArray();

    private static string[] Steps(IRenderedComponent<PageCrumbs> crumbs) =>
        crumbs.FindAll(".mud-breadcrumb-item").Select(i => i.TextContent.Trim()).Where(t => t.Length > 0).ToArray();

    [BunitFact]
    public void Crumbs_go_from_the_databases_to_the_current_page_and_only_the_last_step_has_no_link()
    {
        var crumbs = Ctx.Render<PageCrumbs>(p => p
            .Add(x => x.DatabaseId, "d1").Add(x => x.DatabaseName, "Boutique")
            .Add(x => x.TableId, "t1").Add(x => x.TableName, "customers")
            .Add(x => x.ColumnName, "email"));

        Assert.Equal(["Bases", "Boutique", "customers", "email"], Steps(crumbs));
        Assert.Equal(["/", "/databases/d1", "/databases/d1/tables/t1"], Links(crumbs.FindAll("a")));
    }

    [BunitFact]
    public void A_section_page_has_the_same_trail_as_the_others()
    {
        var crumbs = Ctx.Render<PageCrumbs>(p => p.Add(x => x.Current, "Webhooks"));

        Assert.Equal(["Bases", "Webhooks"], Steps(crumbs));
        Assert.Equal(["/"], Links(crumbs.FindAll("a")));
    }

    [BunitFact]
    public void The_database_page_shows_its_database_as_the_current_step()
    {
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));

        var crumbs = page.Find(".page-crumbs");
        Assert.Contains("Boutique", crumbs.TextContent);
        Assert.Equal(["/"], Links(page.FindAll(".page-crumbs a")));
    }

    [BunitFact]
    public void A_table_tab_opened_from_a_column_ends_the_trail_with_that_column()
    {
        Nav.NavigateTo("/databases/d1/tables/t1?column=c2");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1").Add(x => x.TableRoute, "t1"));

        page.WaitForAssertion(() => Assert.Contains("email", page.Find(".page-crumbs").TextContent));
        Assert.Equal(["/", "/databases/d1", "/databases/d1/tables/t1"], Links(page.FindAll(".page-crumbs a")));
    }

    [BunitFact]
    public void A_relation_opens_the_tables_on_both_of_its_sides()
    {
        Nav.NavigateTo("/databases/d1/tables/t1");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1").Add(x => x.TableRoute, "t1"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("a.relation-link")));

        var links = page.FindAll("[data-relation] a.relation-link");

        Assert.Equal(["/databases/d1/tables/t1?column=c1", "/databases/d1/tables/t2?column=c3"], links.Select(a => a.GetAttribute("href")!).ToArray());
        Assert.Contains("customers.id → orders.customer_id", page.Find("[data-relation]").TextContent);
    }
}
