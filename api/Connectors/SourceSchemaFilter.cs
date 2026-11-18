namespace GranitDB.API.Connectors;

/// <summary>Restreint un schéma lu à la source aux schémas choisis (SQL Server).</summary>
public static class SourceSchemaFilter
{
    /// <summary>
    ///     Schéma d'une table SQL Server d'après son nom affiché : <c>dbo</c> pour un nom sans préfixe, sinon le préfixe de
    ///     <c>schéma.table</c> (voir <see cref="SqlServerSchemaConnector.Qualify" />).
    /// </summary>
    public static string SchemaOf(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot < 0 ? "dbo" : tableName[..dot];
    }

    /// <summary>
    ///     Garde les tables des <paramref name="schemas" /> (sans tenir compte de la casse ; tout si <c>null</c>) et ce qui ne
    ///     concerne plus que ces tables : clés étrangères dont les deux extrémités restent, index des tables gardées.
    /// </summary>
    public static SourceSchema Apply(SourceSchema schema, IReadOnlyCollection<string>? schemas)
    {
        if (schemas == null || schemas.Count == 0) return schema;
        var wanted = schemas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = schema.Tables.Where(t => wanted.Contains(SchemaOf(t.Name))).ToList();
        var names = tables.Select(t => t.Name).ToHashSet();
        return schema with
        {
            Tables = tables,
            ForeignKeys = schema.ForeignKeys.Where(fk => names.Contains(fk.Table) && names.Contains(fk.RefTable)).ToList(),
            Indexes = schema.Indexes.Where(i => names.Contains(i.Table)).ToList()
        };
    }
}
