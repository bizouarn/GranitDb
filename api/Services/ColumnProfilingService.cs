using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>Profil d'une colonne tel que l'API le montre : des comptes, des proportions et un genre, jamais une valeur.</summary>
public record ColumnProfileView(string ColumnId, string Column, long RowCount, long NullCount, double NullRatio, long? DistinctCount,
    string Kind, DateTime ProfiledAt);

/// <summary>Les profils d'une table ; <c>ProfiledAt</c> est nul tant qu'elle n'a pas été profilée.</summary>
public record TableProfileView(string TableId, long? RowCount, DateTime? ProfiledAt, List<ColumnProfileView> Columns);

/// <summary>Bilan du profilage d'une base : tables profilées, en échec (avec la raison) et restantes pour une prochaine demande.</summary>
public record ProfileRunView(int TablesProfiled, int TablesFailed, int TablesRemaining, List<string> Errors);

/// <summary>
///     Genre d'une colonne d'après ses comptes : <c>empty</c> (que des nuls ou table vide), <c>constant</c> (une seule valeur),
///     <c>unique</c> (autant de valeurs que de lignes non nulles : identifiant probable), <c>low</c> (peu de valeurs : une liste
///     de codes), <c>high</c> (beaucoup de valeurs), <c>unknown</c> (type dont on ne compte pas les valeurs distinctes).
/// </summary>
public static class ProfileKind
{
    public const int LowCardinality = 20;

    public static string Of(long rowCount, long nullCount, long? distinct)
    {
        var nonNull = rowCount - nullCount;
        if (rowCount == 0 || nonNull <= 0) return "empty";
        if (distinct == null) return "unknown";
        if (distinct <= 1) return "constant";
        if (distinct == nonNull) return "unique";
        return distinct <= LowCardinality || distinct * 100 < nonNull ? "low" : "high";
    }
}

/// <summary>
///     Profilage léger à la demande : la source compte lignes, nuls et valeurs distinctes de chaque colonne (lecture seule),
///     le catalogue ne garde que ces comptes. Désactivé par défaut (<c>Catalog:AllowProfiling</c>).
/// </summary>
public class ColumnProfilingService(ICatalogRepository repository, DatabaseImportService import,
    IEnumerable<IColumnProfiler> profilers, IOptions<CatalogOptions> options)
{
    private CatalogOptions Options => options.Value;

    private void EnsureAllowed()
    {
        if (!Options.AllowProfiling)
            throw new GranitExceptions("Le profilage des colonnes est désactivé (Catalog:AllowProfiling = false) : il lit des données de la source, pas seulement son schéma.");
    }

    private IColumnProfiler ProfilerOf(string type) =>
        profilers.FirstOrDefault(p => p.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidSourceException($"Type de base non pris en charge pour le profilage : {type}");

    /// <summary>Les profils enregistrés d'une table (vides tant qu'elle n'a pas été profilée) ; <c>null</c> si la table n'existe pas.</summary>
    public async Task<TableProfileView?> GetAsync(string tableId, CancellationToken ct = default)
    {
        var table = await repository.GetAsync<TableInfo>(tableId, ct);
        return table == null ? null : await ViewAsync(table, ct);
    }

    private async Task<TableProfileView> ViewAsync(TableInfo table, CancellationToken ct)
    {
        var profiles = await repository.FindAsync<ColumnProfileInfo>(nameof(ColumnProfileInfo.TableId), table.Id, ct);
        var columns = (await repository.FindAsync<ColumnInfo>(nameof(ColumnInfo.TableId), table.Id, ct)).ToDictionary(c => c.Id);
        var views = profiles.Where(p => columns.ContainsKey(p.ColumnId))
            .OrderBy(p => columns[p.ColumnId].Position)
            .Select(p => new ColumnProfileView(p.ColumnId, columns[p.ColumnId].Name, p.RowCount, p.NullCount,
                p.RowCount == 0 ? 0 : Math.Round((double)p.NullCount / p.RowCount, 4), p.DistinctCount,
                ProfileKind.Of(p.RowCount, p.NullCount, p.DistinctCount), p.ProfiledAt)).ToList();
        return new TableProfileView(table.Id, views.Count == 0 ? null : views[0].RowCount,
            views.Count == 0 ? null : views.Max(v => v.ProfiledAt), views);
    }

    /// <summary>Profile une table (seules les vraies tables, pas les vues) et remplace ses profils. <c>null</c> si elle n'existe pas.</summary>
    public async Task<TableProfileView?> ProfileTableAsync(string tableId, CancellationToken ct = default)
    {
        EnsureAllowed();
        var table = await repository.GetAsync<TableInfo>(tableId, ct);
        if (table == null) return null;
        var database = await repository.GetAsync<DatabaseInfo>(table.DatabaseId, ct)
                       ?? throw new GranitExceptions("La base de cette table n'existe plus.");
        var source = (await import.ResolveSourceAsync(database))!.Value;
        await ProfileAsync(table, source, ct);
        return await ViewAsync(table, ct);
    }

    private async Task ProfileAsync(TableInfo table, (string Type, string Source) source, CancellationToken ct)
    {
        if (table.TableType != "Table")
            throw new GranitExceptions($"{table.Name} est une {(table.TableType == "View" ? "vue" : "table virtuelle")} : seules les tables se profilent.");
        var columns = (await repository.FindAsync<ColumnInfo>(nameof(ColumnInfo.TableId), table.Id, ct)).OrderBy(c => c.Position).ToList();
        if (columns.Count == 0) throw new GranitExceptions($"{table.Name} n'a aucune colonne à profiler.");

        var result = await ProfilerOf(source.Type).ProfileAsync(source.Source, table.Name,
            columns.Select(c => new ProfiledColumn(c.Name, c.DataType)).ToList(), Options.ProfilingTimeoutSeconds, ct);

        var byName = columns.ToDictionary(c => c.Name);
        var now = DateTime.UtcNow;
        await repository.InTransactionAsync(async repo =>
        {
            foreach (var old in await repo.FindAsync<ColumnProfileInfo>(nameof(ColumnProfileInfo.TableId), table.Id, ct))
                await repo.DeleteAsync<ColumnProfileInfo>(old.Id, ct);
            foreach (var column in result.Columns.Where(c => byName.ContainsKey(c.Column)))
                await repo.CreateAsync(new ColumnProfileInfo
                {
                    Id = Guid.NewGuid().ToString(), TableId = table.Id, ColumnId = byName[column.Column].Id,
                    RowCount = result.RowCount, NullCount = column.NullCount, DistinctCount = column.DistinctCount, ProfiledAt = now
                }, ct);
            return 0;
        }, ct);
    }

    /// <summary>
    ///     Profile les tables d'une base, les plus anciennement profilées d'abord, au plus <c>ProfilingMaxTables</c> par demande
    ///     (une grosse base se profile en plusieurs demandes). Une table en échec n'arrête pas les autres. <c>null</c> si la base n'existe pas.
    /// </summary>
    public async Task<ProfileRunView?> ProfileDatabaseAsync(string databaseId, CancellationToken ct = default)
    {
        EnsureAllowed();
        var database = await repository.GetAsync<DatabaseInfo>(databaseId, ct);
        if (database == null) return null;
        var source = (await import.ResolveSourceAsync(database))!.Value;

        var tables = (await repository.GetAllAsync<TableInfo>([new CatalogFilter("DatabaseId", databaseId)], ct))
            .Where(t => t.TableType == "Table").ToList();
        var lastByTable = (await repository.GetAllAsync<ColumnProfileInfo>(
                [CatalogFilter.In("TableId", tables.Select(t => t.Id))], ct))
            .GroupBy(p => p.TableId).ToDictionary(g => g.Key, g => g.Max(p => p.ProfiledAt));
        var queue = tables.OrderBy(t => lastByTable.TryGetValue(t.Id, out var at) ? at : DateTime.MinValue)
            .ThenBy(t => t.Name, StringComparer.Ordinal).ToList();

        var take = Math.Max(1, Options.ProfilingMaxTables);
        int done = 0, failed = 0;
        var errors = new List<string>();
        foreach (var table in queue.Take(take))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProfileAsync(table, source, ct);
                done++;
            }
            catch (GranitExceptions e)
            {
                failed++;
                if (errors.Count < 20) errors.Add($"{table.Name} : {e.Message}");
            }
        }

        return new ProfileRunView(done, failed, Math.Max(0, queue.Count - take), errors);
    }
}
