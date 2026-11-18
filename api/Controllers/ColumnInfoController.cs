using GranitDB.API.Controllers.Base;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Corps du marquage d'une colonne comme sensible.</summary>
public class SensitivityInput
{
    public bool IsSensitive { get; set; }
}

[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class ColumnInfoController(ICatalogRepository repository)
    : BaseDbInfoController<ColumnInfo, ColumnInput>(repository)
{
    /// <summary>
    ///     Filtres : <c>databaseId</c>, <c>tableId</c>, <c>name</c> (contient), <c>dataType</c>, <c>isPrimaryKey</c>,
    ///     <c>isNullable</c>, <c>isSensitive</c> et <c>tag</c> (nom d'une étiquette).
    /// </summary>
    protected override IReadOnlyDictionary<string, (string Property, FilterMode Mode)> Filters { get; } =
        new Dictionary<string, (string, FilterMode)>(StringComparer.OrdinalIgnoreCase)
        {
            ["databaseId"] = ("DatabaseId", FilterMode.Equals),
            ["tableId"] = ("TableId", FilterMode.Equals),
            ["name"] = ("Name", FilterMode.Contains),
            ["dataType"] = ("DataType", FilterMode.Equals),
            ["isPrimaryKey"] = ("IsPrimaryKey", FilterMode.Bool),
            ["isNullable"] = ("IsNullable", FilterMode.Bool),
            ["isSensitive"] = ("IsSensitive", FilterMode.Bool)
        };

    protected override async Task<List<CatalogFilter>> ReadAllFiltersAsync(CancellationToken ct)
    {
        var filters = ReadFilters();
        var tag = Request.Query["tag"].ToString();
        if (!string.IsNullOrWhiteSpace(tag))
            filters.Add(await LinkFilters.ByTagAsync(Repository, TagLinkInfo.Column, tag, ct));
        return filters;
    }

    /// <summary>Marque (ou démarque) la colonne comme contenant des données sensibles ; le changement est historisé.</summary>
    [HttpPut("{id}/sensitivity")]
    public async Task<IActionResult> SetSensitivity(string id, [FromBody] SensitivityInput input, CancellationToken ct)
    {
        var updated = await Repository.ModifyAsync<ColumnInfo>(id, c => c.IsSensitive = input.IsSensitive, ct, Actor);
        return updated == null ? NotFound() : Ok(updated);
    }
}
