namespace GranitDb.Client.Workspace;

/// <summary>Les résultats techniques dits en phrases : ce que ça veut dire pour la personne qui lit, pas le nom des champs.</summary>
public static class Phrases
{
    private static readonly Dictionary<string, (string One, string Many, bool Feminine)> Kinds = new()
    {
        ["table"] = ("table", "tables", true),
        ["column"] = ("colonne", "colonnes", true),
        ["index"] = ("index", "index", false),
        ["relation"] = ("relation", "relations", true)
    };

    private static readonly string[] Order = ["table", "column", "index", "relation"];

    /// <summary>« 2 colonnes ajoutées, 1 table supprimée » ; chaque action est comptée par sorte d'élément.</summary>
    public static string SyncSummary(IEnumerable<(string Kind, string Action)> changes)
    {
        var list = changes.ToList();
        var parts = new List<string>();
        foreach (var (action, participleBase) in new[] { ("added", "ajouté"), ("changed", "modifié"), ("removed", "supprimé") })
        {
            foreach (var kind in Order)
            {
                var count = list.Count(c => c.Action == action && c.Kind == kind);
                if (count == 0 || !Kinds.TryGetValue(kind, out var k)) continue;
                var word = count == 1 ? k.One : k.Many;
                var participle = participleBase + (k.Feminine ? (count == 1 ? "e" : "es") : (count == 1 ? "" : "s"));
                parts.Add($"{count} {word} {participle}");
            }
        }

        return parts.Count == 0 ? "Aucun changement." : string.Join(", ", parts) + ".";
    }

    /// <summary>Ce que la perte de documentation veut dire : du texte saisi à la main disparaît avec l'élément.</summary>
    public static string LosingDocumentation(int count) => count == 1
        ? "1 suppression emporte une documentation que vous avez saisie (description, notes, étiquettes, marquage sensible ou relation manuelle) : à vérifier avant d'appliquer."
        : $"{count} suppressions emportent une documentation que vous avez saisie (description, notes, étiquettes, marquage sensible ou relation manuelle) : à vérifier avant d'appliquer.";

    /// <summary>« Si vous modifiez cette colonne, 3 éléments peuvent être touchés. »</summary>
    public static string Impact(bool isColumn, int dependents, int dependencies)
    {
        var what = isColumn ? "cette colonne" : "cette table";
        var text = dependents == 0
            ? $"Rien ne référence {what} : la modifier n'a pas d'effet ailleurs."
            : $"Si vous modifiez {what}, {dependents} {(dependents == 1 ? "élément peut être touché" : "éléments peuvent être touchés")}.";
        if (dependencies > 0)
            text += $" Elle s'appuie elle-même sur {dependencies} {(dependencies == 1 ? "autre élément" : "autres éléments")}.";
        return text;
    }

    /// <summary>Le message après l'import d'une base serveur, ou après le remplacement de sa connexion.</summary>
    public static string ServerSaved(GranitDb.Client.Models.ImportView result, bool isEdit) => isEdit
        ? $"Connexion enregistrée et base resynchronisée : {result.Sync.TablesAdded} table(s) ajoutée(s), {result.Sync.TablesRemoved} supprimée(s)."
        : $"Base {result.Database.Name} importée : {result.Sync.TablesAdded} table(s), {result.Sync.ColumnsAdded} colonne(s).";
}
