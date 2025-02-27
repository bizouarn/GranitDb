using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GranitDB.API.Models.Descriptions;

[Table("Columns")]
public class ColumnInfo
{
    [Key] public string Id { get; set; }

    public string DatabaseId { get; set; } // Foreign key to DatabaseInfo

    public string TableId { get; set; } // Foreign key to TableInfo

    public string Name { get; set; }

    public string DataType { get; set; }

    public bool IsNullable { get; set; }
    public string MetaInfo { get; set; }
}