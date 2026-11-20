using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Options;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

/// <summary>Réglage de la synchronisation automatique d'une base.</summary>
public class ScheduleInput
{
    /// <summary>Minutes entre deux relectures de la source ; 0 ou absent : désactivée.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 525600)]
    public int? IntervalMinutes { get; set; }
}

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class DatabaseInfoController(ICatalogRepository repository, DatabaseImportService import, IOptions<AuthOptions> auth,
    ColumnProfilingService profiling, IOptions<CatalogOptions> catalogOptions, RelationDetectionService detection)
    : BaseDbInfoController<DatabaseInfo, DatabaseInput>(repository)
{
    /// <summary>Appelant sans compte (possible seulement pour les bases publiques, avec l'authentification activée).</summary>
    private bool IsVisitor => auth.Value.Enabled && User.Identity?.IsAuthenticated != true;

    private bool IsAdmin => !auth.Value.Enabled || RoleAuthorizationFilter.RoleOf(User) == Role.Admin;

    /// <summary>
    ///     Une base se présente par sa vue : ni chemin de fichier, ni connexion enregistrée. Un visiteur sans compte ne voit
    ///     pas non plus d'où elle vient (serveur et base source).
    /// </summary>
    protected override object View(DatabaseInfo entity)
    {
        var view = DatabaseView.From(entity);
        return IsVisitor ? view with { SourceLabel = null, CanResync = false } : view;
    }

    /// <summary>Un visiteur ne voit que les bases publiques.</summary>
    protected override async Task<List<CatalogFilter>> ReadAllFiltersAsync(CancellationToken ct)
    {
        var filters = await base.ReadAllFiltersAsync(ct);
        if (IsVisitor) filters.Add(new CatalogFilter(nameof(DatabaseInfo.IsPublic), "true", FilterMode.Bool));
        return filters;
    }

    /// <summary>Rendre une base publique est une décision d'administrateur : pour les autres, la valeur existante est conservée.</summary>
    [HttpPost]
    public override Task<IActionResult> CreateT([FromBody] DatabaseInput input, CancellationToken ct)
    {
        if (!IsAdmin) input.IsPublic = false;
        return base.CreateT(input, ct);
    }

    [HttpPut("{id}")]
    public override async Task<IActionResult> UpdateT(string id, [FromBody] DatabaseInput input, CancellationToken ct)
    {
        if (!IsAdmin && await Repository.GetAsync<DatabaseInfo>(id, ct) is { } existing) input.IsPublic = existing.IsPublic;
        return await base.UpdateT(id, input, ct);
    }

    /// <summary>Supprime la documentation de la base (et son fichier source conservé) ; la base source n'est pas touchée.</summary>
    [HttpDelete("{id}")]
    public override async Task<IActionResult> DeleteT(string id, CancellationToken ct)
    {
        return await import.DeleteDatabaseAsync(id, Actor, ct) ? NoContent() : NotFound();
    }

    /// <summary>
    ///     Profile les tables de la base (les plus anciennement profilées d'abord, au plus <c>Catalog:ProfilingMaxTables</c> par
    ///     demande) : la réponse dit combien ont été profilées, combien ont échoué et combien restent pour une demande suivante.
    /// </summary>
    [HttpPost("{id}/profile")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Profile(string id, CancellationToken ct)
    {
        var run = await profiling.ProfileDatabaseAsync(id, ct);
        return run == null ? NotFound() : Ok(run);
    }

    /// <summary>
    ///     Relations que le schéma ne déclare pas, proposées par la détection (<c>status</c> : pending par défaut, rejected ou
    ///     accepted), les mieux notées d'abord. Des comptes et des raisons, jamais une valeur de la source.
    /// </summary>
    [HttpGet("{id}/relation-suggestions")]
    public async Task<IActionResult> RelationSuggestions(string id, [FromQuery] string? status, CancellationToken ct)
    {
        var list = await detection.ListAsync(id, status, ct);
        return list == null ? NotFound() : Ok(list);
    }

    /// <summary>
    ///     Détecte les relations non déclarées (noms, types, clés puis vérification par le profilage) et remplace les suggestions en
    ///     attente. Demande <c>Catalog:AllowProfiling</c> et le rôle administrateur : la vérification lit des comptes à la source.
    /// </summary>
    [HttpPost("{id}/relation-suggestions/detect")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> DetectRelations(string id, CancellationToken ct)
    {
        var run = await detection.DetectAsync(id, ct);
        return run == null ? NotFound() : Ok(run);
    }

    /// <summary>Accepte une suggestion : crée la relation manuelle correspondante (marquée <c>detected</c>).</summary>
    [HttpPost("{id}/relation-suggestions/{suggestionId}/accept")]
    public async Task<IActionResult> AcceptRelationSuggestion(string id, string suggestionId, CancellationToken ct)
    {
        var relation = await detection.AcceptAsync(id, suggestionId, ct);
        return relation == null ? NotFound() : Ok(relation);
    }

    /// <summary>Refuse une suggestion : les détections suivantes ne la proposent plus.</summary>
    [HttpPost("{id}/relation-suggestions/{suggestionId}/reject")]
    public async Task<IActionResult> RejectRelationSuggestion(string id, string suggestionId, CancellationToken ct)
    {
        var rejected = await detection.RejectAsync(id, suggestionId, ct);
        return rejected == null ? NotFound() : Ok(rejected);
    }

    /// <summary>Accepte d'un coup les suggestions en attente dont le score atteint <c>minScore</c> (75 par défaut : « probable »).</summary>
    [HttpPost("{id}/relation-suggestions/accept")]
    public async Task<IActionResult> AcceptRelationSuggestions(string id, [FromQuery] int minScore = 75, CancellationToken ct = default)
    {
        var batch = await detection.AcceptAboveAsync(id, Math.Clamp(minScore, 0, 100), ct);
        return batch == null ? NotFound() : Ok(batch);
    }

    /// <summary>
    ///     Règle la synchronisation automatique : toutes les <c>intervalMinutes</c> minutes (au moins <c>Catalog:MinSyncIntervalMinutes</c>)
    ///     l'API relit la source ; 0 ou absent la désactive. Réservé aux administrateurs (l'API se connecte alors seule à la source).
    /// </summary>
    [HttpPut("{id}/schedule")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Schedule(string id, [FromBody] ScheduleInput input, CancellationToken ct)
    {
        var database = await Repository.GetAsync<DatabaseInfo>(id, ct);
        if (database == null) return NotFound();
        var minutes = input.IntervalMinutes is > 0 ? input.IntervalMinutes : null;
        if (minutes != null)
        {
            var catalog = catalogOptions.Value;
            if (!catalog.AllowScheduledSync)
                throw new GranitExceptions("La synchronisation planifiée est désactivée (Catalog:AllowScheduledSync = false).");
            if (string.IsNullOrWhiteSpace(database.SourcePath) && string.IsNullOrWhiteSpace(database.SourceSecret))
                throw new GranitExceptions("Cette base n'a pas de source à relire : renvoyez le fichier ou enregistrez la connexion.");
            if (minutes < catalog.MinSyncIntervalMinutes)
                throw new GranitExceptions($"L'intervalle minimal est de {catalog.MinSyncIntervalMinutes} minute(s).");
        }

        database.SyncIntervalMinutes = minutes;
        await Repository.UpdateAsync(id, database, ct);
        return Ok(View(database));
    }

    /// <summary>Documentation de la base en un seul fichier HTML autonome (présentation type Dataedo), à télécharger.</summary>
    [HttpGet("{id}/export/html")]
    public async Task<IActionResult> ExportHtml(string id, CancellationToken ct)
    {
        var html = await HtmlDocumentationExporter.ExportAsync(repository, id, ct);
        return html == null ? NotFound() : File(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", "documentation.html");
    }

    /// <summary>Documentation en Markdown (un fichier .md : domaines, tables, colonnes, relations, index).</summary>
    [HttpGet("{id}/export/markdown")]
    public async Task<IActionResult> ExportMarkdown(string id, CancellationToken ct)
    {
        var snapshot = await HtmlDocumentationExporter.LoadAsync(repository, id, ct);
        return snapshot == null ? NotFound() : File(System.Text.Encoding.UTF8.GetBytes(DocumentationTextExporters.ToMarkdown(snapshot)), "text/markdown; charset=utf-8", "documentation.md");
    }

    /// <summary>Dictionnaire des colonnes en CSV (ouvre directement dans Excel).</summary>
    [HttpGet("{id}/export/csv")]
    public async Task<IActionResult> ExportCsv(string id, CancellationToken ct)
    {
        var snapshot = await HtmlDocumentationExporter.LoadAsync(repository, id, ct);
        return snapshot == null ? NotFound() : File(System.Text.Encoding.UTF8.GetBytes(DocumentationTextExporters.ToCsv(snapshot)), "text/csv; charset=utf-8", "colonnes.csv");
    }

    /// <summary>Documentation en classeur Excel natif (.xlsx) : onglets Tables, Colonnes, Relations et Index.</summary>
    [HttpGet("{id}/export/xlsx")]
    public async Task<IActionResult> ExportXlsx(string id, CancellationToken ct)
    {
        var snapshot = await HtmlDocumentationExporter.LoadAsync(repository, id, ct);
        return snapshot == null ? NotFound() : File(XlsxExporter.ToXlsx(snapshot),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "documentation.xlsx");
    }

    /// <summary>Relit la source conservée (fichier SQLite, connexion SQL Server enregistrée) et met le catalogue à jour.</summary>
    [HttpPost("{id}/sync")]
    public async Task<IActionResult> Sync(string id, CancellationToken ct)
    {
        var result = await import.ResyncAsync(id, ct);
        return result == null ? NotFound() : Ok(SyncSummaryView.From(result.Sync));
    }

    /// <summary>
    ///     Aperçu de la synchronisation : tables, colonnes, index et relations qui seraient ajoutés, modifiés ou
    ///     supprimés, avec ce qui ferait perdre une documentation saisie. Ne modifie rien.
    /// </summary>
    [HttpPost("{id}/sync/preview")]
    public async Task<IActionResult> PreviewSync(string id, CancellationToken ct)
    {
        var preview = await import.PreviewResyncAsync(id, ct);
        return preview == null ? NotFound() : Ok(preview);
    }
}
