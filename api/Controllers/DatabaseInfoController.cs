using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Options;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class DatabaseInfoController(ICatalogRepository repository, DatabaseImportService import, IOptions<AuthOptions> auth,
    ColumnProfilingService profiling)
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
