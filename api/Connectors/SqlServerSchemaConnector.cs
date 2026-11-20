using GranitDB.API.Exceptions;
using Microsoft.Data.SqlClient;

namespace GranitDB.API.Connectors;

/// <summary>
///     Lit le schéma d'une base SQL Server via INFORMATION_SCHEMA et les vues système. Les tables du schéma
///     <c>dbo</c> gardent leur nom ; les autres sont préfixées (<c>sales.Orders</c>) pour que deux tables de même nom
///     dans deux schémas ne se confondent pas.
/// </summary>
public class SqlServerSchemaConnector : ISchemaConnector
{
    public const string TypeName = "SqlServer";

    public string Type => TypeName;

    /// <summary>Nom affiché d'une table : sans préfixe pour <c>dbo</c>, <c>schéma.table</c> sinon.</summary>
    public static string Qualify(string schema, string table)
    {
        return string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
    }

    public async Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidSourceException("Chaîne de connexion SQL Server manquante.");

        try
        {
            await using var db = new SqlConnection(source);
            await db.OpenAsync(ct);

            var primaryKeys = new HashSet<(string, string)>();
            await foreach (var r in SchemaReading.Rows(db, """
                SELECT tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                """, ct))
                primaryKeys.Add((Qualify(r.GetString(0), r.GetString(1)), r.GetString(2)));

            var tables = new Dictionary<string, (string Type, List<SourceColumn> Columns)>();
            await foreach (var r in SchemaReading.Rows(db,
                "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY TABLE_SCHEMA, TABLE_NAME", ct))
                tables[Qualify(r.GetString(0), r.GetString(1))] = (r.GetString(2) == "VIEW" ? "View" : "Table", []);

            await foreach (var r in SchemaReading.Rows(db, """
                SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, ORDINAL_POSITION
                FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
                """, ct))
            {
                var table = Qualify(r.GetString(0), r.GetString(1));
                if (!tables.TryGetValue(table, out var t)) continue;
                var name = r.GetString(2);
                // ORDINAL_POSITION commence à 1 ; on stocke une position à partir de 0, comme en SQLite.
                t.Columns.Add(new SourceColumn(name, r.GetString(3), r.GetString(4) != "NO",
                    primaryKeys.Contains((table, name)), r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetInt32(6) - 1));
            }

            var foreignKeys = new List<SourceForeignKey>();
            await foreach (var r in SchemaReading.Rows(db, """
                SELECT ps.name, pt.name, pc.name, rs.name, rt.name, rc.name, fk.name,
                    (SELECT COUNT(*) FROM sys.foreign_key_columns x WHERE x.constraint_object_id = fkc.constraint_object_id)
                FROM sys.foreign_key_columns fkc
                JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
                JOIN sys.tables pt ON pt.object_id = fkc.parent_object_id
                JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
                JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
                JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                ORDER BY fkc.constraint_object_id, fkc.constraint_column_id
                """, ct))
                foreignKeys.Add(new SourceForeignKey(Qualify(r.GetString(0), r.GetString(1)), r.GetString(2),
                    Qualify(r.GetString(3), r.GetString(4)), r.GetString(5),
                    r.GetInt32(7) > 1 ? $"{Qualify(r.GetString(0), r.GetString(1))}#{r.GetString(6)}" : null));

            // Index hors clé primaire, colonnes incluses (INCLUDE) exclues, dans l'ordre de la clé.
            var indexRows = new List<(string Table, string Name, bool Unique, string Column)>();
            await foreach (var r in SchemaReading.Rows(db, """
                SELECT s.name, t.name, i.name, i.is_unique, c.name
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.is_primary_key = 0 AND i.type > 0 AND i.is_hypothetical = 0 AND ic.is_included_column = 0
                ORDER BY s.name, t.name, i.name, ic.key_ordinal
                """, ct))
                indexRows.Add((Qualify(r.GetString(0), r.GetString(1)), r.GetString(2), r.GetBoolean(3), r.GetString(4)));

            return SchemaReading.Build(db.Database, TypeName, tables, foreignKeys, SchemaReading.GroupIndexes(indexRows));
        }
        catch (SqlException e)
        {
            throw new InvalidSourceException(SqlServerErrors.Describe(e), e);
        }
    }
}
