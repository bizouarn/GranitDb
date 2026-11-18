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
        return await base.CreateT(input, ct);
    }

    [HttpPut("{id}")]
    public override async Task<IActionResult> UpdateT(string id, [FromBody] RelationInput input, CancellationToken ct)
    {
        await CheckColumnsAsync(input, id, ct);
        return await base.UpdateT(id, input, ct);
    }

    private async Task CheckColumnsAsync(RelationInput input, string? ownId, CancellationToken ct)
    {
        var first = await Repository.GetAsync<ColumnInfo>(input.FirstId, ct);
        var second = await Repository.GetAsync<ColumnInfo>(input.SecondId, ct);
        if (first == null || second == null)
            throw new GranitExceptions("Une des colonnes de la relation n'existe pas.");
        if (first.DatabaseId != second.DatabaseId)
            throw new GranitExceptions("Les deux colonnes d'une relation doivent appartenir à la même base.");

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
