namespace GranitDB.API.Connectors;

public record SourceColumn(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    string? DefaultValue,
    int Ordinal);

public record SourceTable(string Name, string TableType, IReadOnlyList<SourceColumn> Columns);

/// <summary>Clé étrangère : la colonne <c>Column</c> de <c>Table</c> référence <c>RefColumn</c> de <c>RefTable</c>.</summary>
public record SourceForeignKey(string Table, string Column, string RefTable, string RefColumn);

/// <summary>Index d'une table (hors clé primaire) ; <c>Columns</c> dans l'ordre de l'index.</summary>
public record SourceIndex(string Table, string Name, bool IsUnique, IReadOnlyList<string> Columns);

public record SourceSchema(
    string DatabaseName,
    string Type,
    IReadOnlyList<SourceTable> Tables,
    IReadOnlyList<SourceForeignKey> ForeignKeys,
    IReadOnlyList<SourceIndex>? Indexes = null)
{
    public IReadOnlyList<SourceIndex> Indexes { get; init; } = Indexes ?? [];
}

/// <summary>Lit le schéma d'une base source, sans jamais la modifier.</summary>
public interface ISchemaConnector
{
    /// <summary>Valeur stockée dans <c>DatabaseInfo.Type</c> (ex : Sqlite, SqlServer).</summary>
    string Type { get; }

    /// <summary>
    ///     Lit le schéma. <paramref name="source" /> est un chemin de fichier (Sqlite) ou une chaîne de connexion
    ///     (SqlServer).
    /// </summary>
    Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default);
}
