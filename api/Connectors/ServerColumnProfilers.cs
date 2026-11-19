using GranitDB.API.Exceptions;
using MySqlConnector;
using Npgsql;

namespace GranitDB.API.Connectors;

/// <summary>Profilage PostgreSQL : même schéma de comptes que les autres moteurs, citations <c>"…"</c>, lecture seule par la connexion.</summary>
public class PostgreSqlColumnProfiler : IColumnProfiler
{
    public string Type => PostgreSqlSchemaConnector.TypeName;

    // Types sans opérateur d'égalité : COUNT(DISTINCT) est refusé.
    private static readonly HashSet<string> NoDistinct = new(StringComparer.OrdinalIgnoreCase)
        { "json", "xml", "point", "line", "lseg", "box", "path", "polygon", "circle" };

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Un nom de table tel que le connecteur le donne : <c>table</c> (schéma public) ou <c>schéma.table</c>.</summary>
    public static string TableReference(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? $"{Quote(name[..dot])}.{Quote(name[(dot + 1)..])}" : $"{Quote("public")}.{Quote(name)}";
    }

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException("Chaîne de connexion PostgreSQL manquante.");
        try
        {
            await using var db = new NpgsqlConnection(source);
            await db.OpenAsync(ct);
            long rows = 0;
            var results = new List<ColumnProfileResult>();
            foreach (var batch in ProfileQuery.Batches(columns))
            {
                await using var cmd = new NpgsqlCommand(
                    ProfileQuery.Build(TableReference(table), batch, Quote, c => !NoDistinct.Contains(BaseType(c.DataType)), "COUNT"), db)
                { CommandTimeout = Math.Max(1, timeoutSeconds) };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                rows = reader.GetInt64(0);
                var at = 1;
                foreach (var column in batch)
                {
                    var nonNull = reader.GetInt64(at++);
                    long? distinct = NoDistinct.Contains(BaseType(column.DataType)) ? null : reader.GetInt64(at++);
                    results.Add(new ColumnProfileResult(column.Name, rows - nonNull, distinct));
                }
            }

            return new TableProfileResult(rows, results);
        }
        catch (NpgsqlException e)
        {
            throw new InvalidSourceException($"Profilage de {table} impossible : {Short(e is PostgresException pg ? pg.MessageText : e.Message)}", e);
        }
    }

    /// <summary>« character varying(50) » → « character varying » ; « json » reste « json » (mais « jsonb » se compte).</summary>
    private static string BaseType(string dataType)
    {
        var paren = dataType.IndexOf('(');
        return (paren > 0 ? dataType[..paren] : dataType).Trim();
    }

    private static string Short(string message) => message.Length <= 300 ? message : message[..300] + "…";
}

/// <summary>Profilage MySQL : mêmes comptes, citations <c>`…`</c>.</summary>
public class MySqlColumnProfiler : IColumnProfiler
{
    public string Type => MySqlSchemaConnector.TypeName;

    private static string Quote(string name) => "`" + name.Replace("`", "``") + "`";

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException("Chaîne de connexion MySQL manquante.");
        try
        {
            await using var db = new MySqlConnection(source);
            await db.OpenAsync(ct);
            long rows = 0;
            var results = new List<ColumnProfileResult>();
            foreach (var batch in ProfileQuery.Batches(columns))
            {
                await using var cmd = new MySqlCommand(ProfileQuery.Build(Quote(table), batch, Quote, _ => true, "COUNT"), db)
                { CommandTimeout = Math.Max(1, timeoutSeconds) };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                rows = reader.GetInt64(0);
                var at = 1;
                foreach (var column in batch)
                {
                    var nonNull = reader.GetInt64(at++);
                    results.Add(new ColumnProfileResult(column.Name, rows - nonNull, reader.GetInt64(at++)));
                }
            }

            return new TableProfileResult(rows, results);
        }
        catch (MySqlException e)
        {
            throw new InvalidSourceException($"Profilage de {table} impossible : {(e.Message.Length <= 300 ? e.Message : e.Message[..300] + "…")}", e);
        }
    }
}
