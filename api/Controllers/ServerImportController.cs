using GranitDB.API.Models;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>
///     Documente une base client/serveur de n'importe quel moteur pris en charge (<c>engine</c> : <c>SqlServer</c>,
///     <c>PostgreSql</c>, <c>MySql</c>). Mêmes règles que <c>import/sqlserver</c> : activé par <c>Catalog:AllowSqlServerSources</c>,
///     seul le schéma est lu, la connexion est enregistrée chiffrée et ne ressort jamais d'une réponse.
/// </summary>
[Route("api/v1/import/server")]
[Route("import/server")]
[ApiController]
[MinimumRole(Role.Admin)]
public class ServerImportController(DatabaseImportService import) : ControllerBase
{
    /// <summary>Essaie la connexion sans rien enregistrer (un échec de connexion revient en 200 avec <c>ok</c> faux et la consigne à suivre).</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        return Ok(await import.TestServerAsync(input, ct));
    }

    /// <summary>Documente la base : crée l'entrée du catalogue, extrait le schéma et enregistre la connexion chiffrée.</summary>
    [HttpPost]
    public async Task<IActionResult> Import([FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ImportServerAsync(input, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }

    /// <summary>Remplace la connexion d'une base déjà documentée (même moteur) et la resynchronise.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ReplaceServerConnectionAsync(id, input, ct);
        return result == null ? NotFound() : Ok(ImportView.From(result));
    }
}
