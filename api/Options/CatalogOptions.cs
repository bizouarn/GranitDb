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

    /// <summary>
    ///     Autorise le profilage des colonnes (nombre de lignes, de nuls et de valeurs distinctes, calculés par la source en lecture
    ///     seule ; aucune valeur n'est lue ni stockée). Désactivé par défaut : c'est la seule fonction qui lit des données de la
    ///     source et pas seulement son schéma ; SQL Server demande alors le droit de lecture des tables (db_datareader).
    /// </summary>
    public bool AllowProfiling { get; set; }

    /// <summary>Durée maximale du profilage d'une table, en secondes.</summary>
    public int ProfilingTimeoutSeconds { get; set; } = 60;

    /// <summary>Tables profilées au plus par demande de profilage d'une base (les plus anciennement profilées d'abord).</summary>
    public int ProfilingMaxTables { get; set; } = 100;

    /// <summary>Dossier des clés qui chiffrent les connexions enregistrées (par défaut : <c>DataDirectory/keys</c>).</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>
    ///     Chiffrement des clés au repos : <c>None</c> (défaut : fichiers en clair, à protéger par les droits du dossier),
    ///     <c>Dpapi</c> (Windows : lié au compte ou à la machine) ou <c>Certificate</c> (<see cref="KeysCertificatePath" />).
    ///     Les clés déjà créées restent lisibles ; seules les nouvelles sont chiffrées.
    /// </summary>
    public string KeysProtection { get; set; } = "None";

    /// <summary>Certificat (.pfx) qui chiffre les clés avec <c>KeysProtection = Certificate</c>.</summary>
    public string? KeysCertificatePath { get; set; }

    /// <summary>Mot de passe du certificat : <c>env:NOM</c> ou <c>file:/chemin</c> pour le garder hors du fichier de configuration.</summary>
    public string? KeysCertificatePassword { get; set; }

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

    /// <summary>
    ///     Chemin du fichier (Sqlite) ou chaîne de connexion (SqlServer). Une chaîne de connexion peut être une référence :
    ///     <c>env:NOM</c> (variable d'environnement) ou <c>file:/chemin</c> (secret Docker, Kubernetes…) pour ne pas laisser
    ///     le mot de passe en clair dans la configuration.
    /// </summary>
    public string Source { get; set; } = "";

    public string? Name { get; set; }
}
