using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Tables")]
public class TableInfo : InfoBase
{
    public string Name { get; set; } = "";

    public string DatabaseId { get; set; } = ""; // Relation vers DatabaseInfo

    public string TableType { get; set; } = "Table"; // Table, View, Virtual

    /// <summary>
    ///     Table saisie à la main (fichier plat, service, table prévue) : elle n'existe pas dans la source et la synchronisation
    ///     ne la supprime pas. Si la source gagne ensuite une table du même nom, la source l'emporte (le marquage est levé).
    /// </summary>
    public bool IsManual { get; set; }

    [NotMapped] public List<RelationInfo> Relations { get; set; } = [];

    [NotMapped] public List<ColumnInfo> Columns { get; set; } = [];

    [NotMapped] public List<IndexInfo> Indexes { get; set; } = [];
}
