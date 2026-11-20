namespace GranitDB.API.Models;

public enum FilterMode
{
    /// <summary>Égalité exacte.</summary>
    Equals,

    /// <summary>Contient le texte, sans tenir compte de la casse.</summary>
    Contains,

    /// <summary>Booléen (<c>true</c> / <c>false</c>).</summary>
    Bool,

    /// <summary>La valeur est l'une de <see cref="CatalogFilter.Values" /> (aucune : aucun résultat).</summary>
    In
}

/// <summary>Filtre SQL sur une propriété mappée d'une entité du catalogue.</summary>
public record CatalogFilter(string Property, string Value, FilterMode Mode = FilterMode.Equals)
{
    /// <summary>Valeurs admises pour <see cref="FilterMode.In" />.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>Les éléments d'une base : le filtre le plus courant du catalogue.</summary>
    public static CatalogFilter[] ForDatabase(string databaseId) => [new CatalogFilter("DatabaseId", databaseId)];

    public static CatalogFilter In(string property, IEnumerable<string> values)
    {
        return new CatalogFilter(property, "", FilterMode.In) { Values = values.Distinct().ToList() };
    }
}
