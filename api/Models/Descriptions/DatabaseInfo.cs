using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Databases")]
public class DatabaseInfo : InfoBase
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

    /// <summary>Resynchroniser la base automatiquement toutes les N minutes ; absent : jamais (seulement à la demande).</summary>
    public int? SyncIntervalMinutes { get; set; }

    /// <summary>Dernière relecture de la source (planifiée ou demandée), en UTC.</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Résultat de la dernière relecture ; absent tant qu'il n'y en a pas eu.</summary>
    public bool? LastSyncOk { get; set; }

    /// <summary>Raison du dernier échec (sans secret), tronquée.</summary>
    public string? LastSyncError { get; set; }

    /// <summary>Description de la source sans secret (« serveur / base »), affichée par l'interface.</summary>
    public string? SourceLabel { get; set; }
}
