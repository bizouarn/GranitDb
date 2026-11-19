using GranitDB.API.Services;
using GranitDB.API.Security;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Journal d'audit : qui a modifié quoi et quand (requêtes d'écriture et exports), du plus récent au plus ancien.</summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
[MinimumRole(Role.Admin)]
public class AuditController(AuditLog log) : ControllerBase
{
    /// <summary>
    ///     Filtres cumulables : <c>actor</c>, <c>method</c>, <c>status</c>, <c>controller</c>, <c>entityId</c>,
    ///     <c>from</c> / <c>to</c> (dates ISO, UTC par défaut). <c>skip</c>/<c>take</c> (take de 1 à 1000, 50 par
    ///     défaut) paginent ; le total est dans <c>X-Total-Count</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? actor, [FromQuery] string? method, [FromQuery] int? status,
        [FromQuery] string? controller, [FromQuery] string? entityId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (skip < 0 || take is < 1 or > 1000)
            return BadRequest("skip doit être >= 0 et take compris entre 1 et 1000.");
        var query = new AuditQuery(actor, method, status, controller, entityId, from, to);
        Response.Headers["X-Total-Count"] = (await log.CountAsync(query, ct)).ToString();
        return Ok(await log.QueryAsync(query, skip, take, ct));
    }
}
