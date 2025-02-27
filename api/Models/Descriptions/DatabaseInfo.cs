using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace GranitDB.API.Models.Descriptions;

[Table("Databases")]
public class DatabaseInfo
{
    [Key] public string Id { get; set; }

    public string Name { get; set; }

    public string Type { get; set; } // Ex: SQL Server, PostgreSQL, MySQL

    public bool IsPublic { get; set; }

    public string Description { get; set; }
    public string ProjectUrl { get; set; }
    public string DatabaseUrl { get; set; }

    public string MetaInfo { get; set; }
}