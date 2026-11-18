using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;

namespace GranitDB.API.Models;

/// <summary>
///     Ce que l'API dit d'une base. Distinct de l'entité du catalogue : le chemin du fichier source et la connexion
///     enregistrée sont des détails du serveur, jamais renvoyés (même chiffrée, même pour un administrateur).
/// </summary>
public record DatabaseView(
    string Id,
    string Name,
    string Type,
    bool IsPublic,
    string? ProjectUrl,
    string? DatabaseUrl,
    string? Description,
    string? MetaInfo,
    bool CanResync,
    string? SourceLabel)
{
    public static DatabaseView From(DatabaseInfo database)
    {
        return new DatabaseView(database.Id, database.Name, database.Type, database.IsPublic, database.ProjectUrl,
            database.DatabaseUrl, database.Description, database.MetaInfo,
            !string.IsNullOrWhiteSpace(database.SourcePath) || !string.IsNullOrWhiteSpace(database.SourceSecret),
            database.SourceLabel);
    }
}

/// <summary>Bilan d'une synchronisation (comptes seulement : pas l'entité de la base).</summary>
public record SyncSummaryView(
    int TablesAdded,
    int TablesRemoved,
    int ColumnsAdded,
    int ColumnsUpdated,
    int ColumnsRemoved,
    int RelationsCreated,
    int IndexesAdded,
    int IndexesRemoved)
{
    public static SyncSummaryView From(SyncResult sync)
    {
        return new SyncSummaryView(sync.TablesAdded, sync.TablesRemoved, sync.ColumnsAdded, sync.ColumnsUpdated,
            sync.ColumnsRemoved, sync.RelationsCreated, sync.IndexesAdded, sync.IndexesRemoved);
    }
}

/// <summary>Réponse d'un import : la base créée ou mise à jour et ce que la synchronisation a changé.</summary>
public record ImportView(DatabaseView Database, SyncSummaryView Sync)
{
    public static ImportView From(ImportResult result)
    {
        return new ImportView(DatabaseView.From(result.Database), SyncSummaryView.From(result.Sync));
    }
}
