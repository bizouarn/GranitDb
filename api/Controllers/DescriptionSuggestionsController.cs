using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>
///     File de relecture de l'assistant de documentation : propositions de descriptions (modèle d'IA) et de colonnes sensibles (règles).
///     Lire et relire demande le rôle éditeur ; rien n'est écrit dans le catalogue avant « accepter ».
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class DescriptionSuggestionsController(DescriptionAssistant assistant) : ControllerBase
{
    private string? Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;

    /// <summary>Les propositions d'une base (<c>status</c> : pending par défaut, accepted ou rejected).</summary>
    [HttpGet("database/{databaseId}")]
    public async Task<IActionResult> List(string databaseId, [FromQuery] string? status, CancellationToken ct)
    {
        var list = await assistant.ListAsync(databaseId, status, ct);
        return list == null ? NotFound() : Ok(list);
    }

    /// <summary>
    ///     Lance en arrière-plan la documentation des éléments sans description (base, tables, colonnes, relations). Demande
    ///     <c>Catalog:AllowAi</c> ; une seule à la fois par base.
    /// </summary>
    [HttpPost("database/{databaseId}/generate")]
    public async Task<IActionResult> Generate(string databaseId, CancellationToken ct)
    {
        var job = await assistant.StartJobAsync(databaseId, ct);
        return job == null ? NotFound() : Ok(job);
    }

    /// <summary>Avancement de la dernière documentation en lot de la base (404 si aucune n'a été lancée depuis le démarrage).</summary>
    [HttpGet("database/{databaseId}/job")]
    public IActionResult Job(string databaseId)
    {
        var job = assistant.JobOf(databaseId);
        return job == null ? NotFound() : Ok(job);
    }

    /// <summary>Arrête la documentation en lot ; les propositions déjà faites restent en file.</summary>
    [HttpPost("database/{databaseId}/job/cancel")]
    public IActionResult CancelJob(string databaseId)
    {
        if (!assistant.CancelJob(databaseId)) return NotFound();
        return Ok(assistant.JobOf(databaseId));
    }

    /// <summary>Propose la description d'un seul élément (bouton « Suggérer »), à accepter ensuite. Jamais pour un élément déjà décrit.</summary>
    [HttpPost("database/{databaseId}/suggest")]
    public async Task<IActionResult> Suggest(string databaseId, [FromBody] SuggestInput input, CancellationToken ct)
    {
        var suggestion = await assistant.SuggestAsync(databaseId, input.TargetType, input.TargetId, ct);
        return suggestion == null ? NotFound() : Ok(suggestion);
    }

    /// <summary>Propose, par des règles de nommage et sans modèle, les colonnes qui semblent sensibles ; renvoie le nombre de propositions.</summary>
    [HttpPost("database/{databaseId}/detect-sensitive")]
    public async Task<IActionResult> DetectSensitive(string databaseId, CancellationToken ct)
    {
        var count = await assistant.DetectSensitiveAsync(databaseId, ct);
        return count == null ? NotFound() : Ok(new AcceptBatchView(count.Value, 0));
    }

    /// <summary>Accepte une proposition (texte retouché facultatif) : écrit la description ou marque la colonne sensible.</summary>
    [HttpPost("database/{databaseId}/{id}/accept")]
    public async Task<IActionResult> Accept(string databaseId, string id, [FromBody] AcceptSuggestionInput? input, CancellationToken ct)
    {
        var accepted = await assistant.AcceptAsync(databaseId, id, input?.Text, Actor, ct);
        return accepted == null ? NotFound() : Ok(accepted);
    }

    /// <summary>Refuse une proposition : la documentation en lot ne la repropose plus.</summary>
    [HttpPost("database/{databaseId}/{id}/reject")]
    public async Task<IActionResult> Reject(string databaseId, string id, CancellationToken ct)
    {
        var rejected = await assistant.RejectAsync(databaseId, id, ct);
        return rejected == null ? NotFound() : Ok(new AcceptBatchView(0, 1));
    }

    /// <summary>Accepte d'un coup les propositions de description sûres (non incertaines) ; les incertaines restent à relire.</summary>
    [HttpPost("database/{databaseId}/accept-confident")]
    public async Task<IActionResult> AcceptConfident(string databaseId, CancellationToken ct)
    {
        var batch = await assistant.AcceptConfidentAsync(databaseId, Actor, ct);
        return batch == null ? NotFound() : Ok(batch);
    }
}
