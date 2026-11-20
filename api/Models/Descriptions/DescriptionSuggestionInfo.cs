using GranitDB.API.Models.Descriptions.Base;
using System.ComponentModel.DataAnnotations.Schema;

namespace GranitDB.API.Models.Descriptions;

/// <summary>
///     Une proposition de l'assistant, en attente de relecture : un texte de description (proposé par un modèle d'IA) ou le marquage
///     d'une colonne comme sensible (proposé par des règles de nommage). Rien n'est écrit dans le catalogue avant acceptation, et une
///     description déjà saisie n'est jamais remplacée.
/// </summary>
[Table("DescriptionSuggestions")]
public class DescriptionSuggestionInfo : InfoBase
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";

    public const string KindDescription = "description";
    public const string KindSensitive = "sensitive";

    public const string TargetDatabase = "database";
    public const string TargetTable = "table";
    public const string TargetColumn = "column";
    public const string TargetRelation = "relation";

    public string DatabaseId { get; set; } = "";

    /// <summary><c>description</c> (texte à écrire) ou <c>sensitive</c> (colonne à marquer sensible).</summary>
    public string Kind { get; set; } = KindDescription;

    public string TargetType { get; set; } = "";

    public string TargetId { get; set; } = "";

    /// <summary>Le texte proposé ; pour <c>sensitive</c>, la raison du marquage.</summary>
    public string Text { get; set; } = "";

    /// <summary>Le modèle qui l'a proposé (ou <c>règles</c>).</summary>
    public string Model { get; set; } = "";

    /// <summary>Proposition peu sûre (nom cryptique, doute du modèle) : à relire avec plus d'attention, jamais acceptée en lot.</summary>
    public bool Uncertain { get; set; }

    public string Status { get; set; } = Pending;

    public DateTime CreatedAt { get; set; }
}
