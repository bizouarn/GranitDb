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
