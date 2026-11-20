using System.Data.Common;
using GranitDB.API.Exceptions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace GranitDB.API.Connectors;

/// <summary>
///     Profilage d'un moteur client/serveur : la mécanique est commune (ouvrir, interroger par paquets de colonnes, lire les
///     comptes, traduire l'erreur du pilote), chaque moteur ne donne que sa connexion, ses citations et ses types sans
///     <c>COUNT(DISTINCT)</c>.
/// </summary>
public abstract class ServerColumnProfiler<TException> : IColumnProfiler where TException : DbException
{
    public abstract string Type { get; }

    /// <summary>Nom du moteur dans les messages.</summary>
    protected abstract string Engine { get; }

    protected abstract DbConnection CreateConnection(string source);

    protected abstract string Quote(string name);

    /// <summary>Un nom de table tel que le connecteur le donne, cité par le moteur.</summary>
    protected abstract string TableOf(string name);

    protected virtual string CountBig => "COUNT";

    protected virtual bool DistinctAllowed(ProfiledColumn column) => true;

    /// <summary>Le message du moteur (droit de lecture refusé, table absente…) dit mieux ce qui manque qu'un texte de connexion.</summary>
    protected virtual string Describe(TException e) => Short(e.Message);

    protected static string Short(string message) => message.Length <= 300 ? message : message[..300] + "…";

    public async Task<InclusionResult> CheckInclusionAsync(string source, InclusionCheck check, int timeoutSeconds, CancellationToken ct = default)
    {
        Require(source);
        try
        {
            await using var db = CreateConnection(source);
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandTimeout = Math.Max(1, timeoutSeconds);
            cmd.CommandText = ProfileQuery.BuildInclusion(TableOf(check.ChildTable), check.ChildColumn,
                TableOf(check.ParentTable), check.ParentColumn, Quote, CountBig);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return ProfileQuery.ReadInclusion(reader);
        }
        catch (TException e)
        {
            throw new InvalidSourceException($"Vérification de {check.ChildTable}.{check.ChildColumn} impossible : {Describe(e)}", e);
        }
    }

    public async Task<TableProfileResult> ProfileAsync(string source, string table, IReadOnlyList<ProfiledColumn> columns,
        int timeoutSeconds, CancellationToken ct = default)
    {
        Require(source);
        try
        {
            await using var db = CreateConnection(source);
            await db.OpenAsync(ct);
            long rows = 0;
            var results = new List<ColumnProfileResult>();
            foreach (var batch in ProfileQuery.Batches(columns))
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandTimeout = Math.Max(1, timeoutSeconds);
                cmd.CommandText = ProfileQuery.Build(TableOf(table), batch, Quote, DistinctAllowed, CountBig);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                rows = ProfileQuery.ReadBatch(reader, batch, DistinctAllowed, results);
            }

            return new TableProfileResult(rows, results);
        }
        catch (TException e)
        {
            throw new InvalidSourceException($"Profilage de {table} impossible : {Describe(e)}", e);
        }
    }

    private void Require(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException($"Chaîne de connexion {Engine} manquante.");
    }
}

public class SqlServerColumnProfiler : ServerColumnProfiler<SqlException>
{
    public override string Type => SqlServerSchemaConnector.TypeName;
    protected override string Engine => "SQL Server";

    // COUNT(DISTINCT) n'existe pas pour ces types.
    private static readonly HashSet<string> NoDistinct = new(StringComparer.OrdinalIgnoreCase)
        { "text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid", "sql_variant" };

    private static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>Un nom de table tel que le connecteur le donne : <c>table</c> (schéma dbo) ou <c>schéma.table</c>.</summary>
    public static string TableReference(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? $"{QuoteName(name[..dot])}.{QuoteName(name[(dot + 1)..])}" : $"[dbo].{QuoteName(name)}";
    }

    protected override DbConnection CreateConnection(string source) => new SqlConnection(source);
    protected override string Quote(string name) => QuoteName(name);
    protected override string TableOf(string name) => TableReference(name);
    protected override string CountBig => "COUNT_BIG";
    protected override bool DistinctAllowed(ProfiledColumn column) => !NoDistinct.Contains(column.DataType);
}

/// <summary>Profilage PostgreSQL : même schéma de comptes que les autres moteurs, citations <c>"…"</c>, lecture seule par la connexion.</summary>
public class PostgreSqlColumnProfiler : ServerColumnProfiler<NpgsqlException>
{
    public override string Type => PostgreSqlSchemaConnector.TypeName;
    protected override string Engine => "PostgreSQL";

    // Types sans opérateur d'égalité : COUNT(DISTINCT) est refusé.
    private static readonly HashSet<string> NoDistinct = new(StringComparer.OrdinalIgnoreCase)
        { "json", "xml", "point", "line", "lseg", "box", "path", "polygon", "circle" };

    private static string QuoteName(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Un nom de table tel que le connecteur le donne : <c>table</c> (schéma public) ou <c>schéma.table</c>.</summary>
    public static string TableReference(string name)
    {
        var dot = name.IndexOf('.');
        return dot > 0 ? $"{QuoteName(name[..dot])}.{QuoteName(name[(dot + 1)..])}" : $"{QuoteName("public")}.{QuoteName(name)}";
    }

    /// <summary>« character varying(50) » → « character varying » ; « json » reste « json » (mais « jsonb » se compte).</summary>
    private static string BaseType(string dataType)
    {
        var paren = dataType.IndexOf('(');
        return (paren > 0 ? dataType[..paren] : dataType).Trim();
    }

    protected override DbConnection CreateConnection(string source) => new NpgsqlConnection(source);
    protected override string Quote(string name) => QuoteName(name);
    protected override string TableOf(string name) => TableReference(name);
    protected override bool DistinctAllowed(ProfiledColumn column) => !NoDistinct.Contains(BaseType(column.DataType));

    protected override string Describe(NpgsqlException e) => Short(e is PostgresException pg ? pg.MessageText : e.Message);
}

/// <summary>Profilage MySQL : mêmes comptes, citations <c>`…`</c>.</summary>
public class MySqlColumnProfiler : ServerColumnProfiler<MySqlException>
{
    public override string Type => MySqlSchemaConnector.TypeName;
    protected override string Engine => "MySQL";

    protected override DbConnection CreateConnection(string source) => new MySqlConnection(source);
    protected override string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    protected override string TableOf(string name) => Quote(name);
}
