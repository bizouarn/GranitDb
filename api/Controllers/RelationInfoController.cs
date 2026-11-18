using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class RelationInfoController(ICatalogRepository repository)
    : BaseDbInfoController<RelationInfo, RelationInput>(repository)
{
    /// <summary>Filtres : <c>firstId</c> (colonne référencée), <c>secondId</c> (colonne qui référence), <c>relationType</c>.</summary>
    protected override IReadOnlyDictionary<string, (string Property, FilterMode Mode)> Filters { get; } =
        new Dictionary<string, (string, FilterMode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["firstId"] = ("FirstId", FilterMode.Equals),
            ["secondId"] = ("SecondId", FilterMode.Equals),
            ["relationType"] = ("RelationType", FilterMode.Equals)
        };

    /// <summary>
    ///     Crée une relation manuelle. Les deux colonnes doivent exister, appartenir à la même base et ne pas être déjà
    ///     reliées (dans un sens ou dans l'autre) ; la synchronisation ne touche jamais aux relations manuelles.
    /// </summary>
    [HttpPost]
    public override async Task<IActionResult> CreateT([FromBody] RelationInput input, CancellationToken ct)
    {
        await CheckColumnsAsync(input, null, ct);
        var result = await base.CreateT(input, ct);
        if (result is CreatedAtActionResult { Value: RelationInfo created } && input.ExtraColumns is { Count: > 0 })
            created.ExtraColumns = await SaveExtrasAsync(created.Id, input.ExtraColumns, ct);
        return result;
    }

    [HttpGet("{id}")]
    public override async Task<IActionResult> GetT(string id, CancellationToken ct)
    {
        var relation = await Repository.GetAsync<RelationInfo>(id, ct);
        if (relation == null) return NotFound();
        relation.ExtraColumns = (await Repository.FindAsync<RelationColumnInfo>(nameof(RelationColumnInfo.RelationId), id, ct))
            .OrderBy(e => e.Position).Select(e => new RelationColumnPair(e.FirstId, e.SecondId)).ToList();
        return Ok(relation);
    }

    /// <summary>Remplace les paires supplémentaires de la relation (rang 1, 2…).</summary>
    private async Task<List<RelationColumnPair>> SaveExtrasAsync(string relationId, List<RelationColumnPair> pairs,
        CancellationToken ct)
    {
        foreach (var old in await Repository.FindAsync<RelationColumnInfo>(nameof(RelationColumnInfo.RelationId), relationId, ct))
            await Repository.DeleteAsync<RelationColumnInfo>(old.Id, ct);
        for (var i = 0; i < pairs.Count; i++)
            await Repository.CreateAsync(new RelationColumnInfo
                { RelationId = relationId, FirstId = pairs[i].FirstId, SecondId = pairs[i].SecondId, Position = i + 1 }, ct);
        return pairs;
    }

    [HttpPut("{id}")]
    public override async Task<IActionResult> UpdateT(string id, [FromBody] RelationInput input, CancellationToken ct)
    {
        await CheckColumnsAsync(input, id, ct);
        var result = await base.UpdateT(id, input, ct);
        if (result is OkObjectResult { Value: RelationInfo updated } && input.ExtraColumns != null)
            updated.ExtraColumns = await SaveExtrasAsync(id, input.ExtraColumns, ct);
        return result;
    }

    private async Task CheckColumnsAsync(RelationInput input, string? ownId, CancellationToken ct)
    {
        var first = await Repository.GetAsync<ColumnInfo>(input.FirstId, ct);
        var second = await Repository.GetAsync<ColumnInfo>(input.SecondId, ct);
        if (first == null || second == null)
            throw new GranitExceptions("Une des colonnes de la relation n'existe pas.");
        if (first.DatabaseId != second.DatabaseId)
            throw new GranitExceptions("Les deux colonnes d'une relation doivent appartenir à la même base.");

        // Relation multi-colonnes : toutes les colonnes existent, dans la même base ; côté « référencé » comme côté
        // « qui référence », elles appartiennent à une seule table.
        var pairs = input.ExtraColumns ?? [];
        var seen = new HashSet<(string, string)> { (input.FirstId, input.SecondId) };
        foreach (var pair in pairs)
        {
            var f = await Repository.GetAsync<ColumnInfo>(pair.FirstId, ct);
            var s = await Repository.GetAsync<ColumnInfo>(pair.SecondId, ct);
            if (f == null || s == null)
                throw new GranitExceptions("Une des colonnes de la relation n'existe pas.");
            if (f.DatabaseId != first.DatabaseId || s.DatabaseId != first.DatabaseId)
                throw new GranitExceptions("Les deux colonnes d'une relation doivent appartenir à la même base.");
            if (f.TableId != first.TableId || s.TableId != second.TableId)
                throw new GranitExceptions("Les colonnes d'une relation multi-colonnes doivent appartenir à la même table de chaque côté.");
            if (f.Id == s.Id || !seen.Add((pair.FirstId, pair.SecondId)))
                throw new GranitExceptions("Une paire de colonnes figure deux fois dans la relation.");
        }

        var existing = await Repository.GetRelationsOfDatabaseAsync(first.DatabaseId, ct);
        if (existing.Any(r => r.Id != ownId &&
                              ((r.FirstId == input.FirstId && r.SecondId == input.SecondId) ||
                               (r.FirstId == input.SecondId && r.SecondId == input.FirstId))))
            throw new GranitExceptions("Ces deux colonnes sont déjà reliées.");
    }

    protected override void ApplyDocumentation(RelationInfo entity, DocumentationInput input)
    {
        if (input.MetaInfo?.Trim() == RelationInfo.AutoMarker && entity.MetaInfo != RelationInfo.AutoMarker)
            throw new GranitExceptions($"MetaInfo ne peut pas valoir {RelationInfo.AutoMarker} (réservé à la synchronisation).");
        var marker = entity.MetaInfo == RelationInfo.AutoMarker;
        base.ApplyDocumentation(entity, input);
        if (marker) entity.MetaInfo = RelationInfo.AutoMarker;
    }
}
