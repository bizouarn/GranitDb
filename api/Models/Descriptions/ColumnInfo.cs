using Dapper.Contrib.Extensions;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Columns")]
public class ColumnInfo : InfoBase
{
    public string DatabaseId { get; set; } // Foreign key to DatabaseInfo

    public string TableId { get; set; } // Foreign key to TableInfo

    public string Name { get; set; }

    public string DataType { get; set; }

    public bool IsNullable { get; set; }
}