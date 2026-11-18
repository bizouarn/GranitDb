using GranitDb.Interfaces;

namespace GranitDb.Client.Models;

public class ColumnInfo : IColumnInfo
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public string DatabaseId { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public string? DefaultValue { get; set; }
    public int Position { get; set; }
    public bool IsSensitive { get; set; }
}

public class RelationInfo : IRelationInfo
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public string FirstId { get; set; } = "";
    public string SecondId { get; set; } = "";
    public string RelationType { get; set; } = "";

    /// <summary>Paires suivantes d'une relation multi-colonnes (la première est FirstId/SecondId).</summary>
    public List<RelationColumnPair> ExtraColumns { get; set; } = [];

    /// <summary>Toutes les paires (référencée, qui référence), la première d'abord.</summary>
    public IEnumerable<(string First, string Second)> Pairs() =>
        new[] { (FirstId, SecondId) }.Concat(ExtraColumns.Select(p => (p.FirstId, p.SecondId)));
}

public class RelationColumnPair
{
    public string FirstId { get; set; } = "";
    public string SecondId { get; set; } = "";
}

public class IndexInfo : IIndexInfo
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public string DatabaseId { get; set; } = "";
    public string TableId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsUnique { get; set; }
    public string ColumnNames { get; set; } = "";
}

public class TableInfo
{
    public string Id { get; set; } = "";
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public string Name { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string TableType { get; set; } = "Table";
    public List<ColumnInfo> Columns { get; set; } = [];
    public List<RelationInfo> Relations { get; set; } = [];
    public List<IndexInfo> Indexes { get; set; } = [];
}

public record SyncSummary(
    int TablesAdded,
    int TablesRemoved,
    int ColumnsAdded,
    int ColumnsUpdated,
    int ColumnsRemoved,
    int RelationsCreated,
    int IndexesAdded = 0,
    int IndexesRemoved = 0);

public record ImportResponse(DatabaseInfo Database, SyncSummary Sync);

/// <summary>Ce que le serveur de l'API autorise (l'interface ne propose que cela).</summary>
public record Capabilities(bool AllowSqlServerSources, bool AllowLocalPaths);

/// <summary>Connexion à une base SQL Server à documenter (les champs que l'API attend).</summary>
public class SqlServerConnection
{
    public string Server { get; set; } = "";
    public int? Port { get; set; }
    public string Database { get; set; } = "";

    /// <summary>Sql (identifiant et mot de passe) ou Integrated (compte Windows du processus de l'API).</summary>
    public string AuthMode { get; set; } = "Sql";

    public string? User { get; set; }
    public string? Password { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public List<string>? Schemas { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public record SchemaCount(string Name, int Tables);

/// <summary>Résultat d'un test de connexion : réussi (ce qui serait documenté) ou la consigne à suivre.</summary>
public record ConnectionTestResult(bool Ok, string Message, int Tables, int Views, SchemaCount[] Schemas);

/// <summary>Résultat de la recherche globale : base, table, colonne ou index.</summary>
public class SearchHit
{
    public string Kind { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string DatabaseId { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string? TableId { get; set; }
    public string? TableName { get; set; }

    /// <summary>Page du client qui affiche ce résultat : la base, ou l'onglet de la table (la colonne y est mise en évidence).</summary>
    public string Href => string.IsNullOrEmpty(TableId)
        ? $"/databases/{Uri.EscapeDataString(DatabaseId)}"
        : $"/databases/{Uri.EscapeDataString(DatabaseId)}/tables/{Uri.EscapeDataString(TableId)}" +
          (Kind == "column" ? $"?column={Uri.EscapeDataString(Id)}" : "");
}

/// <summary>Une page de résultats avec le nombre total d'éléments.</summary>
public record Page<T>(T[] Items, int Total);

/// <summary>Un élément touché par une modification : une table, ou une colonne (et sa table).</summary>
public record ImpactNode(string TableId, string TableName, string? ColumnId, string? ColumnName, int Depth);

/// <summary>Analyse d'impact : ce qui dépend de l'élément (à modifier avec précaution) et ce dont il dépend.</summary>
public record ImpactResult(ImpactNode Root, ImpactNode[] Dependents, ImpactNode[] Dependencies);

/// <summary>Une étiquette posée sur une base, une table ou une colonne.</summary>
public record TagLinkView(string TagId, string TagName, string? Color, string EntityType, string EntityId);

public class TagInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
}

/// <summary>Une table d'un domaine et sa place mémorisée dans le diagramme du domaine.</summary>
public record DomainTable(string TableId, double? X, double? Y);

/// <summary>Domaine métier : regroupement de tables d'une base.</summary>
public class DomainInfo
{
    public string Id { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }

    /// <summary>Le diagramme montre aussi les tables voisines (d'autres domaines), en pointillés.</summary>
    public bool ShowNeighbors { get; set; }

    /// <summary>Relations que le diagramme du domaine ne dessine pas.</summary>
    public List<string> HiddenRelations { get; set; } = [];

    public List<DomainTable> Tables { get; set; } = [];
}

/// <summary>Avancement de la documentation d'une table.</summary>
public record TableCompleteness(
    string TableId,
    string TableName,
    bool TableDescribed,
    int Columns,
    int ColumnsDescribed,
    int SensitiveColumns,
    int SensitiveUndescribed,
    int Percent);

/// <summary>Avancement de la documentation d'une base ou d'un domaine.</summary>
public record CompletenessReport(
    string DatabaseId,
    int Tables,
    int TablesDescribed,
    int Columns,
    int ColumnsDescribed,
    int SensitiveColumns,
    int SensitiveUndescribed,
    int Percent,
    TableCompleteness[] PerTable);

/// <summary>Une différence entre le catalogue et la source (table, column, index ou relation ; added, removed ou changed).</summary>
public record SyncChange(string Kind, string Action, string Table, string? Name, string? Detail, bool LosesDocumentation);

/// <summary>Ce que ferait une synchronisation, sans l'appliquer.</summary>
public record SyncPreview(SyncChange[] Changes, bool HasChanges, int LosesDocumentation);

/// <summary>Une ligne du journal d'audit.</summary>
public class AuditEntry
{
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string? Actor { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public string? Controller { get; set; }
    public string? EntityId { get; set; }
    public string? Ip { get; set; }
}

/// <summary>Filtres du journal d'audit (tous facultatifs).</summary>
public record AuditFilter(string? Actor = null, string? Method = null, int? Status = null, string? Controller = null,
    string? EntityId = null);

/// <summary>Un webhook tel que l'API le montre : le secret n'y est jamais, seulement le fait qu'il existe.</summary>
public class WebhookView
{
    public string Id { get; set; } = "";
    public string? DatabaseId { get; set; }
    public string Url { get; set; } = "";
    public string[] Events { get; set; } = [];
    public bool IsActive { get; set; }
    public bool HasSecret { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class WebhookDelivery
{
    public string Id { get; set; } = "";
    public DateTime At { get; set; }
    public string Event { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

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
