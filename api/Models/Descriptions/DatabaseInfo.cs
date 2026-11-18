using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Databases")]
public class DatabaseInfo : InfoBase, IDatabaseInfo
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = ""; // Ex: SqlServer, Sqlite

    public bool IsPublic { get; set; }

    public string? ProjectUrl { get; set; }
    public string? DatabaseUrl { get; set; }

    /// <summary>Chemin du fichier source conservé côté serveur (SQLite) ; jamais renvoyé par l'API.</summary>
    public string? SourcePath { get; set; }

    /// <summary>
    ///     Connexion à la source (SQL Server) chiffrée par <c>ISecretProtector</c> ; jamais renvoyée par l'API, même
    ///     chiffrée.
    /// </summary>
    [JsonIgnore]
    public string? SourceSecret { get; set; }

    /// <summary>Description de la source sans secret (« serveur / base »), affichée par l'interface.</summary>
    public string? SourceLabel { get; set; }
}
