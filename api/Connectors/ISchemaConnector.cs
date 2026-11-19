namespace GranitDB.API.Connectors;

public record SourceColumn(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    string? DefaultValue,
    int Ordinal);

public record SourceTable(string Name, string TableType, IReadOnlyList<SourceColumn> Columns);

/// <summary>
///     Clé étrangère : la colonne <c>Column</c> de <c>Table</c> référence <c>RefColumn</c> de <c>RefTable</c>.
///     <c>Constraint</c> n'est renseigné que pour une clé composite (plusieurs colonnes) : toutes ses paires le
///     portent, dans l'ordre de la clé, et forment une seule relation. Absent pour une clé à une colonne.
/// </summary>
public record SourceForeignKey(string Table, string Column, string RefTable, string RefColumn, string? Constraint = null);

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

    /// <summary>
    ///     Les clés étrangères groupées par relation : une clé composite (même <c>Constraint</c>) forme un seul groupe, dans
    ///     l'ordre de la clé ; une clé à une colonne forme un groupe à elle seule. Règle commune à la synchronisation et à
    ///     l'aperçu.
    /// </summary>
    public IEnumerable<IGrouping<object, SourceForeignKey>> ForeignKeyGroups() =>
        ForeignKeys.GroupBy(fk => fk.Constraint == null ? (object)fk : (fk.Table, fk.Constraint));
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
