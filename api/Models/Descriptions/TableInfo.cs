using Dapper.Contrib.Extensions;

namespace GranitDB.API.Models.Descriptions;

[Table("Tables")]
public class TableInfo
{
    [Key] public string Id { get; set; }

    public string Name { get; set; }

    public string DatabaseId { get; set; } // Relation vers DatabaseInfo

    public int ColumnCount { get; set; }

    public string MetaInfo { get; set; }

    public List<RelationInfo> Relations { get; set; }
    public List<ColumnInfo> Columns { get; set; }
}