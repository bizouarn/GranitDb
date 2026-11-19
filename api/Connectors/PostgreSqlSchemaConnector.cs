using GranitDB.API.Exceptions;
using Npgsql;

namespace GranitDB.API.Connectors;

/// <summary>
///     Lit le schéma d'une base PostgreSQL dans le catalogue système (<c>pg_class</c>, <c>pg_attribute</c>, <c>pg_constraint</c>,
///     <c>pg_index</c>) : les tables du schéma <c>public</c> gardent leur nom, les autres sont préfixées (<c>sales.orders</c>).
///     Lecture seule (<c>default_transaction_read_only</c>) ; seul le schéma est lu, jamais les données.
/// </summary>
public class PostgreSqlSchemaConnector : ISchemaConnector
{
    public const string TypeName = "PostgreSql";

    public string Type => TypeName;

    private const string UserSchemas =
        "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\\_toast%' AND n.nspname NOT LIKE 'pg\\_temp%'";

    private static string Qualify(string schema, string table) => ServerEngines.PostgreSql.Qualify(schema, table);

    public async Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException("Chaîne de connexion PostgreSQL manquante.");
        try
        {
            await using var db = new NpgsqlConnection(source);
            await db.OpenAsync(ct);

            string databaseName;
            await using (var cmd = new NpgsqlCommand("SELECT current_database()", db))
                databaseName = (string)(await cmd.ExecuteScalarAsync(ct))!;

            var primaryKeys = new HashSet<(string, string)>();
            await foreach (var r in Rows(db, $"""
                SELECT n.nspname, c.relname, a.attname
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
                WHERE con.contype = 'p' AND {UserSchemas}
                """, ct))
                primaryKeys.Add((Qualify(r.GetString(0), r.GetString(1)), r.GetString(2)));

            var tables = new Dictionary<string, (string Type, List<SourceColumn> Columns)>();
            await foreach (var r in Rows(db, $"""
                SELECT n.nspname, c.relname, c.relkind::text
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm') AND {UserSchemas}
                ORDER BY n.nspname, c.relname
                """, ct))
                tables[Qualify(r.GetString(0), r.GetString(1))] = (r.GetString(2) is "v" or "m" ? "View" : "Table", []);

            await foreach (var r in Rows(db, $"""
                SELECT n.nspname, c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                       pg_get_expr(d.adbin, d.adrelid)
                FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'f', 'v', 'm') AND {UserSchemas}
                ORDER BY n.nspname, c.relname, a.attnum
                """, ct))
            {
                var table = Qualify(r.GetString(0), r.GetString(1));
                if (!tables.TryGetValue(table, out var t)) continue;
                var name = r.GetString(2);
                // attnum garde des trous quand une colonne a été supprimée : la position est l'ordre de lecture, à partir de 0.
                t.Columns.Add(new SourceColumn(name, r.GetString(3), r.GetBoolean(4), primaryKeys.Contains((table, name)),
                    r.IsDBNull(5) ? null : r.GetString(5), t.Columns.Count));
            }

            var foreignKeys = new List<SourceForeignKey>();
            var rows = new List<(string Key, string Table, string Column, string RefTable, string RefColumn)>();
            await foreach (var r in Rows(db, $"""
                SELECT con.oid::text, n.nspname, c.relname, a.attname, rn.nspname, rc.relname, ra.attname
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                JOIN pg_class rc ON rc.oid = con.confrelid
                JOIN pg_namespace rn ON rn.oid = rc.relnamespace
                CROSS JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(attnum, fattnum, ord)
                JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
                JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = k.fattnum
                WHERE con.contype = 'f' AND {UserSchemas}
                ORDER BY con.oid, k.ord
                """, ct))
                rows.Add((r.GetString(0), Qualify(r.GetString(1), r.GetString(2)), r.GetString(3),
                    Qualify(r.GetString(4), r.GetString(5)), r.GetString(6)));
            foreach (var key in rows.GroupBy(x => x.Key))
            {
                var pairs = key.ToList();
                // Une clé composite : toutes ses paires portent la même contrainte, dans l'ordre de la clé.
                var constraint = pairs.Count > 1 ? $"{pairs[0].Table}#{key.Key}" : null;
                foreach (var p in pairs) foreignKeys.Add(new SourceForeignKey(p.Table, p.Column, p.RefTable, p.RefColumn, constraint));
            }

            var indexes = new List<SourceIndex>();
            var current = new List<(string Table, string Name, bool Unique, string Column)>();
            await foreach (var r in Rows(db, $"""
                SELECT n.nspname, tc.relname, ic.relname, i.indisunique, CASE WHEN k.attnum = 0 THEN NULL ELSE a.attname END
                FROM pg_index i
                JOIN pg_class ic ON ic.oid = i.indexrelid
                JOIN pg_class tc ON tc.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = tc.relnamespace
                CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                LEFT JOIN pg_attribute a ON a.attrelid = tc.oid AND a.attnum = k.attnum
                WHERE NOT i.indisprimary AND k.ord <= i.indnkeyatts AND tc.relkind IN ('r', 'p', 'm') AND {UserSchemas}
                ORDER BY n.nspname, tc.relname, ic.relname, k.ord
                """, ct))
                current.Add((Qualify(r.GetString(0), r.GetString(1)), r.GetString(2), r.GetBoolean(3),
                    r.IsDBNull(4) ? "<expression>" : r.GetString(4)));
            foreach (var index in current.GroupBy(x => (x.Table, x.Name)))
                indexes.Add(new SourceIndex(index.Key.Table, index.Key.Name, index.First().Unique, index.Select(x => x.Column).ToList()));

            return new SourceSchema(databaseName, TypeName,
                tables.Select(t => new SourceTable(t.Key, t.Value.Type, t.Value.Columns)).ToList(), foreignKeys, indexes);
        }
        catch (NpgsqlException e)
        {
            throw new InvalidSourceException(Describe(e), e);
        }
    }

    private static async IAsyncEnumerable<NpgsqlDataReader> Rows(NpgsqlConnection db, string sql,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader;
    }

    /// <summary>Traduit une erreur de connexion ou de lecture PostgreSQL en consigne ; le détail du pilote reste dans les journaux.</summary>
    public static string Describe(NpgsqlException e)
    {
        if (e is PostgresException pg)
            return pg.SqlState switch
            {
                "28P01" or "28000" => "Identifiants refusés par le serveur : vérifiez l'identifiant, le mot de passe et le fichier pg_hba.conf.",
                "3D000" => "Base introuvable : vérifiez le nom de la base.",
                "42501" => "Droits insuffisants : le compte doit pouvoir se connecter à la base et lire le catalogue (USAGE sur les schémas).",
                "57P03" or "53300" => "Le serveur n'accepte pas de connexion pour le moment (démarrage ou trop de connexions).",
                _ => $"Lecture PostgreSQL impossible (code {pg.SqlState}) : consultez le journal de l'API pour le détail."
            };
        if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return "Le certificat du serveur n'est pas approuvé : installez-le, ou cochez « Faire confiance au certificat » (déconseillé en production).";
        if (e.InnerException is System.Net.Sockets.SocketException || e.Message.Contains("connect", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Serveur injoignable ou sans réponse : vérifiez le nom, le port, le pare-feu et que le serveur accepte les connexions TCP.";
        return "Connexion PostgreSQL impossible : consultez le journal de l'API pour le détail.";
    }
}
