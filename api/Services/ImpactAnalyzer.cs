namespace GranitDB.API.Services;

/// <summary>Un nœud atteint par l'analyse, à <see cref="Depth" /> relations du point de départ (1 = direct).</summary>
public record ImpactStep(string Key, int Depth);

/// <summary>
///     Parcours en largeur du graphe des dépendances. Une arête va de l'élément <b>référencé</b> vers l'élément qui
///     le <b>référence</b> (clé étrangère) : modifier le premier peut casser le second. Sans risque de boucle : un
///     nœud n'est visité qu'une fois, à sa plus petite profondeur.
/// </summary>
public static class ImpactAnalyzer
{
    /// <summary>Éléments qui dépendent de <paramref name="start" /> (directement ou par chaîne de références).</summary>
    public static IReadOnlyList<ImpactStep> Dependents(string start,
        IEnumerable<(string Referenced, string Referencing)> edges)
    {
        return Walk(start, edges.ToLookup(e => e.Referenced, e => e.Referencing));
    }

    /// <summary>Éléments dont <paramref name="start" /> dépend (ce qu'il référence, directement ou en chaîne).</summary>
    public static IReadOnlyList<ImpactStep> Dependencies(string start,
        IEnumerable<(string Referenced, string Referencing)> edges)
    {
        return Walk(start, edges.ToLookup(e => e.Referencing, e => e.Referenced));
    }

    private static List<ImpactStep> Walk(string start, ILookup<string, string> next)
    {
        var seen = new HashSet<string> { start };
        var result = new List<ImpactStep>();
        var queue = new Queue<(string Key, int Depth)>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0)
        {
            var (key, depth) = queue.Dequeue();
            foreach (var target in next[key].OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!seen.Add(target)) continue;
                result.Add(new ImpactStep(target, depth + 1));
                queue.Enqueue((target, depth + 1));
            }
        }

        return result;
    }
}
