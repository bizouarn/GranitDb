using System.Data.Common;
using GranitDB.API.Exceptions;
using Microsoft.Data.Sqlite;

namespace GranitDB.API.Connectors;

/// <summary>
///     Lit le schéma d'un fichier SQLite via <c>sqlite_master</c> et les PRAGMA. Le fichier est ouvert en lecture
///     seule : documenter ne modifie jamais la base source.
/// </summary>
public class SqliteSchemaConnector : ISchemaConnector
{
    public const string TypeName = "Sqlite";

    public string Type => TypeName;

    public async Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidSourceException($"Fichier SQLite introuvable : {source}");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        try
        {
            await using var db = new SqliteConnection(connectionString);
            await db.OpenAsync(ct);

            var tables = new List<SourceTable>();
            var foreignKeys = new List<SourceForeignKey>();
            var objects = await ReadObjectsAsync(db, ct);
            var primaryKeys = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, type, sql) in objects)
            {
                var columns = await ReadColumnsAsync(db, name, ct);
                primaryKeys[name] = columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => c.Name)
                    .ToList();
                var tableType = type == "view" ? "View"
                    : sql != null && sql.Contains("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase)
                        ? "Virtual"
                        : "Table";
                tables.Add(new SourceTable(name, tableType, columns));
            }

            foreach (var (name, type, _) in objects)
            {
                if (type != "table") continue;
                foreignKeys.AddRange(await ReadForeignKeysAsync(db, name, primaryKeys, ct));
            }

            var indexes = new List<SourceIndex>();
            foreach (var (name, type, _) in objects)
            {
                if (type != "table") continue;
                indexes.AddRange(await ReadIndexesAsync(db, name, ct));
            }

            var dbName = Path.GetFileNameWithoutExtension(source);
            return new SourceSchema(dbName, TypeName, tables, foreignKeys, indexes);
        }
        catch (SqliteException e)
        {
            throw new InvalidSourceException($"Fichier SQLite illisible ou invalide : {e.Message}", e);
        }
    }

    /// <summary>Exécute une requête (au plus un paramètre <c>$p</c>) et convertit chaque ligne.</summary>
    private static async Task<List<T>> QueryAsync<T>(DbConnection db, string sql, string? parameter,
        Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        if (parameter != null)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "$p";
            p.Value = parameter;
            cmd.Parameters.Add(p);
        }

        var rows = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    private static Task<List<(string Name, string Type, string? Sql)>> ReadObjectsAsync(DbConnection db,
        CancellationToken ct)
    {
        return QueryAsync(db,
            "SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name",
            null, r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? (string?)null : r.GetString(2)), ct);
    }

    private static async Task<List<SourceColumn>> ReadColumnsAsync(DbConnection db, string table, CancellationToken ct)
    {
        // table_xinfo inclut les colonnes générées ; hidden = 1 désigne les colonnes cachées des tables virtuelles.
        var rows = await QueryAsync(db,
            "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($p)", table, reader =>
            {
                var declared = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var pk = reader.GetInt64(5);
                return (Hidden: reader.GetInt64(6) == 1, Column: new SourceColumn(
                    reader.GetString(1),
                    string.IsNullOrWhiteSpace(declared) ? "ANY" : declared,
                    reader.GetInt64(3) == 0 && pk == 0,
                    pk > 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    (int)reader.GetInt64(0)));
            }, ct);
        return rows.Where(r => !r.Hidden).Select(r => r.Column).ToList();
    }

    /// <summary>Index créés (<c>CREATE INDEX</c>) et contraintes UNIQUE ; l'index de la clé primaire est exclu.</summary>
    private static async Task<List<SourceIndex>> ReadIndexesAsync(DbConnection db, string table, CancellationToken ct)
    {
        // 'c' = CREATE INDEX, 'u' = UNIQUE, 'pk' = clé primaire
        var headers = (await QueryAsync(db, "SELECT name, \"unique\", origin FROM pragma_index_list($p) ORDER BY seq",
            table, r => (Name: r.GetString(0), Unique: r.GetInt64(1) == 1, Origin: r.GetString(2)), ct))
            .Where(h => h.Origin != "pk");

        var result = new List<SourceIndex>();
        foreach (var (name, unique, _) in headers)
        {
            var columns = await QueryAsync(db, "SELECT name FROM pragma_index_info($p) ORDER BY seqno", name,
                r => r.IsDBNull(0) ? "<expression>" : r.GetString(0), ct); // colonne calculée
            result.Add(new SourceIndex(table, name, unique, columns));
        }

        return result;
    }

    private static async Task<List<SourceForeignKey>> ReadForeignKeysAsync(DbConnection db, string table,
        Dictionary<string, List<string>> primaryKeys, CancellationToken ct)
    {
        var rows = await QueryAsync(db,
            "SELECT id, seq, \"table\", \"from\", \"to\" FROM pragma_foreign_key_list($p) ORDER BY id, seq", table,
            r => (Id: r.GetInt64(0), Seq: r.GetInt64(1), RefTable: r.GetString(2), From: r.GetString(3),
                To: r.IsDBNull(4) ? null : r.GetString(4)), ct);

        var result = new List<SourceForeignKey>();
        foreach (var row in rows)
        {
            var to = row.To;
            // « REFERENCES parent » sans colonne : SQLite référence la clé primaire du parent.
            if (to == null && primaryKeys.TryGetValue(row.RefTable, out var pks) && row.Seq < pks.Count)
                to = pks[(int)row.Seq];
            if (to == null) continue;
            // Une clé composite : plusieurs lignes de même id (seq 0, 1…).
            var composite = rows.Count(r => r.Id == row.Id) > 1;
            result.Add(new SourceForeignKey(table, row.From, row.RefTable, to, composite ? $"{table}#{row.Id}" : null));
        }

        return result;
    }
}
