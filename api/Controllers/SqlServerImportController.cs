using GranitDB.API.Models;
using GranitDB.API.Security;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

/// <summary>Capacités activées sur ce serveur : l'interface propose seulement ce qui est permis.</summary>
public record CapabilitiesView(bool AllowSqlServerSources, bool AllowLocalPaths);

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class CapabilitiesController(IOptions<CatalogOptions> options) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new CapabilitiesView(options.Value.AllowSqlServerSources, options.Value.AllowLocalPaths));
}

/// <summary>
///     Documente une base Microsoft SQL Server. Activé par défaut, désactivable (<c>Catalog:AllowSqlServerSources</c>) : l'API se
///     connecte à un serveur désigné par l'appelant. Seul le schéma est lu ; la connexion est enregistrée chiffrée et ne
///     ressort jamais d'une réponse.
/// </summary>
[Route("api/v1/import/sqlserver")]
[Route("import/sqlserver")]
[ApiController]
[MinimumRole(Role.Admin)]
public class SqlServerImportController(DatabaseImportService import) : ControllerBase
{
    /// <summary>
    ///     Essaie la connexion sans rien enregistrer. Un échec de connexion revient en 200 avec <c>ok</c> faux et la
    ///     consigne à suivre ; une demande mal formée est un 400.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] SqlServerConnectionInput input, CancellationToken ct)
    {
        return Ok(await import.TestSqlServerAsync(input, ct));
    }

    /// <summary>Documente la base : crée l'entrée du catalogue, extrait le schéma et enregistre la connexion chiffrée.</summary>
    [HttpPost]
    public async Task<IActionResult> Import([FromBody] SqlServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ImportSqlServerAsync(input, ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }

    /// <summary>Remplace la connexion d'une base déjà documentée (mot de passe changé, autres schémas) et la resynchronise.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] SqlServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ReplaceSqlServerConnectionAsync(id, input, ct);
        return result == null ? NotFound() : Ok(ImportView.From(result));
    }
}
