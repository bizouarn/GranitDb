using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class DatabaseInfoController(ICatalogRepository repository, DatabaseImportService import)
    : BaseDbInfoController<DatabaseInfo, DatabaseInput>(repository)
{
    /// <summary>Une base se présente par sa vue : ni chemin de fichier, ni connexion enregistrée.</summary>
    protected override object View(DatabaseInfo entity) => DatabaseView.From(entity);

    /// <summary>Supprime la documentation de la base (et son fichier source conservé) ; la base source n'est pas touchée.</summary>
    [HttpDelete("{id}")]
    public override async Task<IActionResult> DeleteT(string id, CancellationToken ct)
    {
        return await import.DeleteDatabaseAsync(id, Actor, ct) ? NoContent() : NotFound();
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
