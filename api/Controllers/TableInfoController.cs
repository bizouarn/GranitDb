using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class TableInfoController(ICatalogRepository repository) : BaseDbInfoController<TableInfo, TableInput>(repository)
{
    /// <summary>
    ///     Filtres : <c>databaseId</c>, <c>name</c> (contient), <c>tableType</c> (Table, View, Virtual), <c>domainId</c>
    ///     (tables d'un domaine) et <c>tag</c> (nom d'une étiquette).
    /// </summary>
    protected override IReadOnlyDictionary<string, (string Property, FilterMode Mode)> Filters { get; } =
        new Dictionary<string, (string, FilterMode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["databaseId"] = ("DatabaseId", FilterMode.Equals),
            ["name"] = ("Name", FilterMode.Contains),
            ["tableType"] = ("TableType", FilterMode.Equals)
        };

    protected override async Task<List<CatalogFilter>> ReadAllFiltersAsync(CancellationToken ct)
    {
        var filters = ReadFilters();
        var domainId = Request.Query["domainId"].ToString();
        if (!string.IsNullOrWhiteSpace(domainId))
            filters.Add(await LinkFilters.ByDomainAsync(Repository, domainId, ct));
        var tag = Request.Query["tag"].ToString();
        if (!string.IsNullOrWhiteSpace(tag))
            filters.Add(await LinkFilters.ByTagAsync(Repository, TagLinkInfo.Table, tag, ct));
        return filters;
    }

    /// <summary>Une table avec ses colonnes et les relations qui les touchent.</summary>
    [HttpGet("{id}")]
    public override async Task<IActionResult> GetT(string id, CancellationToken ct)
    {
        var table = await Repository.GetAsync<TableInfo>(id, ct);
        if (table == null)
            return NotFound();
        await FillAsync([table], ct);
        return Ok(table);
    }

    /// <summary>
    ///     Les tables d'une base, avec colonnes et relations. Filtres <c>name</c> et <c>tableType</c> ; pagination
    ///     facultative (<c>skip</c>/<c>take</c>, total dans <c>X-Total-Count</c>). 404 si la base n'a aucune table.
    /// </summary>
    [HttpGet("database/{databaseId}")]
    public async Task<IActionResult> GetTablesByDatabaseId(string databaseId, [FromQuery] int? skip,
        [FromQuery] int? take, CancellationToken ct)
    {
        var filters = (await ReadAllFiltersAsync(ct)).Where(f => f.Property != nameof(TableInfo.DatabaseId)).ToList();
        filters.Add(new CatalogFilter(nameof(TableInfo.DatabaseId), databaseId));

        List<TableInfo> tables;
        if (skip == null && take == null)
        {
            tables = (await Repository.GetPageAsync<TableInfo>(0, int.MaxValue, filters, ct)).ToList();
        }
        else
        {
            var offset = skip ?? 0;
            var size = take ?? 50;
            if (offset < 0 || size is < 1 or > 1000)
                return BadRequest("skip doit être >= 0 et take compris entre 1 et 1000.");
            Response.Headers["X-Total-Count"] = (await Repository.CountAsync<TableInfo>(filters, ct)).ToString();
            tables = (await Repository.GetPageAsync<TableInfo>(offset, size, filters, ct)).ToList();
        }

        // Sans filtre ni page, une base sans table est « introuvable » ; une page vide au-delà de la fin reste vide.
        if (tables.Count == 0 && skip is null or 0 && filters.Count == 1)
            return NotFound();
        await FillAsync(tables, ct);
        return Ok(tables);
    }

    /// <summary>Colonnes d'une table, dans l'ordre de la table.</summary>
    [HttpGet("{id}/columns")]
    public async Task<IActionResult> GetColumns(string id, CancellationToken ct)
    {
        if (await Repository.GetAsync<TableInfo>(id, ct) == null)
            return NotFound();
        var columns = await Repository.GetAllAsync<ColumnInfo>([new CatalogFilter("TableId", id)], ct);
        return Ok(columns.OrderBy(c => c.Position).ThenBy(c => c.Name));
    }

    /// <summary>Index d'une table.</summary>
    [HttpGet("{id}/indexes")]
    public async Task<IActionResult> GetIndexes(string id, CancellationToken ct)
    {
        if (await Repository.GetAsync<TableInfo>(id, ct) == null)
            return NotFound();
        var indexes = await Repository.GetAllAsync<IndexInfo>([new CatalogFilter("TableId", id)], ct);
        return Ok(indexes.OrderBy(i => i.Name));
    }

    /// <summary>Relations dont une colonne de la table est l'une des extrémités.</summary>
    [HttpGet("{id}/relations")]
    public async Task<IActionResult> GetRelations(string id, CancellationToken ct)
    {
        var table = await Repository.GetAsync<TableInfo>(id, ct);
        if (table == null)
            return NotFound();
        await FillAsync([table], ct);
        return Ok(table.Relations);
    }

    private async Task FillAsync(List<TableInfo> tables, CancellationToken ct)
    {
        var databaseIds = tables.Select(t => t.DatabaseId).Distinct().ToList();
        var columns = new List<ColumnInfo>();
        var relations = new List<RelationInfo>();
        var indexes = new List<IndexInfo>();
        foreach (var databaseId in databaseIds)
        {
            var byDatabase = new[] { new CatalogFilter(nameof(ColumnInfo.DatabaseId), databaseId) };
            columns.AddRange(await Repository.GetAllAsync<ColumnInfo>(byDatabase, ct));
            relations.AddRange(await Repository.GetRelationsOfDatabaseAsync(databaseId, ct));
            indexes.AddRange(await Repository.GetAllAsync<IndexInfo>(byDatabase, ct));
        }

        var byTable = columns.ToLookup(c => c.TableId);
        var indexesByTable = indexes.ToLookup(i => i.TableId);
        foreach (var table in tables)
        {
            table.Columns = byTable[table.Id].OrderBy(c => c.Position).ToList();
            table.Indexes = indexesByTable[table.Id].OrderBy(i => i.Name).ToList();
            var ids = table.Columns.Select(c => c.Id).ToHashSet();
            table.Relations = relations.Where(r => ids.Contains(r.FirstId) || ids.Contains(r.SecondId)).ToList();
        }
    }
}
