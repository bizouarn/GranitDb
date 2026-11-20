using GranitDB.API.Models;
using GranitDB.API.Security;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

/// <summary>Capacités activées sur ce serveur : l'interface propose seulement ce qui est permis.</summary>
public record CapabilitiesView(bool AllowSqlServerSources, bool AllowLocalPaths, bool AllowProfiling = false, bool AllowAi = false,
    string? AiModel = null);

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class CapabilitiesController(IOptions<CatalogOptions> options, IOptions<AiOptions> ai) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new CapabilitiesView(options.Value.AllowSqlServerSources, options.Value.AllowLocalPaths,
        options.Value.AllowProfiling, options.Value.AllowAi, options.Value.AllowAi ? ai.Value.Model : null));
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
    /// <summary>Cette route est celle de SQL Server (la route générique <c>import/server</c> choisit le moteur) : le moteur y est imposé.</summary>
    private static ServerConnectionInput Forced(ServerConnectionInput input)
    {
        input.Engine = GranitDB.API.Connectors.ServerEngines.SqlServer.Type;
        return input;
    }

    /// <summary>
    ///     Essaie la connexion sans rien enregistrer. Un échec de connexion revient en 200 avec <c>ok</c> faux et la
    ///     consigne à suivre ; une demande mal formée est un 400.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        return Ok(await import.TestServerAsync(Forced(input), ct));
    }

    /// <summary>Documente la base : crée l'entrée du catalogue, extrait le schéma et enregistre la connexion chiffrée.</summary>
    [HttpPost]
    public async Task<IActionResult> Import([FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ImportServerAsync(Forced(input), ct);
        return Created($"/DatabaseInfo/{result.Database.Id}", ImportView.From(result));
    }

    /// <summary>Remplace la connexion d'une base déjà documentée (mot de passe changé, autres schémas) et la resynchronise.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Replace(string id, [FromBody] ServerConnectionInput input, CancellationToken ct)
    {
        var result = await import.ReplaceServerConnectionAsync(id, Forced(input), ct);
        return result == null ? NotFound() : Ok(ImportView.From(result));
    }
}
