using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Indexes")]
public class IndexInfo : InfoBase, IIndexInfo
{
    public string DatabaseId { get; set; } = "";

    public string TableId { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsUnique { get; set; }

    public string ColumnNames { get; set; } = "";
}
