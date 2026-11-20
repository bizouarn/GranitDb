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
