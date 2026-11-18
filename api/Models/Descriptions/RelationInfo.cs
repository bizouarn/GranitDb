using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Relations")]
public class RelationInfo : InfoBase, IRelationInfo
{
    /// <summary>Marque les relations créées automatiquement par la synchronisation (clés étrangères).</summary>
    public const string AutoMarker = "auto:fk";

    public string FirstId { get; set; } = "";

    public string SecondId { get; set; } = "";

    public string RelationType { get; set; } = ""; // Ex: One-to-One, One-to-Many, Many-to-Many
}
