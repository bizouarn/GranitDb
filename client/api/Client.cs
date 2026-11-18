using System.Net.Http.Headers;
using System.Net.Http.Json;
using GranitDb.Client.Models;

namespace GranitDb.Client.api;

/// <summary>Client HTTP de l'API GranitDb.</summary>
public class Client(HttpClient http)
{
    public async Task<DatabaseInfo[]> GetAllDatabaseAsync()
    {
        return await http.GetFromJsonAsync<DatabaseInfo[]>("api/v1/DatabaseInfo") ?? [];
    }

    /// <summary>Une page de bases (triées par nom) et le total, lu dans l'en-tête <c>X-Total-Count</c>.</summary>
    public async Task<Page<DatabaseInfo>> GetDatabasesPageAsync(int skip, int take)
    {
        var response = await http.GetAsync($"api/v1/DatabaseInfo?skip={skip}&take={take}");
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<DatabaseInfo[]>() ?? [];
        var total = response.Headers.TryGetValues("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var n)
            ? n
            : skip + items.Length;
        return new Page<DatabaseInfo>(items, total);
    }

    /// <summary>Recherche globale ; lève <see cref="ApiException" /> si le terme est refusé par l'API.</summary>
    public async Task<SearchHit[]> SearchAsync(string term)
    {
        var response = await http.GetAsync($"api/v1/Search?q={Uri.EscapeDataString(term)}");
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response));
        return await response.Content.ReadFromJsonAsync<SearchHit[]>() ?? [];
    }

    public async Task<DatabaseInfo?> GetDatabaseAsync(string id)
    {
        var response = await http.GetAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<DatabaseInfo>() : null;
    }

    /// <summary>Tables d'une base, avec colonnes et relations. Liste vide si la base n'a pas de table.</summary>
    public async Task<TableInfo[]> GetTablesAsync(string databaseId)
    {
        var response = await http.GetAsync($"api/v1/TableInfo/database/{Uri.EscapeDataString(databaseId)}");
        if (!response.IsSuccessStatusCode) return [];
        return await response.Content.ReadFromJsonAsync<TableInfo[]>() ?? [];
    }

    /// <summary>
    ///     Analyse d'impact d'une table (<paramref name="kind" /> = <c>table</c>) ou d'une colonne (<c>column</c>) ;
    ///     <c>null</c> si l'élément n'existe pas.
    /// </summary>
    public async Task<ImpactResult?> GetImpactAsync(string kind, string id)
    {
        var response = await http.GetAsync($"api/v1/Impact/{kind}/{Uri.EscapeDataString(id)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ImpactResult>() : null;
    }

    /// <summary>Envoie un fichier SQLite ; lève <see cref="ApiException" /> avec le message du serveur si refusé.</summary>
    public async Task<ImportResponse> UploadSqliteAsync(Stream file, string fileName, string? name, string? description)
    {
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        if (!string.IsNullOrWhiteSpace(name)) form.Add(new StringContent(name), "name");
        if (!string.IsNullOrWhiteSpace(description)) form.Add(new StringContent(description), "description");

        var response = await http.PostAsync("api/v1/import/sqlite", form);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response));
        return (await response.Content.ReadFromJsonAsync<ImportResponse>())!;
    }

    private static MultipartFormDataContent FileForm(byte[] file, string fileName)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", fileName);
        return form;
    }

    /// <summary>Aperçu du remplacement du fichier d'une base SQLite : rien n'est modifié.</summary>
    public async Task<SyncPreview> PreviewReplaceSqliteAsync(string id, byte[] file, string fileName)
    {
        using var form = FileForm(file, fileName);
        var response = await http.PostAsync($"api/v1/import/sqlite/{Uri.EscapeDataString(id)}/preview", form);
        if (!response.IsSuccessStatusCode) throw new ApiException(await ReadErrorAsync(response));
        return (await response.Content.ReadFromJsonAsync<SyncPreview>())!;
    }

    /// <summary>Remplace le fichier d'une base SQLite et la resynchronise (descriptions conservées).</summary>
    public async Task<ImportResponse> ReplaceSqliteAsync(string id, byte[] file, string fileName)
    {
        using var form = FileForm(file, fileName);
        var response = await http.PutAsync($"api/v1/import/sqlite/{Uri.EscapeDataString(id)}", form);
        if (!response.IsSuccessStatusCode) throw new ApiException(await ReadErrorAsync(response));
        return (await response.Content.ReadFromJsonAsync<ImportResponse>())!;
    }

    public async Task<SyncSummary> SyncAsync(string id)
    {
        var response = await http.PostAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}/sync", null);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response));
        return (await response.Content.ReadFromJsonAsync<SyncSummary>())!;
    }

    // --- Sources SQL Server ---

    /// <summary>Capacités activées sur le serveur ; tout est refusé si elles sont illisibles.</summary>
    public async Task<Capabilities> GetCapabilitiesAsync()
    {
        try
        {
            return await GetOptionalAsync<Capabilities>("api/v1/Capabilities", new Capabilities(false, false)) ?? new Capabilities(false, false);
        }
        catch (HttpRequestException)
        {
            return new Capabilities(false, false); // les pages gèrent elles-mêmes l'API injoignable
        }
    }

    /// <summary>
    ///     Essaie une connexion SQL Server sans rien enregistrer. Un échec de connexion revient avec <c>Ok</c> faux et sa
    ///     consigne ; une demande refusée par l'API (désactivée, champ manquant) lève <see cref="ApiException" />.
    /// </summary>
    public async Task<ConnectionTestResult> TestSqlServerAsync(SqlServerConnection connection)
    {
        var response = await http.PostAsJsonAsync("api/v1/import/sqlserver/test", connection);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<ConnectionTestResult>())!;
    }

    /// <summary>Documente la base SQL Server ; la connexion est enregistrée chiffrée côté serveur.</summary>
    public async Task<ImportResponse> ImportSqlServerAsync(SqlServerConnection connection)
    {
        var response = await http.PostAsJsonAsync("api/v1/import/sqlserver", connection);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<ImportResponse>())!;
    }

    /// <summary>Remplace la connexion d'une base déjà documentée et la resynchronise.</summary>
    public async Task<ImportResponse> UpdateSqlServerConnectionAsync(string id, SqlServerConnection connection)
    {
        var response = await http.PutAsJsonAsync($"api/v1/import/sqlserver/{Uri.EscapeDataString(id)}", connection);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<ImportResponse>())!;
    }

    // --- Documentation, sensibilité, complétude ---

    /// <summary>
    ///     Modifie la description et les métadonnées d'une base (<c>DatabaseInfo</c>), table (<c>TableInfo</c>), colonne
    ///     (<c>ColumnInfo</c>) ou relation (<c>RelationInfo</c>). Un texte vide l'efface.
    /// </summary>
    public async Task UpdateDocumentationAsync(string kind, string id, string? description, string? metaInfo)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/{kind}/{Uri.EscapeDataString(id)}/documentation",
            new { description, metaInfo }));
    }

    public async Task SetSensitiveAsync(string columnId, bool isSensitive)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/ColumnInfo/{Uri.EscapeDataString(columnId)}/sensitivity",
            new { isSensitive }));
    }

    /// <summary>Complétude d'une base, ou des seules tables d'un domaine ; <c>null</c> si elle est indisponible.</summary>
    public async Task<CompletenessReport?> GetCompletenessAsync(string databaseId, string? domainId = null)
    {
        var url = $"api/v1/Completeness/database/{Uri.EscapeDataString(databaseId)}" +
                  (domainId == null ? "" : $"?domainId={Uri.EscapeDataString(domainId)}");
        return await GetOptionalAsync<CompletenessReport?>(url, null);
    }

    // --- Synchronisation : aperçu ---

    /// <summary>Ce que ferait la synchronisation, sans l'appliquer ; lève <see cref="ApiException" /> si impossible.</summary>
    public async Task<SyncPreview> PreviewSyncAsync(string id)
    {
        var response = await http.PostAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}/sync/preview", null);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<SyncPreview>())!;
    }

    // --- Étiquettes ---

    public async Task<TagInfo[]> GetTagsAsync()
    {
        return await GetOptionalAsync<TagInfo[]>("api/v1/TagInfo", []) ?? [];
    }

    /// <summary>Étiquettes posées dans une base (la base, ses tables et ses colonnes) ; vide si indisponibles.</summary>
    public async Task UpdateTagAsync(TagInfo tag, string name, string? color)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/TagInfo/{Uri.EscapeDataString(tag.Id)}", new { id = tag.Id, name, color }));
    }

    /// <summary>Supprime l'étiquette et la retire de tous les éléments qui la portent.</summary>
    public async Task DeleteTagAsync(string id)
    {
        await EnsureAsync(await http.DeleteAsync($"api/v1/TagInfo/{Uri.EscapeDataString(id)}"));
    }

    public async Task<TagLinkView[]> GetTagLinksAsync(string databaseId)
    {
        return await GetOptionalAsync<TagLinkView[]>($"api/v1/TagInfo/database/{Uri.EscapeDataString(databaseId)}/links", []) ?? [];
    }

    /// <summary>Pose l'étiquette (créée au besoin) sur une base, une table ou une colonne (<c>database</c>, <c>table</c>, <c>column</c>).</summary>
    public async Task AttachTagAsync(string name, string entityType, string entityId)
    {
        await EnsureAsync(await http.PostAsJsonAsync("api/v1/TagInfo/attach", new { name, entityType, entityId }));
    }

    public async Task DetachTagAsync(string tagId, string entityType, string entityId)
    {
        await EnsureAsync(await http.DeleteAsync(
            $"api/v1/TagInfo/{Uri.EscapeDataString(tagId)}/links/{entityType}/{Uri.EscapeDataString(entityId)}"));
    }

    /// <summary>Supprime la documentation d'une base du catalogue (la base source n'est pas touchée).</summary>
    public async Task DeleteDatabaseAsync(string id)
    {
        await EnsureAsync(await http.DeleteAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}"));
    }

    /// <summary>Documentation de la base en un fichier HTML autonome.</summary>
    public Task<string> ExportHtmlAsync(string id) => ExportAsync(id, "html");

    /// <summary>Exporte la documentation d'une base : <paramref name="format" /> = <c>html</c>, <c>markdown</c> ou <c>csv</c>.</summary>
    public async Task<string> ExportAsync(string id, string format)
    {
        var response = await http.GetAsync($"api/v1/DatabaseInfo/{Uri.EscapeDataString(id)}/export/{format}");
        await EnsureAsync(response);
        return await response.Content.ReadAsStringAsync();
    }

    // --- Relations manuelles ---

    public async Task CreateRelationAsync(string referencedColumnId, string referencingColumnId, string relationType,
        string? description = null, IEnumerable<RelationColumnPair>? extraColumns = null)
    {
        await EnsureAsync(await http.PostAsJsonAsync("api/v1/RelationInfo",
            new
            {
                firstId = referencedColumnId, secondId = referencingColumnId, relationType, description,
                extraColumns = extraColumns?.ToList()
            }));
    }

    /// <summary>Remplace les colonnes, le type et la description d'une relation manuelle (toutes ses paires, dans l'ordre).</summary>
    public async Task UpdateRelationAsync(string id, string referencedColumnId, string referencingColumnId, string relationType,
        string? description, string? metaInfo, IEnumerable<RelationColumnPair> extraColumns)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/RelationInfo/{Uri.EscapeDataString(id)}",
            new
            {
                id, firstId = referencedColumnId, secondId = referencingColumnId, relationType, description, metaInfo,
                extraColumns = extraColumns.ToList()
            }));
    }

    public async Task UpdateRelationTypeAsync(RelationInfo relation, string relationType)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/RelationInfo/{Uri.EscapeDataString(relation.Id)}",
            new
            {
                id = relation.Id, firstId = relation.FirstId, secondId = relation.SecondId, relationType,
                description = relation.Description
            }));
    }

    public async Task DeleteRelationAsync(string id)
    {
        await EnsureAsync(await http.DeleteAsync($"api/v1/RelationInfo/{Uri.EscapeDataString(id)}"));
    }

    // --- Domaines ---

    /// <summary>Domaines d'une base avec leurs tables ; vide si indisponibles.</summary>
    public async Task<DomainInfo[]> GetDomainsAsync(string databaseId)
    {
        return await GetOptionalAsync<DomainInfo[]>($"api/v1/DomainInfo/database/{Uri.EscapeDataString(databaseId)}", []) ?? [];
    }

    public async Task<DomainInfo> CreateDomainAsync(string databaseId, string name, string? color = null)
    {
        var response = await http.PostAsJsonAsync("api/v1/DomainInfo", new { databaseId, name, color });
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<DomainInfo>())!;
    }

    public async Task RenameDomainAsync(DomainInfo domain, string name)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/DomainInfo/{Uri.EscapeDataString(domain.Id)}",
            new { id = domain.Id, databaseId = domain.DatabaseId, name, color = domain.Color, description = domain.Description }));
    }

    public async Task UpdateDomainAsync(DomainInfo domain, string name, string? color)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/DomainInfo/{Uri.EscapeDataString(domain.Id)}",
            new { id = domain.Id, databaseId = domain.DatabaseId, name, color, description = domain.Description }));
    }

    public async Task DeleteDomainAsync(string id)
    {
        await EnsureAsync(await http.DeleteAsync($"api/v1/DomainInfo/{Uri.EscapeDataString(id)}"));
    }

    /// <summary>Ajoute la table au domaine ; <paramref name="moveFrom" /> la retire en même temps d'un autre domaine.</summary>
    public async Task AddTableToDomainAsync(string domainId, string tableId, string? moveFrom = null, double? x = null,
        double? y = null)
    {
        var url = $"api/v1/DomainInfo/{Uri.EscapeDataString(domainId)}/tables/{Uri.EscapeDataString(tableId)}" +
                  (moveFrom == null ? "" : $"?moveFrom={Uri.EscapeDataString(moveFrom)}");
        await EnsureAsync(await http.PutAsJsonAsync(url, new { x, y }));
    }

    public async Task RemoveTableFromDomainAsync(string domainId, string tableId)
    {
        await EnsureAsync(await http.DeleteAsync(
            $"api/v1/DomainInfo/{Uri.EscapeDataString(domainId)}/tables/{Uri.EscapeDataString(tableId)}"));
    }

    /// <summary>Mémorise la place de plusieurs tables du domaine.</summary>
    /// <summary>Règle l'affichage d'un domaine ; un argument absent n'est pas modifié (<paramref name="hiddenRelations" /> remplace la liste).</summary>
    public async Task SaveDomainSettingsAsync(string domainId, bool? showNeighbors = null,
        IEnumerable<string>? hiddenRelations = null)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/DomainInfo/{Uri.EscapeDataString(domainId)}/settings",
            new { showNeighbors, hiddenRelations = hiddenRelations?.ToArray() }));
    }

    public async Task SaveLayoutAsync(string domainId, IEnumerable<(string TableId, double X, double Y)> places)
    {
        await EnsureAsync(await http.PutAsJsonAsync($"api/v1/DomainInfo/{Uri.EscapeDataString(domainId)}/layout",
            places.Select(p => new { tableId = p.TableId, x = p.X, y = p.Y })));
    }

    // --- Journal d'audit et webhooks ---

    /// <summary>Une page du journal d'audit (du plus récent au plus ancien) et le total (<c>X-Total-Count</c>).</summary>
    public async Task<Page<AuditEntry>> GetAuditAsync(AuditFilter filter, int skip, int take)
    {
        var query = new List<string> { $"skip={skip}", $"take={take}" };
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }

        Add("actor", filter.Actor);
        Add("method", filter.Method);
        Add("status", filter.Status?.ToString());
        Add("controller", filter.Controller);
        Add("entityId", filter.EntityId);
        var response = await http.GetAsync("api/v1/Audit?" + string.Join('&', query));
        await EnsureAsync(response);
        var items = await response.Content.ReadFromJsonAsync<AuditEntry[]>() ?? [];
        var total = response.Headers.TryGetValues("X-Total-Count", out var values) && int.TryParse(values.FirstOrDefault(), out var n)
            ? n
            : skip + items.Length;
        return new Page<AuditEntry>(items, total);
    }

    public async Task<WebhookView[]> GetWebhooksAsync()
    {
        var response = await http.GetAsync("api/v1/Webhooks");
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<WebhookView[]>() ?? [];
    }

    public async Task SaveWebhookAsync(string? id, WebhookForm form)
    {
        var body = new
        {
            url = form.Url, secret = form.Secret, databaseId = form.DatabaseId, events = form.Events, isActive = form.IsActive,
            description = form.Description
        };
        var response = id == null
            ? await http.PostAsJsonAsync("api/v1/Webhooks", body)
            : await http.PutAsJsonAsync($"api/v1/Webhooks/{Uri.EscapeDataString(id)}", body);
        await EnsureTextAsync(response);
    }

    public async Task DeleteWebhookAsync(string id)
    {
        await EnsureAsync(await http.DeleteAsync($"api/v1/Webhooks/{Uri.EscapeDataString(id)}"));
    }

    /// <summary>Envoie un événement d'essai (<c>ping</c>) et renvoie le résultat : code reçu, erreur éventuelle.</summary>
    public async Task<WebhookDelivery> TestWebhookAsync(string id)
    {
        var response = await http.PostAsync($"api/v1/Webhooks/{Uri.EscapeDataString(id)}/test", null);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<WebhookDelivery>())!;
    }

    public async Task<WebhookDelivery[]> GetWebhookDeliveriesAsync(string id, int take = 20)
    {
        var response = await http.GetAsync($"api/v1/Webhooks/{Uri.EscapeDataString(id)}/deliveries?take={take}");
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<WebhookDelivery[]>() ?? [];
    }

    /// <summary>Comme <see cref="EnsureAsync" />, mais l'API refuse un webhook par un simple texte (pas un ProblemDetails).</summary>
    private static async Task EnsureTextAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var text = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        if (text.Length is > 0 and < 300 && !text.StartsWith('{') && !text.StartsWith('<')) throw new ApiException(text);
        throw new ApiException(await ReadErrorAsync(response));
    }

    /// <summary>
    ///     Lecture d'une information complémentaire : l'absence de réponse utilisable (erreur HTTP, corps inattendu) donne
    ///     la valeur de repli, pour qu'une page s'affiche même si ce complément manque.
    /// </summary>
    private async Task<T?> GetOptionalAsync<T>(string url, T? fallback)
    {
        try
        {
            var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() ?? fallback : fallback;
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback;
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>();
            // Erreur de validation : on montre le premier message plutôt que le titre générique.
            var first = problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first;
            if (!string.IsNullOrWhiteSpace(problem?.Title)) return problem.Title;
        }
        catch (Exception)
        {
            // corps non JSON : on retombe sur le code HTTP
        }

        return $"Erreur {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private record ProblemBody(string? Title, Dictionary<string, string[]>? Errors = null);
}

public class ApiException(string message) : Exception(message);
