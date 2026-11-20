using System.Text.Json;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>État du modèle d'IA et essai : réservés aux administrateurs (ils montrent la configuration et font un appel).</summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class AiController(IAiProvider ai) : ControllerBase
{
    private const string TestSchema = """{"type":"object","properties":{"ok":{"type":"boolean"},"mot":{"type":"string"}},"required":["ok","mot"],"additionalProperties":false}""";

    /// <summary>Le modèle configuré est-il activé, le serveur joignable, le modèle installé (ou en cours de téléchargement) ?</summary>
    [HttpGet("status")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await ai.StatusAsync(ct));

    /// <summary>Un appel minimal avec réponse JSON contrainte : durée et jetons, sans rien envoyer du catalogue.</summary>
    [HttpPost("test")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        var response = await ai.CompleteAsync(new AiRequest(
            "Tu es un test de connexion.", "Renvoie l'objet {\"ok\": true, \"mot\": \"bonjour\"}.", TestSchema,
            e => e.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "le champ ok manque", 64), ct);
        return Ok(new AiTestView(response.Model, response.Content, response.Json != null, response.ElapsedMs, response.PromptTokens, response.CompletionTokens));
    }
}
