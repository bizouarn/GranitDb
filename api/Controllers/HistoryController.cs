using GranitDB.API.Data;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Historique des modifications des descriptions (et de <c>MetaInfo</c>) : quoi, quand, ancienne valeur.</summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class HistoryController(ICatalogRepository repository) : ControllerBase
{
    /// <summary>
    ///     Du plus récent au plus ancien. <c>entityId</c> limite à une base, table, colonne, relation ou index ;
    ///     <c>skip</c>/<c>take</c> (take de 1 à 1000, 50 par défaut) paginent, le total est dans <c>X-Total-Count</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? entityId, [FromQuery] int skip = 0,
        [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (skip < 0 || take is < 1 or > 1000)
            return BadRequest("skip doit être >= 0 et take compris entre 1 et 1000.");
        if (string.IsNullOrWhiteSpace(entityId)) entityId = null;
        Response.Headers["X-Total-Count"] = (await repository.CountHistoryAsync(entityId, ct)).ToString();
        return Ok(await repository.GetHistoryAsync(entityId, skip, take, ct));
    }
}
