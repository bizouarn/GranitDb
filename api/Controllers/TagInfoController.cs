using System.ComponentModel.DataAnnotations;
using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Pose d'une étiquette (créée si elle n'existe pas) sur une base, une table ou une colonne.</summary>
public class AttachTagInput
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>database, table ou column.</summary>
    [Required, RegularExpression("^(database|table|column)$", ErrorMessage = "EntityType doit valoir database, table ou column.")]
    public string EntityType { get; set; } = "";

    [Required] public string EntityId { get; set; } = "";
}

/// <summary>Une étiquette posée sur un élément.</summary>
public record TagLinkView(string TagId, string TagName, string? Color, string EntityType, string EntityId);

/// <summary>
///     Étiquettes libres (ex : « RGPD », « legacy ») posées sur des bases, tables et colonnes. Les étiquettes sont
///     communes à toutes les bases ; supprimer une étiquette la retire de partout.
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class TagInfoController(ICatalogRepository repository) : BaseDbInfoController<TagInfo, TagInput>(repository)
{
    /// <summary>Pose l'étiquette (créée au besoin, sans tenir compte de la casse) ; sans effet si elle y est déjà.</summary>
    [HttpPost("attach")]
    public async Task<IActionResult> Attach([FromBody] AttachTagInput input, CancellationToken ct)
    {
        return await Repository.InTransactionAsync<IActionResult>(async repo =>
        {
            if (!await EntityExistsAsync(repo, input.EntityType, input.EntityId, ct))
                return NotFound();

            var name = input.Name.Trim();
            var tag = (await repo.FindAsync<TagInfo>(nameof(TagInfo.Name), name, ct)).FirstOrDefault()
                      ?? await repo.CreateAsync(new TagInfo { Name = name }, ct);
            var already = (await repo.FindAsync<TagLinkInfo>(nameof(TagLinkInfo.EntityId), input.EntityId, ct))
                .Any(l => l.TagId == tag.Id && l.EntityType == input.EntityType);
            if (!already)
                await repo.CreateAsync(new TagLinkInfo
                    { TagId = tag.Id, EntityType = input.EntityType, EntityId = input.EntityId }, ct);
            return Ok(tag);
        }, ct);
    }

    /// <summary>Retire l'étiquette de l'élément (l'étiquette existe toujours).</summary>
    [HttpDelete("{tagId}/links/{entityType}/{entityId}")]
    public async Task<IActionResult> Detach(string tagId, string entityType, string entityId, CancellationToken ct)
    {
        var link = (await Repository.FindAsync<TagLinkInfo>(nameof(TagLinkInfo.EntityId), entityId, ct))
            .FirstOrDefault(l => l.TagId == tagId && l.EntityType == entityType);
        if (link == null) return NotFound();
        await Repository.DeleteAsync<TagLinkInfo>(link.Id, ct);
        return NoContent();
    }

    /// <summary>Étiquettes d'un élément.</summary>
    [HttpGet("of/{entityType}/{entityId}")]
    public async Task<IActionResult> Of(string entityType, string entityId, CancellationToken ct)
    {
        var links = (await Repository.FindAsync<TagLinkInfo>(nameof(TagLinkInfo.EntityId), entityId, ct))
            .Where(l => l.EntityType == entityType).ToList();
        return Ok(await ViewsAsync(links, ct));
    }

    /// <summary>Toutes les étiquettes posées dans une base (la base, ses tables et ses colonnes). 404 si la base n'existe pas.</summary>
    [HttpGet("database/{databaseId}/links")]
    public async Task<IActionResult> OfDatabase(string databaseId, CancellationToken ct)
    {
        if (await Repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return NotFound();
        var byDatabase = new[] { new CatalogFilter("DatabaseId", databaseId) };
        var ids = (await Repository.GetAllAsync<TableInfo>(byDatabase, ct)).Select(t => t.Id)
            .Concat((await Repository.GetAllAsync<ColumnInfo>(byDatabase, ct)).Select(c => c.Id))
            .Append(databaseId).ToList();
        var links = new List<TagLinkInfo>();
        // Par lots : une liste IN trop longue dépasse la limite de paramètres des moteurs.
        foreach (var chunk in ids.Chunk(500))
            links.AddRange(await Repository.GetAllAsync<TagLinkInfo>([CatalogFilter.In("EntityId", chunk)], ct));
        return Ok(await ViewsAsync(links, ct));
    }

    private async Task<List<TagLinkView>> ViewsAsync(List<TagLinkInfo> links, CancellationToken ct)
    {
        if (links.Count == 0) return [];
        var tags = (await Repository.GetAllAsync<TagInfo>(ct)).ToDictionary(t => t.Id);
        return links.Where(l => tags.ContainsKey(l.TagId))
            .Select(l => new TagLinkView(l.TagId, tags[l.TagId].Name, tags[l.TagId].Color, l.EntityType, l.EntityId))
            .OrderBy(v => v.TagName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<bool> EntityExistsAsync(ICatalogRepository repo, string type, string id, CancellationToken ct)
    {
        return type switch
        {
            TagLinkInfo.Database => await repo.GetAsync<DatabaseInfo>(id, ct) != null,
            TagLinkInfo.Table => await repo.GetAsync<TableInfo>(id, ct) != null,
            TagLinkInfo.Column => await repo.GetAsync<ColumnInfo>(id, ct) != null,
            _ => throw new GranitExceptions("EntityType doit valoir database, table ou column.")
        };
    }
}
