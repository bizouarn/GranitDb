using System.ComponentModel.DataAnnotations;
using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Une table d'un domaine et sa place dans le diagramme du domaine (null tant qu'elle n'a pas été déplacée).</summary>
public record DomainTableView(string TableId, double? X, double? Y);

/// <summary>Un domaine avec les tables qui lui appartiennent.</summary>
public class DomainView
{
    public string Id { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Description { get; set; }
    public string? MetaInfo { get; set; }
    public bool ShowNeighbors { get; set; }
    public List<string> HiddenRelations { get; set; } = [];
    public List<DomainTableView> Tables { get; set; } = [];
}

/// <summary>Réglages d'affichage d'un domaine ; ce qui est absent n'est pas modifié.</summary>
public class DomainSettingsInput
{
    public bool? ShowNeighbors { get; set; }

    /// <summary>Remplace la liste des relations masquées (identifiants de relations).</summary>
    public List<string>? HiddenRelations { get; set; }
}

/// <summary>Place d'une table dans le diagramme d'un domaine.</summary>
public class PlacementInput
{
    [Range(-100000, 100000)] public double? X { get; set; }

    [Range(-100000, 100000)] public double? Y { get; set; }
}

public class LayoutItem
{
    [Required] public string TableId { get; set; } = "";

    [Range(-100000, 100000)] public double X { get; set; }

    [Range(-100000, 100000)] public double Y { get; set; }
}

/// <summary>
///     Domaines métier : un regroupement de tables d'une base (une table peut appartenir à plusieurs domaines) qui
///     permet des diagrammes lisibles par sujet. La place de chaque table dans le diagramme du domaine est mémorisée.
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class DomainInfoController(ICatalogRepository repository) : BaseDbInfoController<DomainInfo, DomainInput>(repository)
{
    /// <summary>Filtres : <c>databaseId</c>, <c>name</c> (contient).</summary>
    protected override IReadOnlyDictionary<string, (string Property, FilterMode Mode)> Filters { get; } =
        new Dictionary<string, (string, FilterMode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["databaseId"] = ("DatabaseId", FilterMode.Equals),
            ["name"] = ("Name", FilterMode.Contains)
        };

    /// <summary>Un domaine ne change pas de base : ses tables n'y appartiendraient plus.</summary>
    [HttpPut("{id}")]
    public override async Task<IActionResult> UpdateT(string id, [FromBody] DomainInput input, CancellationToken ct)
    {
        var existing = await Repository.GetAsync<DomainInfo>(id, ct);
        if (existing != null && existing.DatabaseId != input.DatabaseId)
            throw new GranitExceptions("Un domaine ne peut pas changer de base.");
        return await base.UpdateT(id, input, ct);
    }

    /// <summary>Les domaines d'une base avec leurs tables et leurs places, triés par nom. 404 si la base n'existe pas.</summary>
    [HttpGet("database/{databaseId}")]
    public async Task<IActionResult> OfDatabase(string databaseId, CancellationToken ct)
    {
        if (await Repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return NotFound();
        var domains = await Repository.GetAllAsync<DomainInfo>([new CatalogFilter("DatabaseId", databaseId)], ct);
        var ids = domains.Select(d => d.Id).ToList();
        var links = (await Repository.GetAllAsync<DomainTableInfo>([CatalogFilter.In("DomainId", ids)], ct))
            .ToLookup(l => l.DomainId);
        var hidden = (await Repository.GetAllAsync<DomainHiddenRelationInfo>([CatalogFilter.In("DomainId", ids)], ct))
            .ToLookup(h => h.DomainId);
        return Ok(domains.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => ToView(d, links[d.Id], hidden[d.Id])).ToList());
    }

    /// <summary>Règle ce que le diagramme du domaine montre : tables voisines, relations masquées.</summary>
    [HttpPut("{id}/settings")]
    public async Task<IActionResult> Settings(string id, [FromBody] DomainSettingsInput input, CancellationToken ct)
    {
        return await Repository.InTransactionAsync<IActionResult>(async repo =>
        {
            var domain = await repo.GetAsync<DomainInfo>(id, ct);
            if (domain == null) return NotFound();
            if (input.ShowNeighbors != null && input.ShowNeighbors != domain.ShowNeighbors)
            {
                domain.ShowNeighbors = input.ShowNeighbors.Value;
                await repo.UpdateAsync(id, domain, ct);
            }

            if (input.HiddenRelations != null)
            {
                var wanted = input.HiddenRelations.Where(r => !string.IsNullOrWhiteSpace(r)).ToHashSet();
                var current = await repo.FindAsync<DomainHiddenRelationInfo>(nameof(DomainHiddenRelationInfo.DomainId), id, ct);
                foreach (var old in current.Where(h => !wanted.Contains(h.RelationId)))
                    await repo.DeleteAsync<DomainHiddenRelationInfo>(old.Id, ct);
                var kept = current.Select(h => h.RelationId).ToHashSet();
                foreach (var relationId in wanted.Where(r => !kept.Contains(r)))
                {
                    var relation = await repo.GetAsync<RelationInfo>(relationId, ct);
                    if (relation == null) throw new GranitExceptions($"La relation {relationId} n'existe pas.");
                    await repo.CreateAsync(new DomainHiddenRelationInfo { DomainId = id, RelationId = relationId }, ct);
                }
            }

            return Ok(await ViewAsync(repo, domain, ct));
        }, ct);
    }

    /// <summary>
    ///     Ajoute la table au domaine (sans effet si elle y est déjà) et mémorise sa place si elle est donnée.
    ///     <c>moveFrom</c> : retire en même temps la table de cet autre domaine (déplacement d'un domaine à l'autre).
    /// </summary>
    [HttpPut("{id}/tables/{tableId}")]
    public async Task<IActionResult> AddTable(string id, string tableId, [FromQuery] string? moveFrom,
        [FromBody] PlacementInput? placement, CancellationToken ct)
    {
        return await Repository.InTransactionAsync<IActionResult>(async repo =>
        {
            var domain = await repo.GetAsync<DomainInfo>(id, ct);
            var table = await repo.GetAsync<TableInfo>(tableId, ct);
            if (domain == null || table == null) return NotFound();
            if (table.DatabaseId != domain.DatabaseId)
                throw new GranitExceptions("La table n'appartient pas à la base de ce domaine.");

            var links = await repo.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.TableId), tableId, ct);
            var link = links.FirstOrDefault(l => l.DomainId == id);
            if (link == null)
            {
                await repo.CreateAsync(new DomainTableInfo
                    { DomainId = id, TableId = tableId, X = placement?.X, Y = placement?.Y }, ct);
            }
            else if (placement is { X: not null, Y: not null })
            {
                link.X = placement.X;
                link.Y = placement.Y;
                await repo.UpdateAsync(link.Id, link, ct);
            }

            if (!string.IsNullOrEmpty(moveFrom) && moveFrom != id)
                foreach (var old in links.Where(l => l.DomainId == moveFrom))
                    await repo.DeleteAsync<DomainTableInfo>(old.Id, ct);

            return Ok(await ViewAsync(repo, domain, ct));
        }, ct);
    }

    /// <summary>Retire la table du domaine (la table elle-même n'est pas touchée).</summary>
    [HttpDelete("{id}/tables/{tableId}")]
    public async Task<IActionResult> RemoveTable(string id, string tableId, CancellationToken ct)
    {
        var link = (await Repository.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.TableId), tableId, ct))
            .FirstOrDefault(l => l.DomainId == id);
        if (link == null) return NotFound();
        await Repository.DeleteAsync<DomainTableInfo>(link.Id, ct);
        return NoContent();
    }

    /// <summary>Mémorise d'un coup la place de plusieurs tables du domaine (toutes ou aucune).</summary>
    [HttpPut("{id}/layout")]
    public async Task<IActionResult> SaveLayout(string id, [FromBody] List<LayoutItem> items, CancellationToken ct)
    {
        return await Repository.InTransactionAsync<IActionResult>(async repo =>
        {
            var domain = await repo.GetAsync<DomainInfo>(id, ct);
            if (domain == null) return NotFound();
            var links = (await repo.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.DomainId), id, ct))
                .ToDictionary(l => l.TableId);
            foreach (var item in items)
            {
                if (!links.TryGetValue(item.TableId, out var link))
                    throw new GranitExceptions($"La table {item.TableId} n'appartient pas à ce domaine.");
                link.X = item.X;
                link.Y = item.Y;
                await repo.UpdateAsync(link.Id, link, ct);
            }

            return Ok(await ViewAsync(repo, domain, ct));
        }, ct);
    }

    private static async Task<DomainView> ViewAsync(ICatalogRepository repository, DomainInfo domain, CancellationToken ct)
    {
        return ToView(domain, await repository.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.DomainId), domain.Id, ct),
            await repository.FindAsync<DomainHiddenRelationInfo>(nameof(DomainHiddenRelationInfo.DomainId), domain.Id, ct));
    }

    private static DomainView ToView(DomainInfo domain, IEnumerable<DomainTableInfo> links,
        IEnumerable<DomainHiddenRelationInfo> hidden)
    {
        return new DomainView
        {
            Id = domain.Id, DatabaseId = domain.DatabaseId, Name = domain.Name, Color = domain.Color,
            Description = domain.Description, MetaInfo = domain.MetaInfo, ShowNeighbors = domain.ShowNeighbors,
            HiddenRelations = hidden.Select(h => h.RelationId).OrderBy(r => r, StringComparer.Ordinal).ToList(),
            Tables = links.OrderBy(l => l.TableId, StringComparer.Ordinal)
                .Select(l => new DomainTableView(l.TableId, l.X, l.Y)).ToList()
        };
    }
}
