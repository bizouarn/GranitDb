using GranitDB.API.Data;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Security;

/// <summary>
///     Lecture sans compte des bases marquées publiques (<c>IsPublic</c>). Liste blanche : seules les lectures qui montrent
///     une base publique (sa fiche, ses tables, colonnes, domaines, complétude, impact, étiquettes, exports) passent ; tout
///     le reste demande une session. Une ressource inconnue ou d'une base privée reste refusée (401, comme sans compte : on
///     n'apprend pas qu'elle existe).
/// </summary>
public sealed class PublicAccess(ICatalogRepository repository, IOptions<AuthOptions> options)
{
    public bool Enabled => options.Value.AllowPublicDatabases;

    public async Task<bool> MayReadAsync(string? controller, string? action, IDictionary<string, object?> route, CancellationToken ct)
    {
        if (!Enabled) return false;
        string? Value(string key) => route.TryGetValue(key, out var v) ? v?.ToString() : null;

        switch (controller)
        {
            case "Capabilities":
                return true;
            case "Search": // les résultats sont filtrés par le contrôleur
                return true;
            case "DatabaseInfo":
                if (action == "GetAllT") return true; // la liste est réduite aux bases publiques par le contrôleur
                return await IsPublicDatabaseAsync(Value("id"), ct);
            case "TableInfo" when action == "GetTablesByDatabaseId":
                return await IsPublicDatabaseAsync(Value("databaseId"), ct);
            case "TableInfo" when action is "GetT" or "GetColumns" or "GetIndexes" or "GetRelations":
            case "Impact" when action == "OfTable":
                return await IsPublicTableAsync(Value("id"), ct);
            case "Impact" when action == "OfColumn":
                var column = Value("id") is { } id ? await repository.GetAsync<ColumnInfo>(id, ct) : null;
                return await IsPublicTableAsync(column?.TableId, ct);
            case "DomainInfo" when action == "OfDatabase":
            case "TagInfo" when action == "OfDatabase":
                return await IsPublicDatabaseAsync(Value("databaseId"), ct);
            case "Completeness" when action == "OfDatabase":
                return await IsPublicDatabaseAsync(Value("id"), ct);
            default:
                return false;
        }
    }

    private async Task<bool> IsPublicDatabaseAsync(string? id, CancellationToken ct) =>
        id != null && (await repository.GetAsync<DatabaseInfo>(id, ct))?.IsPublic == true;

    private async Task<bool> IsPublicTableAsync(string? id, CancellationToken ct) =>
        id != null && await repository.GetAsync<TableInfo>(id, ct) is { } table && await IsPublicDatabaseAsync(table.DatabaseId, ct);
}
