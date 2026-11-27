using GranitDB.API.Controllers.Base;
using GranitDB.API.Models;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

public class LocalSqliteRequest
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>
///     Documente une base à partir de son fichier, sans serveur : SQLite, ou H2 (<c>.mv.db</c>), reconnu à son en-tête (la route
///     garde son nom d'origine).
/// </summary>
[Route("api/v1/import/sqlite")]
[Route("import/sqlite")]
[ApiController]
public class SqliteImportController(DatabaseImportService import) : ControllerBase
{
    /// <summary>Envoie un fichier SQLite ou H2 (multipart) : crée la base et extrait tables, colonnes et relations.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(long.MaxValue)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? name, [FromForm] string? description,
        [FromForm] bool keepFile = true, CancellationToken ct = default)
    {
        if (file == null || file.Length == 0) return BadRequest("Aucun fichier n'a été envoyé.");
        await using var stream = file.OpenReadStream();
        var result = await import.ImportSqliteAsync(stream, file.FileName, name, description, keepFile, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }

    /// <summary>Remplace le fichier d'une base existante et la resynchronise (descriptions conservées).</summary>
    [HttpPut("{id}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(long.MaxValue)]
    public async Task<IActionResult> Replace(string id, IFormFile file, [FromForm] bool keepFile = true,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0) return BadRequest("Aucun fichier n'a été envoyé.");
        await using var stream = file.OpenReadStream();
        var result = await import.ReplaceSqliteAsync(id, stream, keepFile, ct);
        return result == null ? NotFound() : Ok(ImportView.From(result));
    }

    /// <summary>
    ///     Aperçu du remplacement du fichier d'une base : ce qui serait ajouté, modifié ou supprimé (et la documentation
    ///     qui serait perdue). Ne modifie rien et ne conserve pas le fichier.
    /// </summary>
    [HttpPost("{id}/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(long.MaxValue)]
    public async Task<IActionResult> PreviewReplace(string id, IFormFile file, CancellationToken ct = default)
    {
        if (file == null || file.Length == 0) return BadRequest("Aucun fichier n'a été envoyé.");
        await using var stream = file.OpenReadStream();
        var preview = await import.PreviewReplaceSqliteAsync(id, stream, ct);
        return this.OkOrNotFound(preview);
    }

    /// <summary>Importe la base d'exemple livrée avec l'API (une boutique en ligne) pour découvrir sans rien préparer.</summary>
    [HttpPost("sample")]
    public async Task<IActionResult> Sample(CancellationToken ct)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "shop.sqlite");
        if (!System.IO.File.Exists(path)) return NotFound("La base d'exemple n'est pas installée sur ce serveur.");
        await using var stream = System.IO.File.OpenRead(path);
        var result = await import.ImportSqliteAsync(stream, "shop.sqlite", "Boutique (exemple)",
            "Base d'exemple : une boutique en ligne (catalogue, stock, clients, commandes, paiements, avis).", true, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }

    /// <summary>Documente un fichier SQLite présent sur le serveur (désactivé par défaut).</summary>
    [HttpPost("local")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Local([FromBody] LocalSqliteRequest request, CancellationToken ct)
    {
        var result = await import.ImportLocalSqliteAsync(request.Path, request.Name, request.Description, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }
}
