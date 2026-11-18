using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Tables")]
public class TableInfo : InfoBase
{
    public string Name { get; set; } = "";

    public string DatabaseId { get; set; } = ""; // Relation vers DatabaseInfo

    public string TableType { get; set; } = "Table"; // Table, View, Virtual

    [NotMapped] public List<RelationInfo> Relations { get; set; } = [];

    [NotMapped] public List<ColumnInfo> Columns { get; set; } = [];

    [NotMapped] public List<IndexInfo> Indexes { get; set; } = [];
}
