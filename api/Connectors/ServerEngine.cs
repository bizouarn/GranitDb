namespace GranitDB.API.Connectors;

/// <summary>
///     Un moteur de base de données client/serveur que l'API sait documenter par connexion : SQL Server, PostgreSQL, MySQL.
///     <c>Type</c> est la valeur de <c>DatabaseInfo.Type</c> et le nom du connecteur de schéma.
/// </summary>
/// <param name="Type">Identifiant stable (<c>SqlServer</c>, <c>PostgreSql</c>, <c>MySql</c>).</param>
/// <param name="DisplayName">Nom montré à l'utilisateur.</param>
/// <param name="DefaultSchema">Schéma dont les tables gardent leur nom seul (<c>dbo</c>, <c>public</c>) ; vide si le moteur n'a pas de schémas.</param>
/// <param name="DefaultPort">Port habituel.</param>
/// <param name="SupportsSchemas">Le moteur range les tables dans des schémas qu'on peut choisir (MySQL : la base est l'unité).</param>
/// <param name="SupportsIntegrated">Authentification par le compte Windows du processus (SQL Server seulement).</param>
public record ServerEngine(string Type, string DisplayName, string DefaultSchema, int DefaultPort, bool SupportsSchemas,
    bool SupportsIntegrated)
{
    /// <summary>Schéma d'une table d'après son nom affiché : sans préfixe, celui du moteur par défaut ; sinon le préfixe de <c>schéma.table</c>.</summary>
    public string SchemaOf(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot < 0 || !SupportsSchemas ? DefaultSchema : tableName[..dot];
    }

    /// <summary>Nom affiché d'une table : sans préfixe dans le schéma par défaut, <c>schéma.table</c> ailleurs.</summary>
    public string Qualify(string schema, string table) =>
        !SupportsSchemas || string.Equals(schema, DefaultSchema, StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
}

public static class ServerEngines
{
    public static readonly ServerEngine SqlServer = new("SqlServer", "SQL Server", "dbo", 1433, true, true);
    public static readonly ServerEngine PostgreSql = new("PostgreSql", "PostgreSQL", "public", 5432, true, false);
    public static readonly ServerEngine MySql = new("MySql", "MySQL", "", 3306, false, false);

    public static IReadOnlyList<ServerEngine> All { get; } = [SqlServer, PostgreSql, MySql];

    /// <summary>Le moteur de ce type (sans tenir compte de la casse) ; <c>null</c> pour SQLite ou un type inconnu.</summary>
    public static ServerEngine? Find(string? type) =>
        All.FirstOrDefault(e => e.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    public static ServerEngine Of(string? type) => Find(type) ?? SqlServer;

    public static bool IsServer(string? type) => Find(type) != null;
}
