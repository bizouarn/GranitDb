namespace GranitDb.Client.Models;

// Les types d'échange avec l'API (ColumnInfo, TableInfo, DatabaseView, ImportView…) sont GÉNÉRÉS depuis le contrat OpenAPI
// (client/api/Generated/GranitApiContracts.cs, par scripts/generate-client.ps1) : on ne les écrit plus à la main. Ce fichier
// ne garde que ce que le générateur ne peut pas connaître : les valeurs par défaut des listes, les
// petits outils d'affichage, et les types propres à l'interface (page, filtre, formulaire).

// Le générateur laisse les textes obligatoires à null tant que le JSON de l'API ne les a pas remplis ; l'interface a toujours
// compté sur des textes vides plutôt que sur null (objets construits à la main, réponses incomplètes) : on garde ce comportement.

public partial class ColumnInfo
{
    public ColumnInfo()
    {
        Id = DatabaseId = TableId = Name = DataType = "";
    }
}

public partial class IndexInfo
{
    public IndexInfo()
    {
        Id = DatabaseId = TableId = Name = ColumnNames = "";
    }
}

public partial class DatabaseView
{
    public DatabaseView()
    {
        Id = Name = Type = "";
    }
}

public partial class TagInfo
{
    public TagInfo()
    {
        Id = Name = "";
    }
}

public partial class RelationInfo
{
    public RelationInfo()
    {
        Id = FirstId = SecondId = RelationType = "";
        ExtraColumns = [];
    }

    /// <summary>Marque des relations créées par la synchronisation à partir des clés étrangères de la source.</summary>
    public const string AutoMarker = "auto:fk";

    /// <summary>Clé étrangère détectée dans la source (par opposition à une relation manuelle ou déduite des noms).</summary>
    public bool IsForeignKey => MetaInfo == AutoMarker;

    /// <summary>Toutes les paires (référencée, qui référence), la première d'abord.</summary>
    public IEnumerable<(string First, string Second)> Pairs() =>
        new[] { (FirstId, SecondId) }.Concat(ExtraColumns.Select(p => (p.FirstId, p.SecondId)));
}

public partial class TableInfo
{
    public TableInfo()
    {
        Id = Name = DatabaseId = "";
        TableType = "Table";
        Columns = [];
        Relations = [];
        Indexes = [];
    }
}

public partial class DomainView
{
    public DomainView()
    {
        Id = DatabaseId = Name = "";
        HiddenRelations = [];
        Tables = [];
    }
}

public partial class WebhookView
{
    public WebhookView()
    {
        Events = [];
    }
}

/// <summary>Les moteurs client/serveur que l'API sait documenter par connexion (<c>Engine</c>).</summary>
public static class ServerEngines
{
    public const string SqlServer = "SqlServer";
    public const string PostgreSql = "PostgreSql";
    public const string MySql = "MySql";

    public static string DisplayName(string? engine) => engine switch
    {
        PostgreSql => "PostgreSQL",
        MySql => "MySQL",
        _ => "SQL Server"
    };

    public static int DefaultPort(string? engine) => engine switch
    {
        PostgreSql => 5432,
        MySql => 3306,
        _ => 1433
    };

    /// <summary>L'authentification par le compte Windows du processus de l'API n'existe que pour SQL Server.</summary>
    public static bool SupportsIntegrated(string? engine) => engine is null or SqlServer;

    /// <summary>Le moteur range les tables dans des schémas qu'on peut choisir (MySQL : la base est l'unité).</summary>
    public static bool SupportsSchemas(string? engine) => engine != MySql;

    public static string ServerPlaceholder(string? engine) => engine switch
    {
        PostgreSql => "pg.exemple.fr",
        MySql => "mysql.exemple.fr",
        _ => "srv-sql.exemple.fr ou localhost\\SQLEXPRESS"
    };

    /// <summary>Le droit minimal du compte, selon le moteur (lecture des métadonnées seulement), en HTML.</summary>
    public static string RightHint(string? engine) => engine switch
    {
        PostgreSql => "Un compte qui peut se connecter à la base et a <code>USAGE</code> sur les schémas suffit.",
        MySql => "Un compte qui peut lire la base (<code>SELECT</code>) suffit ; seules les métadonnées sont consultées.",
        _ => "Un compte qui a seulement l'autorisation <code>VIEW DEFINITION</code> suffit."
    };
}

/// <summary>Les schémas qu'une connexion testée propose, et ceux que la personne a cochés.</summary>
public sealed class SchemaChoice
{
    private readonly HashSet<string> _selected = [];

    public ConnectionTestResult? Test { get; private set; }

    public int SelectedCount => _selected.Count;

    public bool IsSelected(string name) => _selected.Contains(name);

    /// <summary>Retient le résultat d'un test (ou l'oublie : <c>null</c>) ; tous les schémas sont cochés au départ.</summary>
    public void Set(ConnectionTestResult? test)
    {
        Test = test;
        _selected.Clear();
        if (test != null)
            foreach (var schema in test.Schemas) _selected.Add(schema.Name);
    }

    public void Toggle(string name, bool on)
    {
        if (on) _selected.Add(name);
        else _selected.Remove(name);
    }

    /// <summary>Le moteur propose des schémas à choisir pour ce test.</summary>
    public bool Applies(string? engine) => Test is { Ok: true, Schemas.Count: > 0 } && ServerEngines.SupportsSchemas(engine);

    /// <summary>Les schémas cochés ; <c>null</c> (tous) quand rien n'a été restreint.</summary>
    public List<string>? Chosen(string? engine)
    {
        if (!Applies(engine)) return null;
        return _selected.Count == Test!.Schemas.Count ? null : _selected.ToList();
    }
}

public partial class ServerConnectionInput
{
    /// <summary>Les champs obligatoires sont remplis (le serveur de l'API valide le reste).</summary>
    public bool IsReady() => !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database)
                             && (AuthMode != "Sql" || (!string.IsNullOrWhiteSpace(User) && !string.IsNullOrEmpty(Password)));

    /// <summary>Changer de moteur remet le formulaire à ce que ce moteur permet (pas d'authentification Windows hors SQL Server).</summary>
    public void SetEngine(string engine)
    {
        Engine = engine;
        if (!ServerEngines.SupportsIntegrated(engine)) AuthMode = "Sql";
    }

    public ServerConnectionInput()
    {
        Engine = ServerEngines.SqlServer;
        AuthMode = "Sql";
        Encrypt = true;
        TimeoutSeconds = 15; // comme l'API (1 à 120) : 0 serait refusé
    }
}

public partial class SearchHit
{
    /// <summary>Page du client qui affiche ce résultat : la base, ou l'onglet de la table (la colonne y est mise en évidence).</summary>
    public string Href => string.IsNullOrEmpty(TableId)
        ? $"/databases/{Uri.EscapeDataString(DatabaseId)}"
        : $"/databases/{Uri.EscapeDataString(DatabaseId)}/tables/{Uri.EscapeDataString(TableId)}" +
          (Kind == "column" ? $"?column={Uri.EscapeDataString(Id)}" : "");

    public static string KindLabel(string kind) => kind switch { "database" => "Base", "table" => "Table", "column" => "Colonne", _ => "Index" };

    public static int KindOrder(string kind) => kind switch { "table" => 0, "column" => 1, "database" => 2, _ => 3 };

    /// <summary>Le nom qu'on affiche : une colonne est précédée de sa table.</summary>
    public static string Label(SearchHit hit) => hit.Kind == "column" ? $"{hit.TableName}.{hit.Name}" : hit.Name;

    /// <summary>Pourquoi ce résultat correspond : le terme est dans le nom, la description (ou, pour un index, ses colonnes).</summary>
    public string MatchReason(string? term)
    {
        var t = term?.Trim();
        if (string.IsNullOrEmpty(t) || Name.Contains(t, StringComparison.OrdinalIgnoreCase)) return "dans le nom";
        if (Description != null && Description.Contains(t, StringComparison.OrdinalIgnoreCase)) return "dans la description";
        return Kind == "index" ? "dans les colonnes de l'index" : "dans le nom";
    }
}

/// <summary>Une page de résultats avec le nombre total d'éléments.</summary>
public record Page<T>(T[] Items, int Total);

/// <summary>Filtres du journal d'audit (tous facultatifs).</summary>
public record AuditFilter(string? Actor = null, string? Method = null, int? Status = null, string? Controller = null,
    string? EntityId = null);

/// <summary>Ce que l'utilisateur saisit pour un webhook ; <c>Secret</c> absent : inchangé, vide : supprimé.</summary>
public class WebhookForm
{
    public string Url { get; set; } = "";
    public string? Secret { get; set; }
    public string? DatabaseId { get; set; }
    public List<string> Events { get; set; } = ["schema.changed"];
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
}

