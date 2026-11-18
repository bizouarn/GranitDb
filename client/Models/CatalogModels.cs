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

public partial class SqlServerConnectionInput
{
    public SqlServerConnectionInput()
    {
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

