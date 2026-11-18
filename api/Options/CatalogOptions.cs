namespace GranitDB.API.Options;

public class CatalogOptions
{
    public const string Section = "Catalog";

    /// <summary>Sqlite (par défaut, aucun serveur requis) ou SqlServer.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Chaîne de connexion du catalogue. Facultative en SQLite (fichier dans DataDirectory).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Dossier des données locales : catalogue SQLite et fichiers sources importés.</summary>
    public string DataDirectory { get; set; } = "granitdb-data";

    /// <summary>Taille maximale d'un fichier SQLite importé (Mo).</summary>
    public int MaxUploadMb { get; set; } = 100;

    /// <summary>Autorise de documenter un fichier SQLite par son chemin local sur le serveur.</summary>
    public bool AllowLocalPaths { get; set; }

    /// <summary>
    ///     Autorise l'enregistrement de sources SQL Server depuis l'API (connexion, import, resynchronisation). Désactivé
    ///     par défaut : l'API se connecte alors à un serveur choisi par l'appelant, ce qui ne doit être ouvert qu'à des
    ///     personnes de confiance (il n'y a pas encore d'authentification).
    /// </summary>
    public bool AllowSqlServerSources { get; set; }

    /// <summary>Dossier des clés qui chiffrent les connexions enregistrées (par défaut : <c>DataDirectory/keys</c>).</summary>
    public string? KeysDirectory { get; set; }

    public string ResolveKeysDirectory() => string.IsNullOrWhiteSpace(KeysDirectory) ? Path.Combine(DataDirectory, "keys") : KeysDirectory;

    /// <summary>Sources synchronisées au démarrage (facultatif).</summary>
    public List<StartupSource> StartupSources { get; set; } = [];

    public bool IsSqlite => string.Equals(Provider, "Sqlite", StringComparison.OrdinalIgnoreCase);
    public bool IsSqlServer => string.Equals(Provider, "SqlServer", StringComparison.OrdinalIgnoreCase);

    public string SourcesDirectory => Path.Combine(DataDirectory, "sources");

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return ConnectionString;
        if (IsSqlite)
            return $"Data Source={Path.Combine(DataDirectory, "granitdb.db")}";
        throw new InvalidOperationException(
            "Catalog:ConnectionString est obligatoire avec le fournisseur SqlServer.");
    }
}

public class StartupSource
{
    /// <summary>Identifiant du DatabaseInfo dans le catalogue.</summary>
    public string Id { get; set; } = "";

    /// <summary>Sqlite ou SqlServer.</summary>
    public string Type { get; set; } = "";

    /// <summary>Chemin du fichier (Sqlite) ou chaîne de connexion (SqlServer).</summary>
    public string Source { get; set; } = "";

    public string? Name { get; set; }
}
