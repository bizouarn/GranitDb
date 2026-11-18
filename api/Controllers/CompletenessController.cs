using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Avancement de la documentation d'une table : elle-même et ses colonnes.</summary>
public record TableCompleteness(
    string TableId,
    string TableName,
    bool TableDescribed,
    int Columns,
    int ColumnsDescribed,
    int SensitiveColumns,
    int SensitiveUndescribed,
    int Percent);

/// <summary>Avancement de la documentation d'une base (ou d'un domaine) : éléments décrits / éléments à décrire.</summary>
public record CompletenessReport(
    string DatabaseId,
    int Tables,
    int TablesDescribed,
    int Columns,
    int ColumnsDescribed,
    int SensitiveColumns,
    int SensitiveUndescribed,
    int Percent,
    IReadOnlyList<TableCompleteness> PerTable);

/// <summary>
///     Indicateur de complétude : part des tables et colonnes qui ont une description (une description vide ou faite
///     d'espaces ne compte pas). Les colonnes sensibles sans description sont comptées à part : ce sont les premières
///     à documenter.
/// </summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
public class CompletenessController(ICatalogRepository repository) : ControllerBase
{
    /// <summary>Complétude d'une base, ou des seules tables du domaine <c>domainId</c>. 404 si la base n'existe pas.</summary>
    [HttpGet("database/{id}")]
    public async Task<IActionResult> OfDatabase(string id, [FromQuery] string? domainId, CancellationToken ct)
    {
        if (await repository.GetAsync<DatabaseInfo>(id, ct) == null) return NotFound();
        var byDatabase = new[] { new CatalogFilter("DatabaseId", id) };
        var tables = await repository.GetAllAsync<TableInfo>(byDatabase, ct);
        var columns = (await repository.GetAllAsync<ColumnInfo>(byDatabase, ct)).ToLookup(c => c.TableId);

        if (!string.IsNullOrWhiteSpace(domainId))
        {
            var members = (await repository.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.DomainId), domainId, ct))
                .Select(l => l.TableId).ToHashSet();
            tables = tables.Where(t => members.Contains(t.Id)).ToList();
        }

        return Ok(Calculate(id, tables, columns));
    }

    public static CompletenessReport Calculate(string databaseId, IEnumerable<TableInfo> tables,
        ILookup<string, ColumnInfo> columnsByTable)
    {
        var perTable = tables.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t =>
        {
            var columns = columnsByTable[t.Id].ToList();
            var described = columns.Count(c => Filled(c.Description));
            var sensitive = columns.Count(c => c.IsSensitive);
            var sensitiveBlank = columns.Count(c => c.IsSensitive && !Filled(c.Description));
            var tableDescribed = Filled(t.Description);
            return new TableCompleteness(t.Id, t.Name, tableDescribed, columns.Count, described, sensitive,
                sensitiveBlank, Percent((tableDescribed ? 1 : 0) + described, 1 + columns.Count));
        }).ToList();

        var done = perTable.Sum(t => (t.TableDescribed ? 1 : 0) + t.ColumnsDescribed);
        var total = perTable.Sum(t => 1 + t.Columns);
        return new CompletenessReport(databaseId, perTable.Count, perTable.Count(t => t.TableDescribed),
            perTable.Sum(t => t.Columns), perTable.Sum(t => t.ColumnsDescribed), perTable.Sum(t => t.SensitiveColumns),
            perTable.Sum(t => t.SensitiveUndescribed), Percent(done, total), perTable);
    }

    private static bool Filled(string? text)
    {
        return !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Pourcentage arrondi à l'entier le plus proche ; 0 quand il n'y a rien à décrire.</summary>
    private static int Percent(int done, int total)
    {
        return total == 0 ? 0 : (int)Math.Round(100.0 * done / total, MidpointRounding.AwayFromZero);
    }
}
