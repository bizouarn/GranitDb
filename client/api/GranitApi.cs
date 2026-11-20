namespace GranitDb.Client.Generated;

/// <summary>
///     Point d'entrée du client typé généré depuis le contrat OpenAPI de l'API (<c>client/api/openapi.json</c> →
///     <c>Generated/GranitApiClient.cs</c>, par <c>scripts/generate-client.ps1</c>) : un client par contrôleur, avec les types
///     d'entrée et de réponse de l'API. Les fichiers générés ne se modifient pas à la main ; ajouter une route à l'API puis relancer
///     le script suffit à la retrouver ici. Le <see cref="HttpClient" /> porte l'adresse de base de l'API. L'interface l'utilise
///     à travers <c>Client</c> (<c>client/api/Client.cs</c>).
/// </summary>
public sealed class GranitApi(HttpClient http)
{
    public AiClient Ai { get; } = new(http);
    public AuditClient Audit { get; } = new(http);
    public DescriptionSuggestionsClient Suggestions { get; } = new(http);
    public AuthClient Auth { get; } = new(http);
    public CapabilitiesClient Capabilities { get; } = new(http);
    public ColumnInfoClient Columns { get; } = new(http);
    public CompletenessClient Completeness { get; } = new(http);
    public DatabaseInfoClient Databases { get; } = new(http);
    public DocumentationImportClient DocumentationImport { get; } = new(http);
    public DomainInfoClient Domains { get; } = new(http);
    public HistoryClient History { get; } = new(http);
    public ImpactClient Impact { get; } = new(http);
    public RelationInfoClient Relations { get; } = new(http);
    public SearchClient Search { get; } = new(http);
    public SqliteImportClient SqliteImport { get; } = new(http);
    public ServerImportClient ServerImport { get; } = new(http);
    public SqlServerImportClient SqlServerImport { get; } = new(http);
    public TableInfoClient Tables { get; } = new(http);
    public TagInfoClient Tags { get; } = new(http);
    public WebhooksClient Webhooks { get; } = new(http);
}
