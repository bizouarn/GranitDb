namespace GranitDB.API.Models;

/// <summary>Un résultat de recherche globale : une base, une table, une colonne ou un index.</summary>
public class SearchHit
{
    /// <summary><c>database</c>, <c>table</c>, <c>column</c>, <c>index</c> ou <c>domain</c>.</summary>
    public string Kind { get; set; } = "";

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string DatabaseId { get; set; } = "";

    public string DatabaseName { get; set; } = "";

    /// <summary>Table contenant la colonne ou l'index (ou la table elle-même).</summary>
    public string? TableId { get; set; }

    public string? TableName { get; set; }

    /// <summary>Pourquoi un élément est trouvé sans que son nom corresponde : « domaine « X » » ou « étiquette « X » » ; absent sinon.</summary>
    public string? Via { get; set; }
}
