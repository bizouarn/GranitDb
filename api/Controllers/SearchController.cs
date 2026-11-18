using GranitDB.API.Data;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class SearchController(ICatalogRepository repository) : ControllerBase
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
        return Ok(await repository.SearchAsync(term, limit, ct));
    }
}
