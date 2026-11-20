using Bunit;
using GranitDb.Client.Layout;

namespace GranitDb.Tests;

/// <summary>Clic droit sur un onglet : fermer à droite, les autres, ou tous (les onglets épinglés restent).</summary>
public class ClientTabMenuTests : ShellTestBase
{
    // Onglets : Boutique | customers | Recherche | Webhooks ; l'onglet affiché est « Webhooks ».
    private IRenderedComponent<TabStrip> Render()
    {
        State.Open("/databases/d1");
        State.Describe("/databases/d1", "Boutique");
        State.Open("/databases/d1/tables/t1");
        State.Describe("/databases/d1/tables/t1", "customers");
        State.Open("/search?q=ab");
        State.Open("/webhooks");
        State.Describe("/webhooks", "Webhooks");
        return Ctx.Render<TabStrip>();
    }

    private static string[] Titles(IRenderedComponent<TabStrip> strip) =>
        strip.FindAll(".tab").Select(t => t.GetAttribute("data-tab")!).ToArray();

    private static void RightClick(IRenderedComponent<TabStrip> strip, string title) =>
        strip.Find($"[data-tab='{title}']").ContextMenu();

    [BunitFact]
    public void Right_click_opens_a_menu_and_Escape_closes_it()
    {
        var strip = Render();
        Assert.Empty(strip.FindAll(".tab-menu"));

        RightClick(strip, "customers");

        Assert.Equal("menu", strip.Find(".tab-menu").GetAttribute("role"));
        Assert.Equal(3, strip.FindAll("button[role=menuitem]").Count);
        strip.Find(".tab-menu").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(strip.FindAll(".tab-menu"));
    }

    [BunitFact]
    public void Close_to_the_right_keeps_that_tab_and_those_on_its_left_and_shows_how_many_it_closes()
    {
        var strip = Render();
        RightClick(strip, "customers");
        Assert.Contains("(2)", strip.Find(".tab-menu-right").TextContent);

        strip.Find(".tab-menu-right").Click();

        Assert.Equal(["Boutique", "customers"], Titles(strip));
        Assert.Empty(strip.FindAll(".tab-menu"));
        Assert.EndsWith("/databases/d1/tables/t1", Nav.Uri); // l'onglet affiché a été fermé : on retombe sur celui d'où part le menu
    }

    [BunitFact]
    public void Close_to_the_right_is_disabled_on_the_last_tab()
    {
        var strip = Render();

        RightClick(strip, "Webhooks");

        Assert.True(strip.Find(".tab-menu-right").HasAttribute("disabled"));
    }

    [BunitFact]
    public void Close_others_keeps_only_the_clicked_tab()
    {
        var strip = Render();
        RightClick(strip, "customers");

        strip.Find(".tab-menu-others").Click();

        Assert.Equal(["customers"], Titles(strip));
    }

    [BunitFact]
    public void Close_all_leaves_only_pinned_tabs_and_goes_home_when_none_remain()
    {
        var strip = Render();
        State.TogglePin("/databases/d1");
        RightClick(strip, "Webhooks");

        strip.Find(".tab-menu-all").Click();

        Assert.Equal(["Boutique"], Titles(strip));
        Assert.EndsWith("/databases/d1", Nav.Uri);

        State.TogglePin("/databases/d1");
        RightClick(strip, "Boutique");
        strip.Find(".tab-menu-all").Click();
        Assert.Empty(Titles(strip));
        Assert.Equal("http://localhost/", Nav.Uri);
    }
}
