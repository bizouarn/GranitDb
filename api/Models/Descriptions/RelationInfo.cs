using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GranitDB.API.Models.Descriptions;

[Table("Relations")]
public class RelationInfo
{
    [Key] public string Id { get; set; }

    public string FirstId { get; set; }

    public string SecondId { get; set; }

    public string RelationType { get; set; } // Ex: One-to-One, One-to-Many, Many-to-Many
    public string MetaInfo { get; set; }
}