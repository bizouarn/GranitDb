using System.ComponentModel.DataAnnotations;
using GranitDB.API.Security;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

public class WebhookInput
{
    [Required] [StringLength(2000)] public string Url { get; set; } = "";

    /// <summary>Secret qui signe chaque envoi. Absent à la modification : inchangé ; chaîne vide : supprimé.</summary>
    [StringLength(200)] public string? Secret { get; set; }

    /// <summary>Base surveillée ; absente = toutes les bases.</summary>
    public string? DatabaseId { get; set; }

    /// <summary>Événements voulus (par défaut <c>schema.changed</c>).</summary>
    public List<string>? Events { get; set; }

    public bool? IsActive { get; set; }

    [StringLength(500)] public string? Description { get; set; }
}

/// <summary>Un webhook tel que l'API le montre : le secret n'y est jamais, seulement le fait qu'il existe.</summary>
public record WebhookView(string Id, string? DatabaseId, string Url, IReadOnlyList<string> Events, bool IsActive,
    bool HasSecret, string? Description, DateTime CreatedAt)
{
    public static WebhookView From(WebhookInfo hook) => new(hook.Id, hook.DatabaseId, hook.Url,
        hook.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), hook.IsActive,
        !string.IsNullOrEmpty(hook.Secret), hook.Description, hook.CreatedAt);
}

/// <summary>
///     Webhooks : des URL appelées en POST (JSON, signé si un secret est défini) quand un événement survient, ici un
///     changement de schéma détecté par une resynchronisation.
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
[MinimumRole(Role.Admin)]
public class WebhooksController(WebhookStore store, WebhookDispatcher dispatcher, ICatalogRepository repository,
    ISecretProtector protector, IOptions<WebhookOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await store.ListAsync(ct)).Select(WebhookView.From));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await store.GetAsync(id, ct) is { } hook ? Ok(WebhookView.From(hook)) : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] WebhookInput input, CancellationToken ct)
    {
        var hook = new WebhookInfo { Id = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow };
        if (await Apply(hook, input, ct) is { } problem) return problem;
        await store.SaveAsync(hook, true, ct);
        return Created($"{Request.Path.Value!.TrimEnd('/')}/{hook.Id}", WebhookView.From(hook));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] WebhookInput input, CancellationToken ct)
    {
        var hook = await store.GetAsync(id, ct);
        if (hook == null) return NotFound();
        if (await Apply(hook, input, ct) is { } problem) return problem;
        await store.SaveAsync(hook, false, ct);
        return Ok(WebhookView.From(hook));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct) =>
        await store.DeleteAsync(id, ct) ? NoContent() : NotFound();

    /// <summary>Envoie un événement <c>ping</c> maintenant et renvoie le résultat (code reçu, erreur éventuelle).</summary>
    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(string id, CancellationToken ct)
    {
        var hook = await store.GetAsync(id, ct);
        if (hook == null) return NotFound();
        return Ok(await dispatcher.DeliverAsync(hook, WebhookEvents.Ping, new { message = "Envoi d'essai depuis GranitDb" }, ct));
    }

    /// <summary>Derniers envois (les plus récents d'abord) : réussite, code reçu, nombre d'essais, erreur.</summary>
    [HttpGet("{id}/deliveries")]
    public async Task<IActionResult> Deliveries(string id, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) == null) return NotFound();
        return Ok(await store.DeliveriesAsync(id, Math.Clamp(take, 1, 200), ct));
    }

    /// <summary>Reporte l'entrée sur le webhook ; renvoie une réponse d'erreur si elle est refusée.</summary>
    private async Task<IActionResult?> Apply(WebhookInfo hook, WebhookInput input, CancellationToken ct)
    {
        if (WebhookUrlPolicy.Validate(input.Url?.Trim(), options.Value.AllowPrivateHosts) is { } refused) return BadRequest(refused);

        var events = (input.Events is { Count: > 0 } ? input.Events : [WebhookEvents.SchemaChanged])
            .Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        if (events.FirstOrDefault(e => !WebhookEvents.Subscribable.Contains(e)) is { } unknown)
            return BadRequest($"Événement inconnu : {unknown} (disponibles : {string.Join(", ", WebhookEvents.Subscribable)}).");

        var databaseId = string.IsNullOrWhiteSpace(input.DatabaseId) ? null : input.DatabaseId.Trim();
        if (databaseId != null && await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null)
            return BadRequest("Base inconnue.");

        hook.Url = input.Url!.Trim();
        hook.DatabaseId = databaseId;
        hook.Events = string.Join(',', events);
        hook.IsActive = input.IsActive ?? hook.IsActive;
        hook.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (input.Secret != null) hook.Secret = input.Secret.Length == 0 ? null : protector.Protect(input.Secret);
        return null;
    }
}
