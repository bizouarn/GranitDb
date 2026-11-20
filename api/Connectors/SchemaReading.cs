using System.Data.Common;
using System.Runtime.CompilerServices;

namespace GranitDB.API.Connectors;

/// <summary>Ce que les connecteurs client/serveur ont en commun : lecture des lignes, regroupements, phrases d'erreur.</summary>
internal static class SchemaReading
{
    public const string CertificateNotTrusted =
        "Le certificat du serveur n'est pas approuvé : installez-le, ou cochez « Faire confiance au certificat » (déconseillé en production).";

    public const string Unreachable =
        "Serveur injoignable ou sans réponse : vérifiez le nom, le port, le pare-feu et que le serveur accepte les connexions TCP.";

    public static bool MentionsCertificate(string message) =>
        message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || message.Contains("certificate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exécute la requête et rend le lecteur à chaque ligne (le même objet : lire les colonnes avant de continuer).</summary>
    public static async IAsyncEnumerable<DbDataReader> Rows(DbConnection db, string sql, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) yield return reader;
    }

    /// <summary>
    ///     Clés étrangères lues colonne par colonne (dans l'ordre de la clé) : celles d'une même contrainte forment une clé composite,
    ///     dont toutes les paires portent <c>Constraint</c> ; une clé à une colonne n'en porte pas.
    /// </summary>
    public static List<SourceForeignKey> GroupForeignKeys(IEnumerable<(string Table, string Constraint, string Column, string RefTable, string RefColumn)> rows)
    {
        var keys = new List<SourceForeignKey>();
        foreach (var key in rows.GroupBy(x => (x.Table, x.Constraint)))
        {
            var pairs = key.ToList();
            var constraint = pairs.Count > 1 ? $"{key.Key.Table}#{key.Key.Constraint}" : null;
            foreach (var p in pairs) keys.Add(new SourceForeignKey(p.Table, p.Column, p.RefTable, p.RefColumn, constraint));
        }

        return keys;
    }

    /// <summary>Index lus colonne par colonne (dans l'ordre de l'index), regroupés par table et par nom.</summary>
    public static List<SourceIndex> GroupIndexes(IEnumerable<(string Table, string Name, bool Unique, string Column)> rows) =>
        rows.GroupBy(x => (x.Table, x.Name))
            .Select(g => new SourceIndex(g.Key.Table, g.Key.Name, g.First().Unique, g.Select(x => x.Column).ToList())).ToList();

    public static SourceSchema Build(string databaseName, string type,
        Dictionary<string, (string Type, List<SourceColumn> Columns)> tables, List<SourceForeignKey> foreignKeys, List<SourceIndex> indexes) =>
        new(databaseName, type, tables.Select(t => new SourceTable(t.Key, t.Value.Type, t.Value.Columns)).ToList(), foreignKeys, indexes);
}
