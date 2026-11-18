using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;

namespace GranitDB.API.Services;

/// <summary>Une différence entre le catalogue et la source.</summary>
/// <param name="Kind">table, column, index ou relation.</param>
/// <param name="Action">added, removed ou changed.</param>
/// <param name="Table">Nom de la table concernée.</param>
/// <param name="Name">Nom de la colonne, de l'index ou de la relation (null pour une table).</param>
/// <param name="Detail">Ce qui change (ex : « INTEGER → TEXT ») ; null s'il n'y a rien à préciser.</param>
/// <param name="LosesDocumentation">
///     Vrai si l'application supprime du travail saisi à la main : description, métadonnées, marquage sensible,
///     étiquettes ou relation manuelle.
/// </param>
public record SyncChange(string Kind, string Action, string Table, string? Name, string? Detail, bool LosesDocumentation);

/// <summary>Ce que ferait une synchronisation, sans l'appliquer.</summary>
public record SyncPreview(IReadOnlyList<SyncChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    public int LosesDocumentation => Changes.Count(c => c.LosesDocumentation);
}

/// <summary>
///     Calcule la différence entre le catalogue et le schéma lu à la source, avec les mêmes règles d'appariement que
///     <see cref="SchemaSynchronizer" /> (tables par nom, colonnes par table et nom, index par table et nom, clés
///     étrangères par colonnes).
/// </summary>
public static class SchemaDiffer
{
    public static async Task<SyncPreview> DiffAsync(ICatalogRepository repository, string databaseId, SourceSchema schema,
        CancellationToken ct = default)
    {
        var byDatabase = new[] { new CatalogFilter("DatabaseId", databaseId) };
        var tables = (await repository.GetAllAsync<TableInfo>(byDatabase, ct)).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var columns = await repository.GetAllAsync<ColumnInfo>(byDatabase, ct);
        var indexes = await repository.GetAllAsync<IndexInfo>(byDatabase, ct);
        var relations = await repository.GetRelationsOfDatabaseAsync(databaseId, ct);

        // Les éléments qui portent des étiquettes comptent comme documentés.
        var tagged = new HashSet<string>();
        var ids = tables.Values.Select(t => t.Id).Concat(columns.Select(c => c.Id)).ToList();
        foreach (var chunk in ids.Chunk(500))
            foreach (var link in await repository.GetAllAsync<TagLinkInfo>([CatalogFilter.In("EntityId", chunk)], ct))
                tagged.Add(link.EntityId);

        var tableNameById = tables.Values.ToDictionary(t => t.Id, t => t.Name);
        var columnsByTable = columns.ToLookup(c => c.TableId);
        var changes = new List<SyncChange>();
        var sourceTables = schema.Tables.ToDictionary(t => t.Name);

        bool Documented(string? description, string? metaInfo, string id, bool sensitive = false)
        {
            return !string.IsNullOrWhiteSpace(description) || !string.IsNullOrWhiteSpace(metaInfo) || sensitive ||
                   tagged.Contains(id);
        }

        // Tables et colonnes
        foreach (var source in schema.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!tables.TryGetValue(source.Name, out var table))
            {
                changes.Add(new SyncChange("table", "added", source.Name, null, $"{source.Columns.Count} colonne(s)", false));
                foreach (var sc in source.Columns)
                    changes.Add(new SyncChange("column", "added", source.Name, sc.Name, sc.DataType, false));
                continue;
            }

            if (table.TableType != source.TableType)
                changes.Add(new SyncChange("table", "changed", source.Name, null, $"{table.TableType} → {source.TableType}", false));

            var existing = columnsByTable[table.Id].GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var sc in source.Columns)
            {
                if (!existing.TryGetValue(sc.Name, out var column))
                {
                    changes.Add(new SyncChange("column", "added", source.Name, sc.Name, sc.DataType, false));
                    continue;
                }

                var detail = ColumnChanges(column, sc);
                if (detail.Count > 0)
                    changes.Add(new SyncChange("column", "changed", source.Name, sc.Name, string.Join(" ; ", detail), false));
            }

            var sourceColumns = source.Columns.Select(c => c.Name).ToHashSet();
            foreach (var gone in existing.Values.Where(c => !sourceColumns.Contains(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
                changes.Add(new SyncChange("column", "removed", source.Name, gone.Name, gone.DataType,
                    Documented(gone.Description, gone.MetaInfo, gone.Id, gone.IsSensitive)));
        }

        foreach (var table in tables.Values.Where(t => !sourceTables.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var doc = Documented(table.Description, table.MetaInfo, table.Id) ||
                      columnsByTable[table.Id].Any(c => Documented(c.Description, c.MetaInfo, c.Id, c.IsSensitive));
            changes.Add(new SyncChange("table", "removed", table.Name, null, $"{columnsByTable[table.Id].Count()} colonne(s)", doc));
        }

        // Index
        var existingIndexes = indexes.Where(i => tableNameById.ContainsKey(i.TableId))
            .GroupBy(i => (Table: tableNameById[i.TableId], i.Name)).ToDictionary(g => g.Key, g => g.First());
        var sourceIndexKeys = new HashSet<(string, string)>();
        foreach (var si in schema.Indexes.OrderBy(i => i.Table, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            sourceIndexKeys.Add((si.Table, si.Name));
            var columnNames = string.Join(",", si.Columns);
            if (!existingIndexes.TryGetValue((si.Table, si.Name), out var index))
                changes.Add(new SyncChange("index", "added", si.Table, si.Name, columnNames, false));
            else if (index.IsUnique != si.IsUnique || index.ColumnNames != columnNames)
                changes.Add(new SyncChange("index", "changed", si.Table, si.Name,
                    $"({index.ColumnNames}){(index.IsUnique ? " unique" : "")} → ({columnNames}){(si.IsUnique ? " unique" : "")}", false));
        }

        foreach (var (key, index) in existingIndexes.Where(e => sourceTables.ContainsKey(e.Key.Table) &&
                                                                !sourceIndexKeys.Contains((e.Key.Table, e.Key.Name)))
                     .OrderBy(e => e.Key.Table, StringComparer.Ordinal).ThenBy(e => e.Key.Name, StringComparer.Ordinal))
            changes.Add(new SyncChange("index", "removed", key.Table, key.Name, index.ColumnNames,
                !string.IsNullOrWhiteSpace(index.Description) || !string.IsNullOrWhiteSpace(index.MetaInfo)));

        // Relations : les clés étrangères détectées sont recréées à chaque synchronisation ; les relations manuelles
        // ne sont supprimées que si l'une de leurs colonnes disparaît.
        var columnLabel = columns.Where(c => tableNameById.ContainsKey(c.TableId))
            .ToDictionary(c => c.Id, c => (Table: tableNameById[c.TableId], Column: c.Name));
        var sourceColumnSet = schema.Tables.SelectMany(t => t.Columns.Select(c => (t.Name, c.Name))).ToHashSet();

        string Label((string Table, string Column) referenced, (string Table, string Column) referencing)
        {
            return $"{referencing.Table}.{referencing.Column} → {referenced.Table}.{referenced.Column}";
        }

        var wanted = schema.ForeignKeys
            .Where(fk => sourceColumnSet.Contains((fk.Table, fk.Column)) && sourceColumnSet.Contains((fk.RefTable, fk.RefColumn)))
            .Select(fk => ((fk.RefTable, fk.RefColumn), (fk.Table, fk.Column))).Distinct().ToList();
        var current = new HashSet<((string, string), (string, string))>();
        foreach (var relation in relations)
        {
            if (!columnLabel.TryGetValue(relation.FirstId, out var first) || !columnLabel.TryGetValue(relation.SecondId, out var second))
                continue;
            var stays = sourceColumnSet.Contains(first) && sourceColumnSet.Contains(second);
            if (relation.MetaInfo == RelationInfo.AutoMarker)
            {
                current.Add((first, second));
                if (!wanted.Contains((first, second)))
                    changes.Add(new SyncChange("relation", "removed", second.Table, Label(first, second), "clé étrangère", false));
            }
            else if (!stays)
            {
                changes.Add(new SyncChange("relation", "removed", second.Table, Label(first, second), "relation manuelle", true));
            }
        }

        foreach (var (referenced, referencing) in wanted.Where(w => !current.Contains(w))
                     .OrderBy(w => w.Item2.Item1, StringComparer.Ordinal).ThenBy(w => w.Item2.Item2, StringComparer.Ordinal))
            changes.Add(new SyncChange("relation", "added", referencing.Item1, Label(referenced, referencing), "clé étrangère", false));

        return new SyncPreview(changes);
    }

    private static List<string> ColumnChanges(ColumnInfo column, SourceColumn source)
    {
        var detail = new List<string>();
        if (column.DataType != source.DataType) detail.Add($"type {column.DataType} → {source.DataType}");
        if (column.IsNullable != source.IsNullable) detail.Add(source.IsNullable ? "devient nullable" : "devient non nullable");
        if (column.IsPrimaryKey != source.IsPrimaryKey) detail.Add(source.IsPrimaryKey ? "devient clé primaire" : "n'est plus clé primaire");
        if (column.DefaultValue != source.DefaultValue)
            detail.Add($"défaut {column.DefaultValue ?? "(aucun)"} → {source.DefaultValue ?? "(aucun)"}");
        if (column.Position != source.Ordinal) detail.Add($"position {column.Position} → {source.Ordinal}");
        return detail;
    }
}
