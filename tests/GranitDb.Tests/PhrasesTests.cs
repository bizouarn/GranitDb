using GranitDb.Client.Workspace;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lot 1 : les résultats techniques dits en phrases.</summary>
public class PhrasesTests
{
    [Fact]
    public void A_sync_summary_counts_each_kind_with_the_right_agreement()
    {
        var text = Phrases.SyncSummary([("column", "added"), ("column", "added"), ("table", "removed"), ("index", "changed"), ("relation", "added")]);

        Assert.Equal("2 colonnes ajoutées, 1 relation ajoutée, 1 index modifié, 1 table supprimée.", text);
    }

    [Fact]
    public void A_sync_without_change_says_so()
    {
        Assert.Equal("Aucun changement.", Phrases.SyncSummary([]));
    }

    [Fact]
    public void Losing_documentation_is_said_in_plain_words_in_singular_and_plural()
    {
        Assert.StartsWith("1 suppression emporte", Phrases.LosingDocumentation(1));
        Assert.StartsWith("3 suppressions emportent", Phrases.LosingDocumentation(3));
        Assert.Contains("à vérifier", Phrases.LosingDocumentation(3));
    }

    [Theory]
    [InlineData(true, 3, 0, "Si vous modifiez cette colonne, 3 éléments peuvent être touchés.")]
    [InlineData(false, 1, 0, "Si vous modifiez cette table, 1 élément peut être touché.")]
    [InlineData(true, 0, 0, "Rien ne référence cette colonne : la modifier n'a pas d'effet ailleurs.")]
    [InlineData(false, 2, 1, "Si vous modifiez cette table, 2 éléments peuvent être touchés. Elle s'appuie elle-même sur 1 autre élément.")]
    public void An_impact_says_what_changing_the_element_may_touch(bool column, int dependents, int dependencies, string expected)
    {
        Assert.Equal(expected, Phrases.Impact(column, dependents, dependencies));
    }
}

public class HiddenRelationsPhraseTests
{
    [Theory]
    [InlineData(12, 0, "12 relations masquées dans ce domaine.")]
    [InlineData(1, 0, "1 relation masquée dans ce domaine.")]
    [InlineData(0, 3, "3 relations de nouveau affichées.")]
    [InlineData(2, 1, "2 relations masquées, 1 de nouveau affichée.")]
    [InlineData(0, 0, "Rien n'a changé : les relations du domaine étaient déjà ainsi.")]
    public void A_bulk_change_of_hidden_relations_is_told_in_a_sentence(int hidden, int shown, string expected) =>
        Assert.Equal(expected, GranitDb.Client.Workspace.Phrases.HiddenRelationsChanged(hidden, shown));
}

public class SyncResultPhraseTests
{
    [Fact]
    public void The_result_of_a_synchronisation_is_told_like_its_preview()
    {
        var summary = new GranitDb.Client.Models.SyncSummaryView { TablesAdded = 2, ColumnsAdded = 1, ColumnsRemoved = 3, IndexesAdded = 1 };

        Assert.Equal("2 tables ajoutées, 1 colonne ajoutée, 1 index ajouté, 3 colonnes supprimées.",
            GranitDb.Client.Workspace.Phrases.SyncSummary(summary));
        Assert.Equal("Aucun changement.", GranitDb.Client.Workspace.Phrases.SyncSummary(new GranitDb.Client.Models.SyncSummaryView()));
    }
}

public class DomainSavedPhraseTests
{
    [Theory]
    [InlineData("Achats", true, 3, 0, 0, "Domaine Achats créé avec 3 tables.")]
    [InlineData("Achats", true, 1, 0, 0, "Domaine Achats créé avec 1 table.")]
    [InlineData("Achats", true, 0, 0, 0, "Domaine Achats créé, sans table.")]
    [InlineData("Ventes", false, 5, 2, 1, "Domaine Ventes modifié : 2 tables ajoutées, 1 retirée.")]
    [InlineData("Ventes", false, 5, 0, 3, "Domaine Ventes modifié : 3 retirées.")]
    [InlineData("Ventes", false, 5, 0, 0, "Domaine Ventes modifié.")]
    public void Saving_a_domain_says_what_changed(string name, bool created, int tables, int added, int removed, string expected) =>
        Assert.Equal(expected, GranitDb.Client.Workspace.Phrases.DomainSaved(name, created, tables, added, removed));
}
