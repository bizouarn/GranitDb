namespace GranitDB.API.Services;

public static class CollectionExtensions
{
    /// <summary>
    ///     Dictionnaire par clé qui tolère les doublons (le premier gagne) : un catalogue peut contenir deux éléments de
    ///     même nom, et la synchronisation ne doit pas échouer pour autant.
    /// </summary>
    public static Dictionary<TKey, T> ToFirstDictionary<T, TKey>(this IEnumerable<T> source, Func<T, TKey> key)
        where TKey : notnull
    {
        return source.GroupBy(key).ToDictionary(g => g.Key, g => g.First());
    }
}
