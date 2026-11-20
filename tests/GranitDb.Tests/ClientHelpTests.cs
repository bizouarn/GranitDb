using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Layout;
using GranitDb.Client.Pages;
using GranitDb.Client.Workspace;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lots 1 et 2 : glossaire au survol, page d'aide, feuille des raccourcis, Échap dans le panneau de détails.</summary>
public class ClientHelpTests : ShellTestBase
{
    [BunitFact]
    public void A_term_explains_itself_on_hover_and_for_screen_readers()
    {
        var term = Ctx.Render<Term>(p => p.Add(x => x.Key, "cle-etrangere"));

        var abbr = term.Find("abbr.glossary-term");
        Assert.Contains("Clé étrangère", abbr.TextContent);
        Assert.Contains("renvoie à la clé d'une autre table", abbr.GetAttribute("title"));
        Assert.Equal("0", abbr.GetAttribute("tabindex")); // atteignable au clavier
        Assert.Contains("Clé étrangère (FK) :", abbr.GetAttribute("aria-label"));
    }

    [BunitFact]
    public void The_help_page_lists_every_term_of_the_glossary()
    {
        var page = Ctx.Render<Help>();

        Assert.Equal(Glossary.Entries.Count, page.FindAll(".glossary-entry").Count);
        Assert.Contains("Cardinalité", page.Markup);
        Assert.Equal(Glossary.Entries.Count, Glossary.Entries.Select(e => e.Key).Distinct().Count());
    }

    [BunitFact]
    public void The_help_page_opens_the_shortcuts_sheet()
    {
        var page = Ctx.Render<Help>();

        page.Find("button.help-shortcuts").Click();

        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".shortcuts-sheet")));
        Assert.Contains("Ctrl + K", Dialogs!.Markup);
        Assert.Contains("Échap", Dialogs!.Markup);
    }

    [BunitFact]
    public void The_palette_offers_the_help_and_the_shortcuts()
    {
        State.SetPalette(true);
        var palette = Ctx.Render<CommandPalette>();

        palette.WaitForAssertion(() => Assert.Contains("Afficher les raccourcis clavier", palette.Markup));
        Assert.Contains("Ouvrir l'aide et le glossaire", palette.Markup);
    }

    [BunitFact]
    public void Escape_in_the_detail_panel_clears_the_selection()
    {
        var panel = Ctx.Render<DetailPanel>();
        State.Select(new SelectionItem("database", "d1", "d1"));
        Assert.NotNull(State.Selection);

        panel.Find("aside").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Null(State.Selection);
    }

    [BunitFact]
    public void Escape_in_the_detail_panel_leaves_the_selection_while_a_field_has_the_focus()
    {
        Ctx.JSInterop.Setup<bool>("granitdb.isTyping").SetResult(true);
        var panel = Ctx.Render<DetailPanel>();
        State.Select(new SelectionItem("database", "d1", "d1"));

        panel.Find("aside").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.NotNull(State.Selection);
    }
}
