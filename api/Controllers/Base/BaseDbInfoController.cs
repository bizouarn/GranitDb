using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions.Base;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers.Base;

/// <summary>
///     CRUD d'une entité du catalogue. Les corps de POST/PUT sont des <typeparamref name="TInput" /> (validés,
///     distincts de l'entité). Les listes acceptent la pagination (<c>skip</c>/<c>take</c>) et les filtres de
///     <see cref="Filters" /> en paramètres de requête.
/// </summary>
public abstract class BaseDbInfoController<T, TInput>(ICatalogRepository repository) : ControllerBase
    where T : InfoBase, new()
    where TInput : InfoInput<T>
{
    protected ICatalogRepository Repository { get; } = repository;

    /// <summary>Identité de l'appelant quand l'API en connaît une (inscrite dans l'historique) ; sinon <c>null</c>.</summary>
    protected string? Actor => User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : null;

    /// <summary>
    ///     Ce que l'API renvoie d'une entité. Par défaut l'entité elle-même ; une entité qui porte des détails du
    ///     serveur ou des secrets (la base) renvoie une vue qui les omet.
    /// </summary>
    protected virtual object View(T entity) => entity;

    /// <summary>Paramètres de requête acceptés pour filtrer la liste : nom du paramètre → propriété et mode.</summary>
    protected virtual IReadOnlyDictionary<string, (string Property, FilterMode Mode)> Filters { get; } =
        new Dictionary<string, (string, FilterMode)>(StringComparer.OrdinalIgnoreCase)
            { ["name"] = ("Name", FilterMode.Contains) };

    /// <summary>Filtres présents dans la requête (les valeurs vides sont ignorées).</summary>
    protected List<CatalogFilter> ReadFilters()
    {
        var result = new List<CatalogFilter>();
        foreach (var (parameter, (property, mode)) in Filters)
        {
            var value = Request.Query[parameter].ToString();
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(new CatalogFilter(property, value, mode));
        }

        return result;
    }

    /// <summary>Tous les filtres de la requête, y compris ceux qui demandent une lecture du catalogue (domaine, étiquette).</summary>
    protected virtual Task<List<CatalogFilter>> ReadAllFiltersAsync(CancellationToken ct)
    {
        return Task.FromResult(ReadFilters());
    }

    /// <summary>
    ///     Sans <c>skip</c>/<c>take</c> : tout. Avec (take de 1 à 1000, 50 par défaut) : une page, et le total dans
    ///     l'en-tête <c>X-Total-Count</c>. Les filtres s'appliquent dans les deux cas.
    /// </summary>
    [HttpGet]
    public virtual async Task<IActionResult> GetAllT([FromQuery] int? skip, [FromQuery] int? take, CancellationToken ct)
    {
        var filters = await ReadAllFiltersAsync(ct);
        if (skip == null && take == null)
            return Ok((await Repository.GetAllAsync<T>(filters, ct)).Select(View));

        var offset = skip ?? 0;
        var size = take ?? 50;
        if (offset < 0 || size is < 1 or > 1000)
            return BadRequest("skip doit être >= 0 et take compris entre 1 et 1000.");
        Response.Headers["X-Total-Count"] = (await Repository.CountAsync<T>(filters, ct)).ToString();
        return Ok((await Repository.GetPageAsync<T>(offset, size, filters, ct)).Select(View));
    }

    [HttpGet("{id}")]
    public virtual async Task<IActionResult> GetT(string id, CancellationToken ct)
    {
        var info = await Repository.GetAsync<T>(id, ct);
        if (info == null)
            return NotFound();
        return Ok(View(info));
    }

    [HttpPost]
    public virtual async Task<IActionResult> CreateT([FromBody] TInput input, CancellationToken ct)
    {
        var info = await Repository.CreateAsync(input.ToEntity(), ct);
        return CreatedAtAction(nameof(GetT), new { id = info.GetId() }, View(info));
    }

    [HttpPut("{id}")]
    public virtual async Task<IActionResult> UpdateT(string id, [FromBody] TInput input, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(input.Id) && id != input.Id)
            return BadRequest("ID mismatch");

        var updated = await Repository.ModifyAsync<T>(id, input.Apply, ct, Actor);
        return updated == null ? NotFound() : Ok(View(updated));
    }

    /// <summary>Modifie la description et les métadonnées d'un élément, sans toucher à ses autres champs (historisé).</summary>
    [HttpPut("{id}/documentation")]
    public virtual async Task<IActionResult> Document(string id, [FromBody] DocumentationInput input, CancellationToken ct)
    {
        var updated = await Repository.ModifyAsync<T>(id, entity => ApplyDocumentation(entity, input), ct, Actor);
        return updated == null ? NotFound() : Ok(View(updated));
    }

    protected virtual void ApplyDocumentation(T entity, DocumentationInput input)
    {
        entity.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        entity.MetaInfo = string.IsNullOrWhiteSpace(input.MetaInfo) ? null : input.MetaInfo.Trim();
    }

    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> DeleteT(string id, CancellationToken ct)
    {
        if (!await Repository.DeleteAsync<T>(id, ct))
            return NotFound();
        return NoContent();
    }
}
