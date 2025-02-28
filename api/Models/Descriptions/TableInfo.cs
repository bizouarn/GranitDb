using Dapper.Contrib.Extensions;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Tables")]
public class TableInfo : InfoBase
{
    public TableInfo()
    {
        Relations = new List<RelationInfo>();
        Columns = new List<ColumnInfo>();
    }

    public string Name { get; set; }

    public string DatabaseId { get; set; } // Relation vers DatabaseInfo

    [Computed] public List<RelationInfo> Relations { get; set; }

    [Computed] public List<ColumnInfo> Columns { get; set; }
}