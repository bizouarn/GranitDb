using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Relations")]
public class RelationInfo : InfoBase, IRelationInfo
{
    public string FirstId { get; set; }

    public string SecondId { get; set; }

    public string RelationType { get; set; } // Ex: One-to-One, One-to-Many, Many-to-Many
}