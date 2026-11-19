using GranitDB.API.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace GranitDB.API.Connectors;

/// <summary>Une colonne à profiler : son nom et son type déclaré (qui dit si on peut compter ses valeurs distinctes).</summary>
public record ProfiledColumn(string Name, string DataType);

/// <summary>Ce que le profilage retient d'une colonne : des comptes, jamais une valeur. <c>DistinctCount</c> est nul si le type ne s'y prête pas.</summary>
public record ColumnProfileResult(string Column, long NullCount, long? DistinctCount);

public record TableProfileResult(long RowCount, IReadOnlyList<ColumnProfileResult> Columns);

/// <summary>
///     Profilage léger d'une table de la source : nombre de lignes, de valeurs nulles et de valeurs distinctes par colonne,
///     calculés par la base elle-même (<c>COUNT</c>, <c>COUNT(DISTINCT …)</c>) et en lecture seule. Aucune valeur ne quitte la
///     source : seuls des comptes remontent. C'est la seule fonction du produit qui lit des données et pas seulement le schéma.
/// </summary>
public interface IColumnProfiler
{
    string Type { get; }

    Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default);
}

/// <summary>Construit les requêtes de comptage : le même schéma pour les deux moteurs, avec les identifiants cités par le moteur.</summary>
internal static class ProfileQuery
{
    /// <summary>Colonnes par requête : au-delà, plusieurs passes (une requête a un nombre d'expressions limité).</summary>
    public const int ColumnsPerQuery = 60;

    public static IEnumerable<IReadOnlyList<ProfiledColumn>> Batches(IReadOnlyList<ProfiledColumn> columns) =>
        columns.Chunk(ColumnsPerQuery).Select(c => (IReadOnlyList<ProfiledColumn>)c);

    /// <summary>
    ///     <c>SELECT COUNT(*), COUNT(c1), COUNT(DISTINCT c1)…</c> : le nombre de nuls se déduit (lignes − non nuls). Les colonnes dont
    ///     <paramref name="distinctAllowed" /> est faux n'ont pas de <c>COUNT(DISTINCT)</c>.
    /// </summary>
    public static string Build(string table, IReadOnlyList<ProfiledColumn> columns, Func<string, string> quote,
        Func<ProfiledColumn, bool> distinctAllowed, string countBig)
    {
        var parts = new List<string> { $"{countBig}(*)" };
        foreach (var column in columns)
        {
            // Une colonne dont le type ne se compte pas (text, ntext, image…) se teste par « IS NULL » : COUNT(colonne) y est refusé.
            parts.Add(distinctAllowed(column)
                ? $"{countBig}({quote(column.Name)})"
                : $"{countBig}(CASE WHEN {quote(column.Name)} IS NULL THEN NULL ELSE 1 END)");
            if (distinctAllowed(column)) parts.Add($"{countBig}(DISTINCT {quote(column.Name)})");
        }

        return $"SELECT {string.Join(", ", parts)} FROM {table}";
    }
}

public class SqliteColumnProfiler : IColumnProfiler
{
    public string Type => SqliteSchemaConnector.TypeName;

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidSourceException($"Fichier SQLite introuvable : {source}");
        var connectionString = new SqliteConnectionStringBuilder
            { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await using var db = new SqliteConnection(connectionString);
            await db.OpenAsync(timeout.Token);
            long rows = 0;
            var results = new List<ColumnProfileResult>();
            foreach (var batch in ProfileQuery.Batches(columns))
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText = ProfileQuery.Build(Quote(table), batch, Quote, _ => true, "COUNT");
                await using var reader = await cmd.ExecuteReaderAsync(timeout.Token);
                await reader.ReadAsync(timeout.Token);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidSourceException($"Le profilage de {table} a dépassé {timeoutSeconds} s.");
        }
        catch (SqliteException e)
        {
            throw new InvalidSourceException($"Profilage de {table} impossible : {e.Message}", e);
        }
    }
}

public class SqlServerColumnProfiler : IColumnProfiler
{
    public string Type => SqlServerSchemaConnector.TypeName;

    // COUNT(DISTINCT) n'existe pas pour ces types.
    private static readonly HashSet<string> NoDistinct = new(StringComparer.OrdinalIgnoreCase)
        { "text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid", "sql_variant" };

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Short(string message) => message.Length <= 300 ? message : message[..300] + "…";

    /// <summary>Un nom de table tel que le connecteur le donne : <c>table</c> (schéma dbo) ou <c>schéma.table</c>.</summary>
    public static string TableReference(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? $"{Quote(name[..dot])}.{Quote(name[(dot + 1)..])}" : $"[dbo].{Quote(name)}";
    }

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException("Chaîne de connexion SQL Server manquante.");
        try
        {
            await using var db = new SqlConnection(source);
            await db.OpenAsync(ct);
            long rows = 0;
            var results = new List<ColumnProfileResult>();
            foreach (var batch in ProfileQuery.Batches(columns))
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandTimeout = Math.Max(1, timeoutSeconds);
                cmd.CommandText = ProfileQuery.Build(TableReference(table), batch, Quote, c => !NoDistinct.Contains(c.DataType), "COUNT_BIG");
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                rows = reader.GetInt64(0);
                var at = 1;
                foreach (var column in batch)
                {
                    var nonNull = reader.GetInt64(at++);
                    long? distinct = NoDistinct.Contains(column.DataType) ? null : reader.GetInt64(at++);
                    results.Add(new ColumnProfileResult(column.Name, rows - nonNull, distinct));
                }
            }

            return new TableProfileResult(rows, results);
        }
        catch (SqlException e)
        {
            // Le message du moteur (droit de lecture refusé, table absente…) dit mieux ce qui manque qu'un texte de connexion.
            throw new InvalidSourceException($"Profilage de {table} impossible : {Short(e.Message)}", e);
        }
    }
}
