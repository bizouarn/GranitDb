using GranitDB.API.Controllers;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace GranitDB.API.Swagger;

/// <summary>
///     Les actions renvoient <c>IActionResult</c> : sans aide, la description OpenAPI ne dit pas ce qu'elles répondent et le client
///     généré n'aurait que des méthodes sans résultat. Ce filtre déclare le type de la réponse de chaque opération (nommée
///     <c>Contrôleur_Action</c>), à un seul endroit ; un test vérifie que chaque opération qui renvoie des données est déclarée.
/// </summary>
public sealed class ResponseTypesFilter : IOperationFilter
{
    private static readonly Dictionary<string, (Type Type, string Status)> Declared = new()
    {
        ["TableInfo_CreateManual"] = (typeof(TableInfo), "201"),
        ["TableInfo_AddColumns"] = (typeof(TableInfo), "200"),
        ["TableInfo_GetProfile"] = (typeof(TableProfileView), "200"),
        ["TableInfo_Profile"] = (typeof(TableProfileView), "200"),
        ["DatabaseInfo_Profile"] = (typeof(ProfileRunView), "200"),
        ["DatabaseInfo_Schedule"] = (typeof(DatabaseView), "200"),
        ["Ai_Status"] = (typeof(AiStatusView), "200"),
        ["Ai_Test"] = (typeof(AiTestView), "200"),
        ["DescriptionSuggestions_List"] = (typeof(IReadOnlyList<SuggestionView>), "200"),
        ["DescriptionSuggestions_Generate"] = (typeof(SuggestionJobView), "200"),
        ["DescriptionSuggestions_Job"] = (typeof(SuggestionJobView), "200"),
        ["DescriptionSuggestions_CancelJob"] = (typeof(SuggestionJobView), "200"),
        ["DescriptionSuggestions_Suggest"] = (typeof(SuggestionView), "200"),
        ["DescriptionSuggestions_DetectSensitive"] = (typeof(AcceptBatchView), "200"),
        ["DescriptionSuggestions_Accept"] = (typeof(SuggestionView), "200"),
        ["DescriptionSuggestions_Reject"] = (typeof(AcceptBatchView), "200"),
        ["DescriptionSuggestions_AcceptConfident"] = (typeof(AcceptBatchView), "200"),
        ["DatabaseInfo_RelationSuggestions"] = (typeof(IReadOnlyList<RelationSuggestionView>), "200"),
        ["DatabaseInfo_DetectRelations"] = (typeof(DetectionRunView), "200"),
        ["DatabaseInfo_AcceptRelationSuggestion"] = (typeof(RelationInfo), "200"),
        ["DatabaseInfo_RejectRelationSuggestion"] = (typeof(RelationSuggestionView), "200"),
        ["DatabaseInfo_AcceptRelationSuggestions"] = (typeof(AcceptBatchView), "200"),
        ["Auth_Me"] = (typeof(GranitDB.API.Security.SessionView), "200"),
        ["Audit_Get"] = (typeof(IReadOnlyList<AuditEntry>), "200"),
        ["ColumnInfo_SetSensitivity"] = (typeof(ColumnInfo), "200"),
        ["Completeness_OfDatabase"] = (typeof(CompletenessReport), "200"),
        ["DatabaseInfo_Sync"] = (typeof(SyncSummaryView), "200"),
        ["DatabaseInfo_PreviewSync"] = (typeof(SyncPreview), "200"),
        ["DocumentationImport_Import"] = (typeof(DocumentationImportResult), "200"),
        ["DomainInfo_OfDatabase"] = (typeof(IReadOnlyList<DomainView>), "200"),
        ["DomainInfo_Settings"] = (typeof(DomainView), "200"),
        ["DomainInfo_AddTable"] = (typeof(DomainView), "200"),
        ["DomainInfo_SaveLayout"] = (typeof(DomainView), "200"),
        ["History_Get"] = (typeof(IReadOnlyList<HistoryEntry>), "200"),
        ["Impact_OfColumn"] = (typeof(ImpactResult), "200"),
        ["Impact_OfTable"] = (typeof(ImpactResult), "200"),
        ["Search_Search"] = (typeof(IReadOnlyList<SearchHit>), "200"),
        ["SqliteImport_Upload"] = (typeof(ImportView), "201"),
        ["SqliteImport_Replace"] = (typeof(ImportView), "200"),
        ["SqliteImport_PreviewReplace"] = (typeof(SyncPreview), "200"),
        ["SqliteImport_Sample"] = (typeof(ImportView), "201"),
        ["SqliteImport_Local"] = (typeof(ImportView), "201"),
        ["Capabilities_Get"] = (typeof(CapabilitiesView), "200"),
        ["SqlServerImport_Test"] = (typeof(ConnectionTestResult), "200"),
        ["SqlServerImport_Import"] = (typeof(ImportView), "201"),
        ["SqlServerImport_Replace"] = (typeof(ImportView), "200"),
        ["ServerImport_Test"] = (typeof(ConnectionTestResult), "200"),
        ["ServerImport_Import"] = (typeof(ImportView), "201"),
        ["ServerImport_Replace"] = (typeof(ImportView), "200"),
        ["TableInfo_GetTablesByDatabaseId"] = (typeof(IReadOnlyList<TableInfo>), "200"),
        ["TableInfo_GetColumns"] = (typeof(IReadOnlyList<ColumnInfo>), "200"),
        ["TableInfo_GetIndexes"] = (typeof(IReadOnlyList<IndexInfo>), "200"),
        ["TableInfo_GetRelations"] = (typeof(IReadOnlyList<RelationInfo>), "200"),
        ["TagInfo_Attach"] = (typeof(TagInfo), "200"),
        ["TagInfo_Of"] = (typeof(IReadOnlyList<TagLinkView>), "200"),
        ["TagInfo_OfDatabase"] = (typeof(IReadOnlyList<TagLinkView>), "200"),
        ["Webhooks_List"] = (typeof(IReadOnlyList<WebhookView>), "200"),
        ["Webhooks_Get"] = (typeof(WebhookView), "200"),
        ["Webhooks_Create"] = (typeof(WebhookView), "201"),
        ["Webhooks_Update"] = (typeof(WebhookView), "200"),
        ["Webhooks_Test"] = (typeof(WebhookDelivery), "200"),
        ["Webhooks_Deliveries"] = (typeof(IReadOnlyList<WebhookDelivery>), "200")
    };

    /// <summary>Ce que renvoient les actions communes du contrôleur de base, par contrôleur.</summary>
    private static readonly Dictionary<string, Type> Entities = new()
    {
        ["ColumnInfo"] = typeof(ColumnInfo),
        ["DatabaseInfo"] = typeof(DatabaseView),
        ["DomainInfo"] = typeof(DomainInfo),
        ["RelationInfo"] = typeof(RelationInfo),
        ["TableInfo"] = typeof(TableInfo),
        ["TagInfo"] = typeof(TagInfo)
    };

    public static bool IsDeclared(string operationId) => Declared.ContainsKey(operationId) || BaseResponse(operationId) != null;

    private static (Type Type, string Status)? BaseResponse(string operationId)
    {
        var parts = operationId.Split('_');
        if (parts.Length != 2 || !Entities.TryGetValue(parts[0], out var entity)) return null;
        return parts[1] switch
        {
            "GetAll" => (typeof(IReadOnlyList<>).MakeGenericType(entity), "200"),
            "Get" or "Update" or "Document" => (entity, "200"),
            "Create" => (entity, "201"),
            _ => null
        };
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var id = operation.OperationId;
        if (id == null) return;
        // Toute suppression répond 204 sans corps (ou 404) : le client généré doit le savoir, sinon un 204 serait une erreur.
        if (context.ApiDescription.HttpMethod == "DELETE")
        {
            operation.Responses ??= [];
            operation.Responses.Remove("200");
            operation.Responses["204"] = new OpenApiResponse { Description = "No Content" };
            return;
        }

        var response = Declared.TryGetValue(id, out var found) ? found : BaseResponse(id);
        if (response is not { } declared) return;

        var schema = context.SchemaGenerator.GenerateSchema(declared.Type, context.SchemaRepository);
        operation.Responses ??= [];
        // Swashbuckle ajoute une réponse « 200 » par défaut : pour une création (201) elle ferait passer la réponse pour vide.
        if (declared.Status != "200") operation.Responses.Remove("200");
        if (!operation.Responses.TryGetValue(declared.Status, out var entry))
            operation.Responses[declared.Status] = entry = new OpenApiResponse { Description = declared.Status == "201" ? "Created" : "OK" };
        if (entry is OpenApiResponse concrete)
        {
            concrete.Content ??= new Dictionary<string, IOpenApiMediaType>();
            concrete.Content["application/json"] = new OpenApiMediaType { Schema = schema };
        }
    }
}
