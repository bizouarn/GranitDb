using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("[controller]")]
[ApiController]
public class UploadSqlLiteController : Controller
{
    [HttpPost]
    public async Task<IActionResult> CreateDatabase(IFormFile file, [FromBody] DatabaseInfo database)
    {
        if (file == null || file.Length == 0) return BadRequest("Aucun fichier n'a été envoyé.");
        database = await InfoBase.CreateAsync(database);
        CreatedAtAction(nameof(CreateDatabase), new { id = database.Id }, database);
        return await UpdateDatabase(database.Id, file);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDatabase(string id, IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("Aucun fichier n'a été envoyé.");
        const string dir = "DB";
        var path = Path.Join(dir, $"{id}.db");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await using var stream = new FileStream(path, FileMode.Create);
        await file.CopyToAsync(stream);

        // Logique pour gérer le fichier et mettre à jour la base de données


        return Ok("Base de données mise à jour avec succès.");
    }
}