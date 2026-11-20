using GranitDB.API.Exceptions;
using Microsoft.Data.Sqlite;

namespace GranitDB.API.Connectors;

/// <summary>Une colonne à profiler : son nom et son type déclaré (qui dit si on peut compter ses valeurs distinctes).</summary>
public record ProfiledColumn(string Name, string DataType);

/// <summary>Ce que le profilage retient d'une colonne : des comptes, jamais une valeur. <c>DistinctCount</c> est nul si le type ne s'y prête pas.</summary>
public record ColumnProfileResult(string Column, long NullCount, long? DistinctCount);

public record TableProfileResult(long RowCount, IReadOnlyList<ColumnProfileResult> Columns);

/// <summary>Une colonne qui en référencerait une autre : table et colonne de chaque côté (noms tels que le connecteur les donne).</summary>
/// <param name="SampleSize">
///     Plus de 0 : le test ne porte que sur autant de valeurs <em>distinctes</em> de la colonne enfant (un test rapide qui écarte les
///     faux rapprochements avant la vérification complète). 0 : toutes les valeurs.
/// </param>
public record InclusionCheck(string ChildTable, string ChildColumn, string ParentTable, string ParentColumn, int SampleSize = 0);

/// <summary>
///     Résultat d'un test d'inclusion, des comptes seulement : valeurs non nulles de la colonne qui référence, celles qui n'existent
///     pas dans la colonne référencée (orphelines) et lignes de la table référencée.
/// </summary>
/// <remarks>Sur un échantillon, <c>ChildNonNull</c> et <c>Orphans</c> comptent des valeurs distinctes.</remarks>
public record InclusionResult(long ChildNonNull, long Orphans, long ParentRows);

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

    /// <summary>
    ///     Vérifie à la source (<c>NOT EXISTS</c>, lecture seule) combien de valeurs de la colonne enfant n'existent pas dans la
    ///     colonne parent : seuls des comptes remontent, jamais une valeur. Sert à confirmer une relation non déclarée.
    /// </summary>
    Task<InclusionResult> CheckInclusionAsync(string source, InclusionCheck check, int timeoutSeconds, CancellationToken ct = default);
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

    /// <summary>
    ///     Trois comptes : valeurs non nulles de la colonne enfant, celles sans équivalent dans la colonne parent, lignes du parent.
    ///     Trois sous-requêtes scalaires plutôt qu'un agrégat sur un <c>NOT EXISTS</c> (SQL Server le refuse).
    /// </summary>
    public static string BuildInclusion(string child, string childColumn, string parent, string parentColumn,
        Func<string, string> quote, string countBig, int sampleSize = 0, bool topSyntax = false)
    {
        var c = quote(childColumn);
        var p = quote(parentColumn);
        if (sampleSize > 0)
        {
            // Les n premières valeurs distinctes : le test coûte au plus n recherches dans le parent, quelle que soit la taille de la table.
            var sample = topSyntax
                ? $"SELECT DISTINCT TOP ({sampleSize}) {c} FROM {child} WHERE {c} IS NOT NULL"
                : $"SELECT DISTINCT {c} FROM {child} WHERE {c} IS NOT NULL LIMIT {sampleSize}";
            return $"SELECT (SELECT {countBig}(*) FROM ({sample}) s), " +
                   $"(SELECT {countBig}(*) FROM ({sample}) c WHERE NOT EXISTS (SELECT 1 FROM {parent} p WHERE p.{p} = c.{c})), " +
                   $"(SELECT {countBig}(*) FROM {parent})";
        }

        return $"SELECT (SELECT {countBig}(*) FROM {child} WHERE {c} IS NOT NULL), " +
               $"(SELECT {countBig}(*) FROM {child} c WHERE c.{c} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {parent} p WHERE p.{p} = c.{c})), " +
               $"(SELECT {countBig}(*) FROM {parent})";
    }

    /// <summary>
    ///     Lit la ligne de comptes d'un paquet (nombre de lignes, puis non nuls et distincts de chaque colonne) et ajoute un résultat
    ///     par colonne ; rend le nombre de lignes de la table.
    /// </summary>
    public static long ReadBatch(System.Data.Common.DbDataReader reader, IReadOnlyList<ProfiledColumn> batch,
        Func<ProfiledColumn, bool> distinctAllowed, List<ColumnProfileResult> results)
    {
        var rows = reader.GetInt64(0);
        var at = 1;
        foreach (var column in batch)
        {
            var nonNull = reader.GetInt64(at++);
            long? distinct = distinctAllowed(column) ? reader.GetInt64(at++) : null;
            results.Add(new ColumnProfileResult(column.Name, rows - nonNull, distinct));
        }

        return rows;
    }

    public static InclusionResult ReadInclusion(System.Data.Common.DbDataReader reader) =>
        new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
}

public class SqliteColumnProfiler : IColumnProfiler
{
    public string Type => SqliteSchemaConnector.TypeName;

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public async Task<InclusionResult> CheckInclusionAsync(string source, InclusionCheck check, int timeoutSeconds, CancellationToken ct = default)
    {
        var connectionString = SqliteSchemaConnector.ReadOnlyConnectionString(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await using var db = new SqliteConnection(connectionString);
            await db.OpenAsync(timeout.Token);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = ProfileQuery.BuildInclusion(Quote(check.ChildTable), check.ChildColumn, Quote(check.ParentTable),
                check.ParentColumn, Quote, "COUNT", check.SampleSize);
            await using var reader = await cmd.ExecuteReaderAsync(timeout.Token);
            await reader.ReadAsync(timeout.Token);
            return ProfileQuery.ReadInclusion(reader);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidSourceException($"La vérification de {check.ChildTable}.{check.ChildColumn} a dépassé {timeoutSeconds} s.");
        }
        catch (SqliteException e)
        {
            throw new InvalidSourceException($"Vérification de {check.ChildTable}.{check.ChildColumn} impossible : {e.Message}", e);
        }
    }

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        var connectionString = SqliteSchemaConnector.ReadOnlyConnectionString(source);

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
                rows = ProfileQuery.ReadBatch(reader, batch, _ => true, results);
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

public static class ColumnProfilers
{
    /// <summary>Le profileur du moteur <paramref name="type" />, ou une erreur qui dit pour quoi il manque (« le profilage », « la détection »).</summary>
    public static IColumnProfiler For(this IEnumerable<IColumnProfiler> profilers, string type, string purpose) =>
        profilers.FirstOrDefault(p => p.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidSourceException($"Type de base non pris en charge pour {purpose} : {type}");
}
