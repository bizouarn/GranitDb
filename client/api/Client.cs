using System.Net.Http.Json;
using System.Text.Json;
using GranitDb.Client.Generated;
using GranitDb.Client.Models;

namespace GranitDb.Client.api;

/// <summary>
///     Client de l'API GranitDb pour l'interface. Les appels passent par le client typé généré depuis le contrat OpenAPI
///     (<see cref="GranitApi" />, <c>client/api/Generated</c>) : routes, paramètres, corps et types de réponse viennent du
///     contrat, pas d'un texte écrit ici. Cette classe ne garde que la politique de l'interface : quelles erreurs sont des
///     messages à afficher (<see cref="ApiException" />), quelles lectures peuvent se replier sur une valeur vide, et les
///     téléchargements de fichiers, que le contrat ne décrit pas comme des données.
/// </summary>
public class Client(HttpClient http)
{
    private readonly GranitApi _api = new(http);

    // --- Bases ---

    /// <summary>Les bases ; une réponse en erreur est une <see cref="HttpRequestException" /> (les pages gèrent l'API injoignable).</summary>
    public async Task<DatabaseView[]> GetAllDatabaseAsync() =>
        (await Strict(() => _api.Databases.GetAllAsync())).Result.ToArray();

    /// <summary>Une page de bases (triées par nom) et le total, lu dans l'en-tête <c>X-Total-Count</c>.</summary>
    public async Task<Page<DatabaseView>> GetDatabasesPageAsync(int skip, int take)
    {
        var response = await Strict(() => _api.Databases.GetAllAsync(skip, take));
        var items = response.Result.ToArray();
        return new Page<DatabaseView>(items, TotalOf(response.Headers, skip + items.Length));
    }

    /// <summary>Recherche globale ; lève <see cref="ApiException" /> si le terme est refusé par l'API.</summary>
    public async Task<SearchHit[]> SearchAsync(string term) =>
        (await CallAsync(() => _api.Search.SearchAsync(term))).ToArray();

    public Task<DatabaseView?> GetDatabaseAsync(string id) => OptionalAsync<DatabaseView?>(() => _api.Databases.GetAsync(id), null);

    /// <summary>Tables d'une base, avec colonnes et relations. Liste vide si la base n'a pas de table.</summary>
    public async Task<TableInfo[]> GetTablesAsync(string databaseId) =>
        (await OptionalAsync(() => _api.Tables.GetTablesByDatabaseIdAsync(databaseId), []))!.ToArray();

    /// <summary>Tables d'une base sans colonnes ni relations (léger) : les colonnes se lisent à l'ouverture d'une table.</summary>
    public async Task<TableInfo[]> GetTableSummariesAsync(string databaseId) =>
        (await OptionalAsync(() => _api.Tables.GetTablesByDatabaseIdAsync(databaseId, summary: true), []))!.ToArray();

    /// <summary>Colonnes d'une table, dans l'ordre de la table.</summary>
    public async Task<ColumnInfo[]> GetColumnsAsync(string tableId) =>
        (await OptionalAsync(() => _api.Tables.GetColumnsAsync(tableId), []))!.ToArray();

    /// <summary>
    ///     Analyse d'impact d'une table (<paramref name="kind" /> = <c>table</c>) ou d'une colonne (<c>column</c>) ;
    ///     <c>null</c> si l'élément n'existe pas.
    /// </summary>
    public Task<ImpactResult?> GetImpactAsync(string kind, string id) =>
        OptionalAsync<ImpactResult?>(() => kind == "column" ? _api.Impact.OfColumnAsync(id) : _api.Impact.OfTableAsync(id), null);

    // --- Import et synchronisation ---

    /// <summary>Envoie un fichier SQLite ; lève <see cref="ApiException" /> avec le message du serveur si refusé.</summary>
    public Task<ImportView> UploadSqliteAsync(Stream file, string fileName, string? name, string? description) =>
        // Le client généré exige les champs du formulaire : vides, le serveur les traite comme absents.
        CallAsync(() => _api.SqliteImport.UploadAsync(new FileParameter(file, fileName, "application/octet-stream"),
            name?.Trim() ?? "", description ?? "", true));

    /// <summary>Importe la base d'exemple livrée avec l'API.</summary>
    public Task<ImportView> ImportSampleAsync() => CallAsync(() => _api.SqliteImport.SampleAsync());

    /// <summary>Règle la synchronisation automatique d'une base (minutes ; <c>null</c> ou 0 : désactivée) ; lève <see cref="ApiException" /> si elle est refusée.</summary>
    public Task<DatabaseView> SetSyncScheduleAsync(string databaseId, int? minutes) =>
        CallAsync(() => _api.Databases.ScheduleAsync(databaseId, new ScheduleInput { IntervalMinutes = minutes ?? 0 }));

    // --- Profilage des colonnes ---

    /// <summary>Profil enregistré d'une table (vide tant qu'elle n'a pas été profilée) ; <c>null</c> si la table n'existe pas.</summary>
    public Task<TableProfileView?> GetTableProfileAsync(string tableId) =>
        OptionalAsync<TableProfileView?>(() => _api.Tables.GetProfileAsync(tableId), null);

    /// <summary>Profile une table à la source ; lève <see cref="ApiException" /> avec la raison si c'est refusé ou impossible.</summary>
    public Task<TableProfileView> ProfileTableAsync(string tableId) => CallAsync(() => _api.Tables.ProfileAsync(tableId));

    /// <summary>Profile les tables d'une base (par lots) et dit combien l'ont été.</summary>
    public Task<ProfileRunView> ProfileDatabaseAsync(string databaseId) => CallAsync(() => _api.Databases.ProfileAsync(databaseId));

    // --- Assistant d'IA ---

    public Task<AiStatusView> GetAiStatusAsync() => CallAsync(() => _api.Ai.StatusAsync());

    /// <summary>Un appel minimal au modèle ; lève <see cref="ApiException" /> avec la raison (désactivé, injoignable, format…).</summary>
    public Task<AiTestView> TestAiAsync() => CallAsync(() => _api.Ai.TestAsync());

    // --- Assistant de documentation ---

    public Task<List<SuggestionView>> GetSuggestionsAsync(string databaseId, string? status = null) =>
        CallAsync(() => _api.Suggestions.ListAsync(databaseId, status));

    /// <summary>Lance la documentation en lot (arrière-plan) ; lève <see cref="ApiException" /> si l'IA est désactivée ou déjà en cours.</summary>
    public Task<SuggestionJobView> GenerateSuggestionsAsync(string databaseId) => CallAsync(() => _api.Suggestions.GenerateAsync(databaseId));

    /// <summary>Avancement du dernier lot ; <c>null</c> si aucun n'a été lancé.</summary>
    public Task<SuggestionJobView?> GetSuggestionJobAsync(string databaseId) =>
        OptionalAsync<SuggestionJobView?>(() => _api.Suggestions.JobAsync(databaseId), null);

    public Task<SuggestionJobView> CancelSuggestionJobAsync(string databaseId) => CallAsync(() => _api.Suggestions.CancelJobAsync(databaseId));

    /// <summary>Propose la description d'un seul élément (table, column, relation ou database).</summary>
    public Task<SuggestionView> SuggestAsync(string databaseId, string targetType, string? targetId) =>
        CallAsync(() => _api.Suggestions.SuggestAsync(databaseId, new SuggestInput { TargetType = targetType, TargetId = targetId }));

    public Task<AcceptBatchView> DetectSensitiveAsync(string databaseId) => CallAsync(() => _api.Suggestions.DetectSensitiveAsync(databaseId));

    public Task<SuggestionView> AcceptSuggestionAsync(string databaseId, string id, string? text) =>
        CallAsync(() => _api.Suggestions.AcceptAsync(databaseId, id, new AcceptSuggestionInput { Text = text }));

    public Task RejectSuggestionAsync(string databaseId, string id) => CallAsync(() => _api.Suggestions.RejectAsync(databaseId, id));

    public Task<AcceptBatchView> AcceptConfidentSuggestionsAsync(string databaseId) => CallAsync(() => _api.Suggestions.AcceptConfidentAsync(databaseId));

    // --- Relations suggérées (détection) ---

    /// <summary>Les suggestions d'une base (en attente par défaut, les mieux notées d'abord).</summary>
    public Task<List<RelationSuggestionView>> GetRelationSuggestionsAsync(string databaseId, string? status = null) =>
        CallAsync(() => _api.Databases.RelationSuggestionsAsync(databaseId, status));

    /// <summary>Lance la détection (vérification par comptes à la source) ; lève <see cref="ApiException" /> avec la raison si elle est refusée.</summary>
    public Task<DetectionRunView> DetectRelationsAsync(string databaseId) => CallAsync(() => _api.Databases.DetectRelationsAsync(databaseId));

    public Task<RelationInfo> AcceptRelationSuggestionAsync(string databaseId, string suggestionId) =>
        CallAsync(() => _api.Databases.AcceptRelationSuggestionAsync(databaseId, suggestionId));

    public Task<RelationSuggestionView> RejectRelationSuggestionAsync(string databaseId, string suggestionId) =>
        CallAsync(() => _api.Databases.RejectRelationSuggestionAsync(databaseId, suggestionId));

    public Task<AcceptBatchView> AcceptRelationSuggestionsAsync(string databaseId, int minScore) =>
        CallAsync(() => _api.Databases.AcceptRelationSuggestionsAsync(databaseId, minScore));

    /// <summary>Aperçu du remplacement du fichier d'une base SQLite : rien n'est modifié.</summary>
    public Task<SyncPreview> PreviewReplaceSqliteAsync(string id, byte[] file, string fileName) =>
        CallAsync(() => _api.SqliteImport.PreviewReplaceAsync(id, new FileParameter(new MemoryStream(file), fileName, "application/octet-stream")));

    /// <summary>Remplace le fichier d'une base SQLite et la resynchronise (descriptions conservées).</summary>
    public Task<ImportView> ReplaceSqliteAsync(string id, byte[] file, string fileName) =>
        CallAsync(() => _api.SqliteImport.ReplaceAsync(id, new FileParameter(new MemoryStream(file), fileName, "application/octet-stream"), true));

    public Task<SyncSummaryView> SyncAsync(string id) => CallAsync(() => _api.Databases.SyncAsync(id));

    /// <summary>Ce que ferait la synchronisation, sans l'appliquer ; lève <see cref="ApiException" /> si impossible.</summary>
    public Task<SyncPreview> PreviewSyncAsync(string id) => CallAsync(() => _api.Databases.PreviewSyncAsync(id));

    // --- Sources SQL Server ---

    /// <summary>Capacités activées sur le serveur ; tout est refusé si elles sont illisibles.</summary>
    public async Task<CapabilitiesView> GetCapabilitiesAsync()
    {
        try
        {
            return await OptionalAsync(() => _api.Capabilities.GetAsync(), Refused) ?? Refused;
        }
        catch (HttpRequestException)
        {
            return Refused; // les pages gèrent elles-mêmes l'API injoignable
        }
    }

    private static CapabilitiesView Refused => new() { AllowSqlServerSources = false, AllowLocalPaths = false };

    /// <summary>
    ///     Essaie une connexion à une base client/serveur sans rien enregistrer. Un échec de connexion revient avec <c>Ok</c> faux et sa
    ///     consigne ; une demande refusée par l'API (désactivée, champ manquant) lève <see cref="ApiException" />.
    /// </summary>
    public Task<ConnectionTestResult> TestServerAsync(ServerConnectionInput connection) =>
        CallAsync(() => IsSqlServer(connection) ? _api.SqlServerImport.TestAsync(connection) : _api.ServerImport.TestAsync(connection));

    /// <summary>Documente la base client/serveur (SQL Server, PostgreSQL, MySQL) ; la connexion est enregistrée chiffrée côté serveur.</summary>
    public Task<ImportView> ImportServerAsync(ServerConnectionInput connection) =>
        CallAsync(() => IsSqlServer(connection) ? _api.SqlServerImport.ImportAsync(connection) : _api.ServerImport.ImportAsync(connection));

    /// <summary>Remplace la connexion d'une base déjà documentée (même moteur) et la resynchronise.</summary>
    public Task<ImportView> UpdateServerConnectionAsync(string id, ServerConnectionInput connection) =>
        CallAsync(() => IsSqlServer(connection) ? _api.SqlServerImport.ReplaceAsync(id, connection) : _api.ServerImport.ReplaceAsync(id, connection));

    // SQL Server garde sa route d'origine (import/sqlserver) ; les autres moteurs passent par la route générique import/server.
    private static bool IsSqlServer(ServerConnectionInput connection) => string.IsNullOrEmpty(connection.Engine) || connection.Engine == ServerEngines.SqlServer;

    // --- Documentation, sensibilité, complétude ---

    /// <summary>
    ///     Modifie la description et les métadonnées d'une base (<c>DatabaseInfo</c>), table (<c>TableInfo</c>), colonne
    ///     (<c>ColumnInfo</c>) ou relation (<c>RelationInfo</c>). Un texte vide l'efface.
    /// </summary>
    public Task UpdateDocumentationAsync(string kind, string id, string? description, string? metaInfo)
    {
        var body = new DocumentationInput { Description = description, MetaInfo = metaInfo };
        return SendAsync(async () =>
        {
            switch (kind)
            {
                case "DatabaseInfo": await _api.Databases.DocumentAsync(id, body); break;
                case "TableInfo": await _api.Tables.DocumentAsync(id, body); break;
                case "ColumnInfo": await _api.Columns.DocumentAsync(id, body); break;
                case "RelationInfo": await _api.Relations.DocumentAsync(id, body); break;
                case "DomainInfo": await _api.Domains.DocumentAsync(id, body); break;
                case "TagInfo": await _api.Tags.DocumentAsync(id, body); break;
                default: throw new ArgumentException($"Type de document inconnu : {kind}", nameof(kind));
            }
        });
    }

    public Task SetSensitiveAsync(string columnId, bool isSensitive) =>
        SendAsync(() => _api.Columns.SetSensitivityAsync(columnId, new SensitivityInput { IsSensitive = isSensitive }));

    /// <summary>Complétude d'une base, ou des seules tables d'un domaine ; <c>null</c> si elle est indisponible.</summary>
    public Task<CompletenessReport?> GetCompletenessAsync(string databaseId, string? domainId = null) =>
        OptionalAsync<CompletenessReport?>(() => _api.Completeness.OfDatabaseAsync(databaseId, domainId), null);

    // --- Étiquettes ---

    public async Task<TagInfo[]> GetTagsAsync() => (await OptionalAsync(() => _api.Tags.GetAllAsync(), []))!.ToArray();

    /// <summary>Crée une étiquette (nom unique) avec sa couleur.</summary>
    public Task<TagInfo> CreateTagAsync(string name, string? color) =>
        CallAsync(() => _api.Tags.CreateAsync(new TagInput { Name = name, Color = color }));

    public Task UpdateTagAsync(TagInfo tag, string name, string? color) =>
        SendAsync(() => _api.Tags.UpdateAsync(tag.Id, new TagInput { Id = tag.Id, Name = name, Color = color }));

    /// <summary>Supprime l'étiquette et la retire de tous les éléments qui la portent.</summary>
    public Task DeleteTagAsync(string id) => SendAsync(() => _api.Tags.DeleteAsync(id));

    /// <summary>Étiquettes posées dans une base (la base, ses tables et ses colonnes) ; vide si indisponibles.</summary>
    public async Task<TagLinkView[]> GetTagLinksAsync(string databaseId) =>
        (await OptionalAsync(() => _api.Tags.OfDatabaseAsync(databaseId), []))!.ToArray();

    /// <summary>Pose l'étiquette (créée au besoin) sur une base, une table ou une colonne (<c>database</c>, <c>table</c>, <c>column</c>).</summary>
    public Task AttachTagAsync(string name, string entityType, string entityId) =>
        SendAsync(() => _api.Tags.AttachAsync(new AttachTagInput { Name = name, EntityType = entityType, EntityId = entityId }));

    public Task DetachTagAsync(string tagId, string entityType, string entityId) =>
        SendAsync(() => _api.Tags.DetachAsync(tagId, entityType, entityId));

    /// <summary>Supprime la documentation d'une base du catalogue (la base source n'est pas touchée).</summary>
    public Task DeleteDatabaseAsync(string id) => SendAsync(() => _api.Databases.DeleteAsync(id));

    // --- Exports (fichiers : le contrat ne les décrit pas comme des données) ---

    /// <summary>Documentation de la base en un fichier HTML autonome.</summary>
    public Task<string> ExportHtmlAsync(string id) => ExportAsync(id, "html");

    /// <summary>Exporte la documentation d'une base : <paramref name="format" /> = <c>html</c>, <c>markdown</c> ou <c>csv</c>.</summary>
    public async Task<string> ExportAsync(string id, string format) =>
        await (await GetExportAsync(id, format)).Content.ReadAsStringAsync();

    /// <summary>Classeur Excel (.xlsx) de la documentation d'une base, en base64 pour le téléchargement par le navigateur.</summary>
    public async Task<string> ExportXlsxBase64Async(string id) =>
        Convert.ToBase64String(await (await GetExportAsync(id, "xlsx")).Content.ReadAsByteArrayAsync());

    private async Task<HttpResponseMessage> GetExportAsync(string id, string format)
    {
        var response = await http.GetAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}/export/{format}");
        await EnsureFileAsync(response);
        return response;
    }

    // --- Relations manuelles ---

    public Task CreateRelationAsync(string referencedColumnId, string referencingColumnId, string relationType,
        string? description = null, IEnumerable<RelationColumnPair>? extraColumns = null) =>
        SendAsync(() => _api.Relations.CreateAsync(new RelationInput
        {
            FirstId = referencedColumnId, SecondId = referencingColumnId, RelationType = relationType, Description = description,
            ExtraColumns = extraColumns?.ToList()
        }));

    /// <summary>Remplace les colonnes, le type et la description d'une relation manuelle (toutes ses paires, dans l'ordre).</summary>
    public Task UpdateRelationAsync(string id, string referencedColumnId, string referencingColumnId, string relationType,
        string? description, string? metaInfo, IEnumerable<RelationColumnPair> extraColumns) =>
        SendAsync(() => _api.Relations.UpdateAsync(id, new RelationInput
        {
            Id = id, FirstId = referencedColumnId, SecondId = referencingColumnId, RelationType = relationType,
            Description = description, MetaInfo = metaInfo, ExtraColumns = extraColumns.ToList()
        }));

    public Task UpdateRelationTypeAsync(RelationInfo relation, string relationType) =>
        SendAsync(() => _api.Relations.UpdateAsync(relation.Id, new RelationInput
        {
            Id = relation.Id, FirstId = relation.FirstId, SecondId = relation.SecondId, RelationType = relationType,
            Description = relation.Description
        }));

    public Task DeleteRelationAsync(string id) => SendAsync(() => _api.Relations.DeleteAsync(id));

    // --- Domaines ---

    /// <summary>Domaines d'une base avec leurs tables ; vide si indisponibles.</summary>
    public async Task<DomainView[]> GetDomainsAsync(string databaseId) =>
        (await OptionalAsync(() => _api.Domains.OfDatabaseAsync(databaseId), []))!.ToArray();

    public async Task<DomainView> CreateDomainAsync(string databaseId, string name, string? color = null)
    {
        var created = await CallAsync(() => _api.Domains.CreateAsync(new DomainInput { DatabaseId = databaseId, Name = name, Color = color }));
        return new DomainView { Id = created.Id, DatabaseId = created.DatabaseId, Name = created.Name, Color = created.Color, Description = created.Description };
    }

    public Task RenameDomainAsync(DomainView domain, string name) => UpdateDomainAsync(domain, name, domain.Color);

    public Task UpdateDomainAsync(DomainView domain, string name, string? color) =>
        SendAsync(() => _api.Domains.UpdateAsync(domain.Id,
            new DomainInput { Id = domain.Id, DatabaseId = domain.DatabaseId, Name = name, Color = color, Description = domain.Description }));

    public Task DeleteDomainAsync(string id) => SendAsync(() => _api.Domains.DeleteAsync(id));

    /// <summary>Ajoute la table au domaine ; <paramref name="moveFrom" /> la retire en même temps d'un autre domaine.</summary>
    public Task AddTableToDomainAsync(string domainId, string tableId, string? moveFrom = null, double? x = null, double? y = null) =>
        SendAsync(() => _api.Domains.AddTableAsync(domainId, tableId, moveFrom, new PlacementInput { X = x, Y = y }));

    public Task RemoveTableFromDomainAsync(string domainId, string tableId) =>
        SendAsync(() => _api.Domains.RemoveTableAsync(domainId, tableId));

    /// <summary>Règle l'affichage d'un domaine ; un argument absent n'est pas modifié (<paramref name="hiddenRelations" /> remplace la liste).</summary>
    public Task SaveDomainSettingsAsync(string domainId, bool? showNeighbors = null, IEnumerable<string>? hiddenRelations = null) =>
        SendAsync(() => _api.Domains.SettingsAsync(domainId,
            new DomainSettingsInput { ShowNeighbors = showNeighbors, HiddenRelations = hiddenRelations?.ToList() }));

    /// <summary>Colore l'en-tête d'une table dans un domaine (<c>#rrggbb</c>) ; <c>null</c> rend la couleur par défaut (bleu).</summary>
    public Task SetTableColorAsync(string domainId, string tableId, string? color) =>
        SendAsync(() => _api.Domains.SetTableColorAsync(domainId, tableId, new TableColorInput { Color = color }));

    /// <summary>Masque aussi les relations indirectes (hors clés étrangères détectées) du domaine.</summary>
    public Task HideIndirectRelationsAsync(string domainId) =>
        SendAsync(() => _api.Domains.SettingsAsync(domainId, new DomainSettingsInput { HideIndirectRelations = true }));

    /// <summary>Remet les relations masquées du domaine comme dans la documentation d'origine (Dataedo).</summary>
    public Task ResetDomainToDocumentationAsync(string domainId) =>
        SendAsync(() => _api.Domains.SettingsAsync(domainId, new DomainSettingsInput { ResetToDocumentation = true }));

    // --- Tables saisies à la main ---

    /// <summary>Crée d'un coup une table qui n'existe pas dans la source, avec ses colonnes (et son domaine).</summary>
    public Task<TableInfo> CreateManualTableAsync(ManualTableInput input) => CallAsync(() => _api.Tables.CreateManualAsync(input));

    /// <summary>Ajoute des colonnes à une table saisie à la main.</summary>
    public Task<TableInfo> AddManualColumnsAsync(string tableId, IEnumerable<ManualColumnInput> columns) =>
        CallAsync(() => _api.Tables.AddColumnsAsync(tableId, new ManualColumnsInput { Columns = columns.ToList() }));

    /// <summary>Mémorise la place de plusieurs tables du domaine.</summary>
    public Task SaveLayoutAsync(string domainId, IEnumerable<(string TableId, double X, double Y)> places) =>
        SendAsync(() => _api.Domains.SaveLayoutAsync(domainId, places.Select(p => new LayoutItem { TableId = p.TableId, X = p.X, Y = p.Y }).ToList()));

    // --- Journal d'audit et webhooks ---

    /// <summary>Une page du journal d'audit (du plus récent au plus ancien) et le total (<c>X-Total-Count</c>).</summary>
    public async Task<Page<AuditEntry>> GetAuditAsync(AuditFilter filter, int skip, int take)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var response = await CallAsync(() => _api.Audit.GetAsync(Clean(filter.Actor), Clean(filter.Method), filter.Status,
            Clean(filter.Controller), Clean(filter.EntityId), skip: skip, take: take));
        var items = response.Result.ToArray();
        return new Page<AuditEntry>(items, TotalOf(response.Headers, skip + items.Length));
    }

    public async Task<WebhookView[]> GetWebhooksAsync() => (await CallAsync(() => _api.Webhooks.ListAsync())).ToArray();

    public Task SaveWebhookAsync(string? id, WebhookForm form)
    {
        var body = new WebhookInput
        {
            Url = form.Url, Secret = form.Secret, DatabaseId = form.DatabaseId, Events = form.Events, IsActive = form.IsActive,
            Description = form.Description
        };
        return SendAsync(() => id == null ? _api.Webhooks.CreateAsync(body) : _api.Webhooks.UpdateAsync(id, body));
    }

    public Task DeleteWebhookAsync(string id) => SendAsync(() => _api.Webhooks.DeleteAsync(id));

    /// <summary>Envoie un événement d'essai (<c>ping</c>) et renvoie le résultat : code reçu, erreur éventuelle.</summary>
    public Task<WebhookDelivery> TestWebhookAsync(string id) => CallAsync(() => _api.Webhooks.TestAsync(id));

    public async Task<WebhookDelivery[]> GetWebhookDeliveriesAsync(string id, int take = 20) =>
        (await CallAsync(() => _api.Webhooks.DeliveriesAsync(id, take))).ToArray();

    // --- Session ---

    /// <summary>Levé quand l'API répond 401 : la session a expiré (ou n'a jamais existé), l'interface doit proposer la connexion.</summary>
    public static event Action? Unauthorized;

    /// <summary>La session de l'appelant ; sans authentification (ou si l'API ne la décrit pas) : désactivée.</summary>
    public async Task<SessionView> GetSessionAsync()
    {
        try
        {
            return await _api.Auth.MeAsync() ?? NoAuthentication;
        }
        catch (Exception e) when (e is GranitApiException or HttpRequestException or JsonException)
        {
            return NoAuthentication;
        }
    }

    private static SessionView NoAuthentication => new() { Enabled = false, Authenticated = false, Providers = [] };

    /// <summary>Ferme la session.</summary>
    public Task LogoutAsync() => SendAsync(() => _api.Auth.LogoutAsync());

    /// <summary>Adresse qui lance la connexion chez un fournisseur puis revient à <paramref name="returnUrl" /> (navigation du navigateur, pas un appel d'API).</summary>
    public string LoginUrl(string provider, string returnUrl) =>
        new Uri(http.BaseAddress!, $"api/v1/Auth/login/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(returnUrl)}").AbsoluteUri;

    private static void NoteUnauthorized(GranitApiException e)
    {
        if (e.StatusCode == 401) Unauthorized?.Invoke();
    }

    // --- Politique d'erreurs ---

    private static int TotalOf(IReadOnlyDictionary<string, IEnumerable<string>> headers, int fallback) =>
        headers.TryGetValue("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var n) ? n : fallback;

    /// <summary>Appel dont l'échec est un message à afficher : une réponse en erreur devient une <see cref="ApiException" />.</summary>
    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GranitApiException e)
        {
            NoteUnauthorized(e);
            throw new ApiException(MessageOf(e));
        }
    }

    /// <summary>
    ///     Appel dont on n'attend aucun résultat. Une réponse de succès dont le corps n'est pas celui du contrat (vide, par
    ///     exemple) est un succès : seule une réponse en erreur est un message à afficher.
    /// </summary>
    private static async Task SendAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (GranitApiException e) when (e.StatusCode is >= 200 and < 300)
        {
            // fait : le corps de la réponse ne nous sert pas
        }
        catch (GranitApiException e)
        {
            NoteUnauthorized(e);
            throw new ApiException(MessageOf(e));
        }
    }

    /// <summary>Lecture dont une réponse en erreur est une <see cref="HttpRequestException" />, comme un <c>EnsureSuccessStatusCode</c>.</summary>
    private static async Task<T> Strict<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (GranitApiException e)
        {
            NoteUnauthorized(e);
            throw new HttpRequestException($"Réponse {e.StatusCode} de l'API.", e, (System.Net.HttpStatusCode)e.StatusCode);
        }
    }

    /// <summary>
    ///     Lecture d'une information complémentaire : l'absence de réponse utilisable (erreur HTTP, corps inattendu) donne
    ///     la valeur de repli, pour qu'une page s'affiche même si ce complément manque.
    /// </summary>
    private static async Task<T?> OptionalAsync<T>(Func<Task<T>> call, T? fallback)
    {
        try
        {
            return await call() ?? fallback;
        }
        catch (Exception e) when (e is GranitApiException or JsonException)
        {
            if (e is GranitApiException api) NoteUnauthorized(api);
            return fallback;
        }
    }

    private static async Task EnsureFileAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) Unauthorized?.Invoke();
        if (!response.IsSuccessStatusCode) throw new ApiException(MessageOf((int)response.StatusCode, await response.Content.ReadAsStringAsync()));
    }

    private static string MessageOf(GranitApiException e) => MessageOf(e.StatusCode, e.Response);

    /// <summary>
    ///     Le message à montrer pour une réponse en erreur : le premier message de validation, sinon le titre du
    ///     <c>ProblemDetails</c>, sinon le texte brut (l'API refuse un webhook par un simple texte), sinon le code HTTP.
    /// </summary>
    private static string MessageOf(int status, string? body)
    {
        var text = (body ?? "").Trim();
        try
        {
            var problem = JsonSerializer.Deserialize<ProblemBody>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            // Erreur de validation : on montre le premier message plutôt que le titre générique.
            var first = problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first;
            if (!string.IsNullOrWhiteSpace(problem?.Title)) return problem.Title;
        }
        catch (JsonException)
        {
            // pas du JSON : texte brut court, ou code HTTP
        }

        text = text.Trim('"');
        if (text.Length is > 0 and < 300 && !text.StartsWith('{') && !text.StartsWith('<')) return text;
        return $"Erreur {status}";
    }

    private record ProblemBody(string? Title, Dictionary<string, string[]>? Errors = null);
}

public class ApiException(string message) : Exception(message);
