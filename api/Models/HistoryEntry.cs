namespace GranitDB.API.Models;

/// <summary>Une modification d'une description (ou de <c>MetaInfo</c>) : quoi, quand, ancienne et nouvelle valeur.</summary>
public class HistoryEntry
{
    public string Id { get; set; } = "";

    /// <summary>database, table, column, relation ou index.</summary>
    public string EntityType { get; set; } = "";

    public string EntityId { get; set; } = "";

    /// <summary>Champ modifié : Description ou MetaInfo.</summary>
    public string Field { get; set; } = "";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTime ChangedAt { get; set; }

    /// <summary>Auteur : renseigné quand l'authentification existera (jalon 6).</summary>
    public string? ChangedBy { get; set; }
}
