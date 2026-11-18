using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Models.Descriptions;

namespace GranitDB.API.Services;

public record SyncResult(
    DatabaseInfo Database,
    int TablesAdded,
    int TablesRemoved,
    int ColumnsAdded,
    int ColumnsUpdated,
    int ColumnsRemoved,
    int RelationsCreated,
    int IndexesAdded = 0,
    int IndexesRemoved = 0);

/// <summary>
///     Reporte un <see cref="SourceSchema" /> dans le catalogue : crée, met à jour et supprime tables, colonnes et
///     relations (clés étrangères) en conservant les descriptions saisies à la main.
/// </summary>
public class SchemaSynchronizer(ICatalogRepository catalog)
{
    /// <summary>Synchronise dans une seule transaction : en cas d'erreur le catalogue reste inchangé.</summary>
    public Task<SyncResult> SyncAsync(string databaseId, SourceSchema schema, string? name = null,
        CancellationToken ct = default)
    {
        return catalog.InTransactionAsync(repository => SyncCoreAsync(repository, databaseId, schema, name, ct), ct);
    }

    /// <summary>Synchronise au sein d'une transaction déjà ouverte par l'appelant (dépôt lié, voir <see cref="ICatalogRepository.InTransactionAsync{TResult}" />).</summary>
    public static Task<SyncResult> SyncAsync(ICatalogRepository repository, string databaseId, SourceSchema schema,
        string? name = null, CancellationToken ct = default)
    {
        return SyncCoreAsync(repository, databaseId, schema, name, ct);
    }

    private static async Task<SyncResult> SyncCoreAsync(ICatalogRepository repository, string databaseId,
        SourceSchema schema, string? name, CancellationToken ct)
    {
        var database = await repository.GetAsync<DatabaseInfo>(databaseId, ct);
        if (database == null)
        {
            database = await repository.CreateAsync(new DatabaseInfo
            {
                Id = databaseId,
                Name = string.IsNullOrWhiteSpace(name) ? schema.DatabaseName : name,
                Type = schema.Type
            }, ct);
        }
        else
        {
            database.Type = schema.Type;
            if (string.IsNullOrWhiteSpace(database.Name))
                database.Name = string.IsNullOrWhiteSpace(name) ? schema.DatabaseName : name;
            await repository.UpdateAsync(databaseId, database, ct);
        }

        // Tables
        var existingTables = (await repository.FindAsync<TableInfo>(nameof(TableInfo.DatabaseId), databaseId, ct))
            .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        int tablesAdded = 0, tablesRemoved = 0;
        var tableIds = new Dictionary<string, string>();
        foreach (var source in schema.Tables)
        {
            if (existingTables.TryGetValue(source.Name, out var table))
            {
                if (table.TableType != source.TableType)
                {
                    table.TableType = source.TableType;
                    await repository.UpdateAsync(table.Id, table, ct);
                }
            }
            else
            {
                table = await repository.CreateAsync(new TableInfo
                {
                    Name = source.Name, DatabaseId = databaseId, TableType = source.TableType
                }, ct);
                tablesAdded++;
            }

            tableIds[source.Name] = table.Id;
        }

        var sourceTableNames = schema.Tables.Select(t => t.Name).ToHashSet();

        // Colonnes
        var existingColumns = await repository.FindAsync<ColumnInfo>(nameof(ColumnInfo.DatabaseId), databaseId, ct);
        var columnsByKey = existingColumns.GroupBy(c => (c.TableId, c.Name)).ToDictionary(g => g.Key, g => g.First());
        var columnIds = new Dictionary<(string Table, string Column), string>();
        var keptColumnIds = new HashSet<string>();
        int columnsAdded = 0, columnsUpdated = 0;
        foreach (var source in schema.Tables)
        foreach (var sc in source.Columns)
        {
            var tableId = tableIds[source.Name];
            if (columnsByKey.TryGetValue((tableId, sc.Name), out var column))
            {
                if (column.DataType != sc.DataType || column.IsNullable != sc.IsNullable ||
                    column.IsPrimaryKey != sc.IsPrimaryKey || column.DefaultValue != sc.DefaultValue ||
                    column.Position != sc.Ordinal)
                {
                    column.DataType = sc.DataType;
                    column.IsNullable = sc.IsNullable;
                    column.IsPrimaryKey = sc.IsPrimaryKey;
                    column.DefaultValue = sc.DefaultValue;
                    column.Position = sc.Ordinal;
                    await repository.UpdateAsync(column.Id, column, ct);
                    columnsUpdated++;
                }
            }
            else
            {
                column = await repository.CreateAsync(new ColumnInfo
                {
                    Name = sc.Name,
                    DataType = sc.DataType,
                    IsNullable = sc.IsNullable,
                    IsPrimaryKey = sc.IsPrimaryKey,
                    DefaultValue = sc.DefaultValue,
                    Position = sc.Ordinal,
                    TableId = tableId,
                    DatabaseId = databaseId
                }, ct);
                columnsAdded++;
            }

            columnIds[(source.Name, sc.Name)] = column.Id;
            keptColumnIds.Add(column.Id);
        }

        // Relations automatiques : on les recrée, les relations saisies à la main sont conservées.
        var obsoleteColumns = existingColumns.Where(c => !keptColumnIds.Contains(c.Id)).ToList();
        var obsoleteColumnIds = obsoleteColumns.Select(c => c.Id).ToHashSet();
        var allColumnIds = keptColumnIds.Union(obsoleteColumnIds).ToHashSet();
        // Une relation détectée dont on a corrigé le type (ex : One-to-One) ou la description les garde : on les
        // reporte sur la relation recréée, avec son identifiant (l'historique la suit).
        var keptEdits = new Dictionary<(string First, string Second), RelationInfo>();
        foreach (var relation in await repository.GetRelationsOfDatabaseAsync(databaseId, ct))
        {
            var ours = allColumnIds.Contains(relation.FirstId) || allColumnIds.Contains(relation.SecondId);
            var auto = relation.MetaInfo == RelationInfo.AutoMarker;
            var dangling = obsoleteColumnIds.Contains(relation.FirstId) || obsoleteColumnIds.Contains(relation.SecondId);
            if (ours && (auto || dangling))
            {
                if (auto && !dangling) keptEdits[(relation.FirstId, relation.SecondId)] = relation;
                await repository.DeleteAsync<RelationInfo>(relation.Id, ct);
            }
        }

        var relationsCreated = 0;
        // Une clé composite (même Constraint) donne une seule relation : sa première paire, puis les suivantes à part.
        foreach (var group in schema.ForeignKeys.GroupBy(fk => fk.Constraint == null ? (object)fk : (fk.Table, fk.Constraint)))
        {
            var pairs = group.Select(fk => columnIds.TryGetValue((fk.RefTable, fk.RefColumn), out var first) &&
                                           columnIds.TryGetValue((fk.Table, fk.Column), out var second)
                    ? (First: first, Second: second)
                    : ((string First, string Second)?)null)
                .Where(p => p != null).Select(p => p!.Value).ToList();
            if (pairs.Count == 0) continue;
            var (firstId, secondId) = pairs[0];
            keptEdits.Remove((firstId, secondId), out var previous); // une seule fois : les doublons n'ont pas le même Id
            var relation = await repository.CreateAsync(new RelationInfo
            {
                Id = previous?.Id ?? "",
                FirstId = firstId,
                SecondId = secondId,
                RelationType = previous?.RelationType ?? "One-to-Many",
                Description = previous?.Description,
                MetaInfo = RelationInfo.AutoMarker
            }, ct);
            for (var i = 1; i < pairs.Count; i++)
                await repository.CreateAsync(new RelationColumnInfo
                {
                    RelationId = relation.Id, FirstId = pairs[i].First, SecondId = pairs[i].Second, Position = i
                }, ct);
            relationsCreated++;
        }

        // Index : clé (table, nom). Ceux d'une table supprimée partent avec elle (suppression en cascade).
        var existingIndexes = (await repository.FindAsync<IndexInfo>(nameof(IndexInfo.DatabaseId), databaseId, ct))
            .GroupBy(i => (i.TableId, i.Name)).ToDictionary(g => g.Key, g => g.First());
        var keptIndexIds = new HashSet<string>();
        int indexesAdded = 0, indexesRemoved = 0;
        foreach (var si in schema.Indexes)
        {
            if (!tableIds.TryGetValue(si.Table, out var indexTableId)) continue;
            var columnNames = string.Join(",", si.Columns);
            if (existingIndexes.TryGetValue((indexTableId, si.Name), out var index))
            {
                if (index.IsUnique != si.IsUnique || index.ColumnNames != columnNames)
                {
                    index.IsUnique = si.IsUnique;
                    index.ColumnNames = columnNames;
                    await repository.UpdateAsync(index.Id, index, ct);
                }
            }
            else
            {
                index = await repository.CreateAsync(new IndexInfo
                {
                    DatabaseId = databaseId, TableId = indexTableId, Name = si.Name, IsUnique = si.IsUnique,
                    ColumnNames = columnNames
                }, ct);
                indexesAdded++;
            }

            keptIndexIds.Add(index.Id);
        }

        foreach (var stale in existingIndexes.Values.Where(i => !keptIndexIds.Contains(i.Id)))
        {
            await repository.DeleteAsync<IndexInfo>(stale.Id, ct);
            indexesRemoved++;
        }

        // Suppression de ce qui a disparu de la source
        foreach (var column in obsoleteColumns)
            await repository.DeleteAsync<ColumnInfo>(column.Id, ct);
        foreach (var table in existingTables.Values.Where(t => !sourceTableNames.Contains(t.Name)))
        {
            await repository.DeleteAsync<TableInfo>(table.Id, ct);
            tablesRemoved++;
        }

        return new SyncResult(database, tablesAdded, tablesRemoved, columnsAdded, columnsUpdated,
            obsoleteColumns.Count, relationsCreated, indexesAdded, indexesRemoved);
    }
}
