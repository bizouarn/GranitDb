using Dapper.Contrib.Extensions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Tables")]
public class TableInfo : InfoBase, ITableInfo
{
    public TableInfo()
    {
        Relations = new List<IRelationInfo>();
        Columns = new List<IColumnInfo>();
    }

    public string Name { get; set; }

    public string DatabaseId { get; set; } // Relation vers DatabaseInfo

    [Computed] public List<IRelationInfo> Relations { get; set; }

    [Computed] public List<IColumnInfo> Columns { get; set; }
}