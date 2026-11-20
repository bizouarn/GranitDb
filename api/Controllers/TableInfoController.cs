using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Security;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class TableInfoController(ICatalogRepository repository, ColumnProfilingService profiling) : BaseDbInfoController<TableInfo, TableInput>(repository)
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
    ///     facultative (<c>skip</c>/<c>take</c>, total dans <c>X-Total-Count</c>) ; <c>summary=true</c> : sans colonnes ni relations. 404 si la base n'a aucune table.
    /// </summary>
    [HttpGet("database/{databaseId}")]
    public async Task<IActionResult> GetTablesByDatabaseId(string databaseId, [FromQuery] int? skip,
        [FromQuery] int? take, [FromQuery] bool summary, CancellationToken ct)
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
        // Résumé : noms et types seulement, sans colonnes, index ni relations (explorateur, panneau de détail) ; les colonnes
        // d'une table se lisent à l'ouverture (<c>{id}/columns</c>).
        if (!summary) await FillAsync(tables, ct);
        return Ok(tables);
    }

    /// <summary>
    ///     Profil enregistré des colonnes d'une table (lignes, nuls, valeurs distinctes : des comptes, jamais de valeur) ; vide tant
    ///     qu'elle n'a pas été profilée.
    /// </summary>
    [HttpGet("{id}/profile")]
    public async Task<IActionResult> GetProfile(string id, CancellationToken ct)
    {
        var profile = await profiling.GetAsync(id, ct);
        return this.OkOrNotFound(profile);
    }

    /// <summary>
    ///     Profile la table à la source (lecture seule) et remplace son profil. Demande <c>Catalog:AllowProfiling</c> et le rôle
    ///     administrateur : c'est la seule route qui lit des données de la source.
    /// </summary>
    [HttpPost("{id}/profile")]
    [MinimumRole(Role.Admin)]
    public async Task<IActionResult> Profile(string id, CancellationToken ct)
    {
        var profile = await profiling.ProfileTableAsync(id, ct);
        return this.OkOrNotFound(profile);
    }

    /// <summary>
    ///     Documente une table qui n'existe pas dans la source (fichier plat, service, table prévue) : la table et ses colonnes
    ///     sont créées d'un coup (tout ou rien) et la synchronisation ne la supprime pas. Un nom déjà pris est refusé.
    /// </summary>
    [HttpPost("manual")]
    public async Task<IActionResult> CreateManual([FromBody] ManualTableInput input, CancellationToken ct)
    {
        var name = input.Name.Trim();
        var created = await Repository.InTransactionAsync(async repo =>
        {
            if (await repo.GetAsync<DatabaseInfo>(input.DatabaseId, ct) == null)
                throw new GranitExceptions("La base n'existe pas.");
            var existing = await repo.FindAsync<TableInfo>(nameof(TableInfo.DatabaseId), input.DatabaseId, ct);
            if (existing.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new GranitExceptions($"Une table nommée {name} existe déjà dans cette base.");
            ValidateColumns(input.Columns, []);
            var table = await repo.CreateAsync(new TableInfo
            {
                Name = name, DatabaseId = input.DatabaseId, TableType = input.TableType, IsManual = true,
                Description = input.Description
            }, ct);
            await AddColumnsAsync(repo, table, input.Columns, 0, ct);
            if (!string.IsNullOrEmpty(input.DomainId))
            {
                var domain = await repo.GetAsync<DomainInfo>(input.DomainId, ct);
                if (domain == null || domain.DatabaseId != input.DatabaseId)
                    throw new GranitExceptions("Le domaine n'existe pas dans cette base.");
                await repo.CreateAsync(new DomainTableInfo { DomainId = domain.Id, TableId = table.Id }, ct);
            }

            return table;
        }, ct);
        await FillAsync([created], ct);
        return CreatedAtAction(nameof(GetT), new { id = created.Id }, created);
    }

    /// <summary>Ajoute des colonnes à une table saisie à la main (une table venue de la source reçoit ses colonnes d'elle).</summary>
    [HttpPost("{id}/columns")]
    public async Task<IActionResult> AddColumns(string id, [FromBody] ManualColumnsInput input, CancellationToken ct)
    {
        var table = await Repository.InTransactionAsync<TableInfo?>(async repo =>
        {
            var t = await repo.GetAsync<TableInfo>(id, ct);
            if (t == null) return null;
            if (!t.IsManual)
                throw new GranitExceptions("Les colonnes de cette table viennent de la source : on ne les saisit pas à la main.");
            var existing = await repo.FindAsync<ColumnInfo>(nameof(ColumnInfo.TableId), id, ct);
            ValidateColumns(input.Columns, existing.Select(c => c.Name));
            await AddColumnsAsync(repo, t, input.Columns, existing.Count == 0 ? 0 : existing.Max(c => c.Position) + 1, ct);
            return t;
        }, ct);
        if (table == null) return NotFound();
        await FillAsync([table], ct);
        return Ok(table);
    }

    private static void ValidateColumns(IEnumerable<ManualColumnInput> columns, IEnumerable<string> already)
    {
        var seen = already.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var name = column.Name.Trim();
            if (name.Length == 0) throw new GranitExceptions("Une colonne n'a pas de nom.");
            if (!seen.Add(name)) throw new GranitExceptions($"La colonne {name} est saisie deux fois (ou existe déjà).");
        }
    }

    private static async Task AddColumnsAsync(ICatalogRepository repo, TableInfo table, IEnumerable<ManualColumnInput> columns,
        int firstPosition, CancellationToken ct)
    {
        var position = firstPosition;
        foreach (var column in columns)
            await repo.CreateAsync(new ColumnInfo
            {
                DatabaseId = table.DatabaseId, TableId = table.Id, Name = column.Name.Trim(), DataType = column.DataType.Trim(),
                IsNullable = column.IsNullable && !column.IsPrimaryKey, IsPrimaryKey = column.IsPrimaryKey,
                IsSensitive = column.IsSensitive, Description = column.Description, Position = position++
            }, ct);
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
