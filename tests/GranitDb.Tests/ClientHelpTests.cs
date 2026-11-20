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
    public void A_term_links_to_its_entry_in_the_help_page_unless_told_not_to()
    {
        var term = Ctx.Render<Term>(p => p.Add(x => x.Key, "domaine"));

        Assert.Equal("/help#domaine", term.Find("a.glossary-more").GetAttribute("href"));
        Assert.Equal("En savoir plus : Domaine", term.Find("a.glossary-more").GetAttribute("aria-label"));
        Assert.DoesNotContain("ⓘ", term.Markup); // le symbole vient du CSS : il ne s'ajoute pas au texte lu

        var plain = Ctx.Render<Term>(p => p.Add(x => x.Key, "domaine").Add(x => x.More, false));
        Assert.Empty(plain.FindAll("a.glossary-more"));
    }

    [BunitFact]
    public void The_vocabulary_of_the_management_page_is_explained()
    {
        var page = Ctx.Render<Manage>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("abbr[data-term='etiquette']")));

        Assert.NotEmpty(page.FindAll("abbr[data-term='domaine']"));
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

/// <summary>Lot 2 : les raccourcis sont écrits une fois et rappelés dans les infobulles des boutons.</summary>
public class ShortcutHintTests
{
    [Fact]
    public void A_hint_puts_the_keys_after_the_label_and_the_sheet_spaces_them()
    {
        Assert.Equal("Explorateur (Ctrl+B)", GranitDb.Client.Workspace.Shortcuts.Hint("Explorateur", GranitDb.Client.Workspace.Shortcuts.Explorer));
        Assert.Equal("Ctrl + K", GranitDb.Client.Workspace.Shortcuts.Spaced(GranitDb.Client.Workspace.Shortcuts.Palette));
        Assert.Equal("Alt + W", GranitDb.Client.Workspace.Shortcuts.Spaced(GranitDb.Client.Workspace.Shortcuts.CloseTab));
    }
}
