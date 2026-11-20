using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

/// <summary>
///     Une relation que le schéma ne déclare pas, proposée par la détection (nom, type, unicité de la cible et vérification par le
///     profilage) et en attente de relecture. Seuls des comptes sont gardés, jamais une valeur de la source.
/// </summary>
[Table("RelationSuggestions")]
public class RelationSuggestionInfo : InfoBase
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public string DatabaseId { get; set; } = "";

    /// <summary>Colonne qui référencerait (le côté « plusieurs »).</summary>
    public string ChildColumnId { get; set; } = "";

    public string ChildTableId { get; set; } = "";

    /// <summary>Colonne référencée (clé primaire ou colonne unique).</summary>
    public string ParentColumnId { get; set; } = "";

    public string ParentTableId { get; set; } = "";

    /// <summary>Score de 0 à 100 : plus il est haut, plus la relation est vraisemblable.</summary>
    public int Score { get; set; }

    /// <summary>Les raisons du score, une par ligne, lisibles par une personne.</summary>
    public string Reasons { get; set; } = "";

    public long NonNullCount { get; set; }

    /// <summary>Valeurs non nulles de la colonne qui n'existent pas dans la colonne référencée.</summary>
    public long OrphanCount { get; set; }

    public string Status { get; set; } = Pending;

    public DateTime DetectedAt { get; set; }
}
