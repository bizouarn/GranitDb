using GranitDb.Interfaces;

namespace GranitDb.Client.Models;

public class DatabaseInfo : IDatabaseInfo
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsPublic { get; set; }
    public string? ProjectUrl { get; set; }
    public string? DatabaseUrl { get; set; }

    /// <summary>La base peut être relue à sa source (fichier SQLite conservé, connexion SQL Server enregistrée).</summary>
    public bool CanResync { get; set; }

    /// <summary>Source SQL Server affichable (serveur / base), sans secret.</summary>
    public string? SourceLabel { get; set; }
}
