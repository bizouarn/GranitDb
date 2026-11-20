using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Un élément touché : une table, ou une colonne (et sa table).</summary>
public record ImpactNode(string TableId, string TableName, string? ColumnId, string? ColumnName, int Depth);

/// <summary>Résultat d'une analyse d'impact : ce qui dépend de l'élément et ce dont il dépend.</summary>
public record ImpactResult(
    ImpactNode Root,
    IReadOnlyList<ImpactNode> Dependents,
    IReadOnlyList<ImpactNode> Dependencies);

/// <summary>
///     Graphe de dépendances : « que casse la modification de cette colonne / table ? ». Se fonde sur les relations du
///     catalogue (clés étrangères détectées et relations manuelles), dans la base de l'élément.
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class ImpactController(ICatalogRepository repository) : ControllerBase
{
    /// <summary>Colonnes qui référencent la colonne (en chaîne), et colonnes qu'elle référence.</summary>
    [HttpGet("column/{id}")]
    public async Task<IActionResult> OfColumn(string id, CancellationToken ct)
    {
        var column = await repository.GetAsync<ColumnInfo>(id, ct);
        if (column == null) return NotFound();
        var (columns, tables, relations) = await LoadAsync(column.DatabaseId, ct);

        ImpactNode Node(string columnId, int depth)
        {
            var c = columns[columnId];
            return new ImpactNode(c.TableId, tables.TryGetValue(c.TableId, out var t) ? t.Name : "", c.Id, c.Name,
                depth);
        }

        // Une relation multi-colonnes compte pour chacune de ses paires.
        var edges = relations.SelectMany(r => r.Pairs())
            .Where(p => columns.ContainsKey(p.First) && columns.ContainsKey(p.Second)).Distinct().ToList();
        return Ok(new ImpactResult(Node(id, 0),
            ImpactAnalyzer.Dependents(id, edges).Select(s => Node(s.Key, s.Depth)).ToList(),
            ImpactAnalyzer.Dependencies(id, edges).Select(s => Node(s.Key, s.Depth)).ToList()));
    }

    /// <summary>Tables qui référencent la table (en chaîne), et tables qu'elle référence.</summary>
    [HttpGet("table/{id}")]
    public async Task<IActionResult> OfTable(string id, CancellationToken ct)
    {
        var table = await repository.GetAsync<TableInfo>(id, ct);
        if (table == null) return NotFound();
        var (columns, tables, relations) = await LoadAsync(table.DatabaseId, ct);

        ImpactNode Node(string tableId, int depth)
        {
            return new ImpactNode(tableId, tables[tableId].Name, null, null, depth);
        }

        // Une relation entre deux colonnes devient une arête entre leurs tables ; les auto-références sont ignorées.
        var edges = relations.SelectMany(r => r.Pairs())
            .Where(p => columns.ContainsKey(p.First) && columns.ContainsKey(p.Second))
            .Select(p => (columns[p.First].TableId, columns[p.Second].TableId))
            .Where(e => e.Item1 != e.Item2 && tables.ContainsKey(e.Item1) && tables.ContainsKey(e.Item2))
            .Distinct().ToList();
        return Ok(new ImpactResult(Node(id, 0),
            ImpactAnalyzer.Dependents(id, edges).Select(s => Node(s.Key, s.Depth)).ToList(),
            ImpactAnalyzer.Dependencies(id, edges).Select(s => Node(s.Key, s.Depth)).ToList()));
    }

    private async Task<(Dictionary<string, ColumnInfo> Columns, Dictionary<string, TableInfo> Tables,
        IReadOnlyList<RelationInfo> Relations)> LoadAsync(string databaseId, CancellationToken ct)
    {
        var byDatabase = CatalogFilter.ForDatabase(databaseId);
        var columns = (await repository.GetAllAsync<ColumnInfo>(byDatabase, ct)).ToDictionary(c => c.Id);
        var tables = (await repository.GetAllAsync<TableInfo>(byDatabase, ct)).ToDictionary(t => t.Id);
        return (columns, tables, await repository.GetRelationsOfDatabaseAsync(databaseId, ct));
    }
}
