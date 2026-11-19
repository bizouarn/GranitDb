using GranitDB.API.Exceptions;
using MySqlConnector;

namespace GranitDB.API.Connectors;

/// <summary>
///     Lit le schéma d'une base MySQL (ou MariaDB) dans <c>information_schema</c>. Dans MySQL « schéma » et « base » sont la même
///     chose : on documente la base de la connexion, ses tables gardent leur nom. Les clés étrangères vers une autre base sont
///     ignorées. Seul le schéma est lu, jamais les données.
/// </summary>
public class MySqlSchemaConnector : ISchemaConnector
{
    public const string TypeName = "MySql";

    public string Type => TypeName;

    public async Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidSourceException("Chaîne de connexion MySQL manquante.");
        try
        {
            await using var db = new MySqlConnection(source);
            await db.OpenAsync(ct);

            string databaseName;
            await using (var cmd = new MySqlCommand("SELECT DATABASE()", db))
                databaseName = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
            if (databaseName.Length == 0) throw new InvalidSourceException("La connexion MySQL doit désigner une base (champ « Base »).");

            var tables = new Dictionary<string, (string Type, List<SourceColumn> Columns)>();
            await foreach (var r in Rows(db, """
                SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME
                """, ct))
                tables[r.GetString(0)] = (r.GetString(1).EndsWith("VIEW", StringComparison.OrdinalIgnoreCase) ? "View" : "Table", []);

            await foreach (var r in Rows(db, """
                SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, ORDINAL_POSITION, COLUMN_KEY
                FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION
                """, ct))
            {
                if (!tables.TryGetValue(r.GetString(0), out var t)) continue;
                // ORDINAL_POSITION commence à 1 ; position à partir de 0, comme pour les autres moteurs.
                t.Columns.Add(new SourceColumn(r.GetString(1), r.GetString(2), r.GetString(3) != "NO", r.GetString(6) == "PRI",
                    r.IsDBNull(4) ? null : r.GetString(4), Convert.ToInt32(r.GetValue(5)) - 1));
            }

            var foreignKeys = new List<SourceForeignKey>();
            var fkRows = new List<(string Table, string Constraint, string Column, string RefTable, string RefColumn)>();
            await foreach (var r in Rows(db, """
                SELECT TABLE_NAME, CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME
                FROM information_schema.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
                ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION
                """, ct))
                fkRows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            foreach (var key in fkRows.GroupBy(x => (x.Table, x.Constraint)))
            {
                var pairs = key.ToList();
                var constraint = pairs.Count > 1 ? $"{key.Key.Table}#{key.Key.Constraint}" : null;
                foreach (var p in pairs) foreignKeys.Add(new SourceForeignKey(p.Table, p.Column, p.RefTable, p.RefColumn, constraint));
            }

            var indexRows = new List<(string Table, string Name, bool Unique, string Column)>();
            await foreach (var r in Rows(db, """
                SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, COLUMN_NAME
                FROM information_schema.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE() AND INDEX_NAME <> 'PRIMARY'
                ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX
                """, ct))
                indexRows.Add((r.GetString(0), r.GetString(1), Convert.ToInt32(r.GetValue(2)) == 0, r.IsDBNull(3) ? "<expression>" : r.GetString(3)));
            var indexes = indexRows.GroupBy(x => (x.Table, x.Name))
                .Where(g => tables.ContainsKey(g.Key.Table))
                .Select(g => new SourceIndex(g.Key.Table, g.Key.Name, g.First().Unique, g.Select(x => x.Column).ToList())).ToList();

            return new SourceSchema(databaseName, TypeName,
                tables.Select(t => new SourceTable(t.Key, t.Value.Type, t.Value.Columns)).ToList(), foreignKeys, indexes);
        }
        catch (MySqlException e)
        {
            throw new InvalidSourceException(Describe(e), e);
        }
    }

    private static async IAsyncEnumerable<MySqlDataReader> Rows(MySqlConnection db, string sql,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader;
    }

    /// <summary>Traduit une erreur MySQL en consigne ; le détail du pilote reste dans les journaux.</summary>
    public static string Describe(MySqlException e) => (int)e.ErrorCode switch
    {
        1045 => "Identifiants refusés par le serveur : vérifiez l'identifiant, le mot de passe et l'hôte autorisé du compte.",
        1049 => "Base introuvable : vérifiez le nom de la base.",
        1044 or 1142 or 1143 or 1227 => "Droits insuffisants : le compte doit pouvoir lire la base (SELECT, ou au moins la lecture des métadonnées).",
        1040 or 1203 => "Le serveur refuse de nouvelles connexions (trop de connexions).",
        _ when e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) =>
            "Le certificat du serveur n'est pas approuvé : installez-le, ou cochez « Faire confiance au certificat » (déconseillé en production).",
        _ when e.InnerException is System.Net.Sockets.SocketException || e.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
               e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) =>
            "Serveur injoignable ou sans réponse : vérifiez le nom, le port, le pare-feu et que le serveur accepte les connexions TCP.",
        _ => $"Connexion MySQL impossible (erreur {(int)e.ErrorCode}) : consultez le journal de l'API pour le détail."
    };
}
