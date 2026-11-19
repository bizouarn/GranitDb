using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class SearchController(ICatalogRepository repository, IOptions<AuthOptions> auth) : ControllerBase
{
    /// <summary>
    ///     Recherche globale (nom, description, colonnes d'index) dans les bases, tables, colonnes et index.
    ///     <c>limit</c> : résultats maximum par type (1 à 100, 25 par défaut).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 25, CancellationToken ct = default)
    {
        var term = q?.Trim() ?? "";
        if (term.Length < 2) return BadRequest("Le terme de recherche doit contenir au moins 2 caractères.");
        if (term.Length > 100) return BadRequest("Le terme de recherche est limité à 100 caractères.");
        if (limit is < 1 or > 100) return BadRequest("limit doit être compris entre 1 et 100.");
        var hits = await repository.SearchAsync(term, limit, ct);
        // Sans compte : uniquement ce qui appartient à une base publique.
        if (auth.Value.Enabled && User.Identity?.IsAuthenticated != true)
        {
            var publicIds = (await repository.GetAllAsync<DatabaseInfo>(
                [new CatalogFilter(nameof(DatabaseInfo.IsPublic), "true", FilterMode.Bool)], ct)).Select(d => d.Id).ToHashSet();
            hits = hits.Where(h => publicIds.Contains(h.DatabaseId)).ToList();
        }

        return Ok(hits);
    }
}
