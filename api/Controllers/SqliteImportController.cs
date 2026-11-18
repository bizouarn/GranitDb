using GranitDB.API.Models;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

public class LocalSqliteRequest
{
    public string Path { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>Documente une base SQLite à partir d'un fichier, sans serveur de base de données.</summary>
[Route("api/v1/import/sqlite")]
[Route("import/sqlite")]
[ApiController]
public class SqliteImportController(DatabaseImportService import) : ControllerBase
{
    /// <summary>Envoie un fichier SQLite (multipart) : crée la base et extrait tables, colonnes et relations.</summary>
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

    /// <summary>Documente un fichier SQLite présent sur le serveur (désactivé par défaut).</summary>
    [HttpPost("local")]
    public async Task<IActionResult> Local([FromBody] LocalSqliteRequest request, CancellationToken ct)
    {
        var result = await import.ImportLocalSqliteAsync(request.Path, request.Name, request.Description, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }
}
