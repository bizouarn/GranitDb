using Dapper.Contrib.Extensions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Columns")]
public class ColumnInfo : InfoBase, IColumnInfo
{
    public string DatabaseId { get; set; } // Foreign key to DatabaseInfo

    public string TableId { get; set; } // Foreign key to TableInfo

    public string Name { get; set; }

    public string DataType { get; set; }

    public bool IsNullable { get; set; }
}