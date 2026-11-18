using System.ComponentModel.DataAnnotations;
using GranitDB.API.Data;
using GranitDB.API.Models.Descriptions;
using Microsoft.AspNetCore.Mvc;

namespace GranitDB.API.Controllers;

/// <summary>Documentation d'une colonne, retrouvée par son nom dans la table.</summary>
public class DocColumnInput
{
    [Required] public string Name { get; set; } = "";

    [StringLength(4000)] public string? Description { get; set; }
}

public class DocTableInput
{
    [Required] public string Name { get; set; } = "";

    [StringLength(4000)] public string? Description { get; set; }

    public List<DocColumnInput> Columns { get; set; } = [];
}

/// <summary>Domaine métier et les tables (par nom) qui le composent.</summary>
public class DocDomainInput
{
    [Required] public string Name { get; set; } = "";

    public string? Description { get; set; }

    public List<string> Tables { get; set; } = [];

    /// <summary>Place des tables dans le diagramme du domaine (reprise d'un plan existant).</summary>
    public List<DocPlacement> Layout { get; set; } = [];
}

public class DocPlacement
{
    [Required] public string Table { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>Relation documentée : <c>Column</c> de <c>Table</c> référence <c>RefColumn</c> de <c>RefTable</c>.</summary>
public class DocRelationInput
{
    [Required] public string Table { get; set; } = "";
    [Required] public string Column { get; set; } = "";
    [Required] public string RefTable { get; set; } = "";
    [Required] public string RefColumn { get; set; } = "";
    public string? RelationType { get; set; }
    [StringLength(4000)] public string? Description { get; set; }
}

/// <summary>Documentation existante (export d'un autre outil, document officiel) à rattacher à une base du catalogue.</summary>
public class DocumentationImportInput
{
    public List<DocTableInput> Tables { get; set; } = [];
    public List<DocDomainInput> Domains { get; set; } = [];
    public List<DocRelationInput> Relations { get; set; } = [];

    /// <summary>Remplace aussi les descriptions déjà saisies (par défaut seules les descriptions vides sont complétées).</summary>
    public bool Overwrite { get; set; }

    /// <summary>Compte rendu seulement : rien n'est écrit.</summary>
    public bool DryRun { get; set; }
}

public record DocumentationImportResult(
    int TablesDescribed, int ColumnsDescribed, int TablesNotFound, int ColumnsNotFound, int DomainsCreated,
    int DomainLinksCreated, int PlacementsSet, int RelationsCreated, int RelationsSkipped, IReadOnlyList<string> Missing);

/// <summary>
///     Complète la documentation d'une base à partir d'une documentation externe, en rapprochant tables et colonnes
///     par leur nom. N'écrit que dans le catalogue ; chaque description modifiée passe par l'historique.
/// </summary>
[Route("api/v1/import/documentation")]
[Route("import/documentation")]
[ApiController]
public class DocumentationImportController(ICatalogRepository repository) : ControllerBase
{
    /// <summary>Marque les relations posées par cet import (ce ne sont pas des clés étrangères détectées).</summary>
    public const string RelationMarker = "doc:import";

    private const string Actor = "import:documentation";

    [HttpPost("{databaseId}")]
    public async Task<IActionResult> Import(string databaseId, [FromBody] DocumentationImportInput input,
        CancellationToken ct)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return NotFound();

        var result = await repository.InTransactionAsync(repo => ApplyAsync(repo, databaseId, input, ct), ct);
        return Ok(result);
    }

    private async Task<DocumentationImportResult> ApplyAsync(ICatalogRepository repo, string databaseId,
        DocumentationImportInput input, CancellationToken ct)
    {
        var tables = (await repo.FindAsync<TableInfo>(nameof(TableInfo.DatabaseId), databaseId, ct))
            .ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var columns = (await repo.FindAsync<ColumnInfo>(nameof(ColumnInfo.DatabaseId), databaseId, ct))
            .GroupBy(c => c.TableId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase));

        var missing = new List<string>();
        int tablesDone = 0, columnsDone = 0, tablesMissing = 0, columnsMissing = 0;

        bool Wanted(string? current, string? incoming) =>
            !string.IsNullOrWhiteSpace(incoming) && (input.Overwrite || string.IsNullOrWhiteSpace(current));

        foreach (var doc in input.Tables)
        {
            if (!tables.TryGetValue(doc.Name, out var table))
            {
                tablesMissing++;
                missing.Add($"table {doc.Name}");
                continue;
            }

            var text = doc.Description?.Trim();
            if (Wanted(table.Description, text))
            {
                tablesDone++;
                if (!input.DryRun) await repo.ModifyAsync<TableInfo>(table.Id, t => t.Description = text, ct, Actor);
            }

            columns.TryGetValue(table.Id, out var byName);
            foreach (var docColumn in doc.Columns)
            {
                if (byName == null || !byName.TryGetValue(docColumn.Name, out var column))
                {
                    columnsMissing++;
                    missing.Add($"colonne {doc.Name}.{docColumn.Name}");
                    continue;
                }

                var columnText = docColumn.Description?.Trim();
                if (!Wanted(column.Description, columnText)) continue;
                columnsDone++;
                if (!input.DryRun)
                    await repo.ModifyAsync<ColumnInfo>(column.Id, c => c.Description = columnText, ct, Actor);
            }
        }

        // Domaines : créés au besoin (par nom), les tables y sont rattachées une seule fois.
        var domains = (await repo.FindAsync<DomainInfo>(nameof(DomainInfo.DatabaseId), databaseId, ct))
            .ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var links = (await repo.GetAllAsync<DomainTableInfo>(ct)).ToDictionary(l => (l.DomainId, l.TableId));
        int domainsCreated = 0, linksCreated = 0, placed = 0;
        foreach (var doc in input.Domains)
        {
            if (!domains.TryGetValue(doc.Name, out var domain))
            {
                domainsCreated++;
                if (input.DryRun) continue;
                domain = await repo.CreateAsync(new DomainInfo
                    { DatabaseId = databaseId, Name = doc.Name, Description = doc.Description }, ct);
                domains[doc.Name] = domain;
            }

            foreach (var tableName in doc.Tables)
            {
                if (!tables.TryGetValue(tableName, out var table)) continue;
                if (links.ContainsKey((domain.Id, table.Id))) continue;
                linksCreated++;
                if (input.DryRun) continue;
                links[(domain.Id, table.Id)] =
                    await repo.CreateAsync(new DomainTableInfo { DomainId = domain.Id, TableId = table.Id }, ct);
            }

            // Une place déjà choisie dans l'interface n'est remplacée qu'avec Overwrite.
            foreach (var place in doc.Layout)
            {
                if (input.DryRun || !tables.TryGetValue(place.Table, out var table) ||
                    !links.TryGetValue((domain.Id, table.Id), out var link)) continue;
                if (!input.Overwrite && link.X != null && link.Y != null) continue;
                link.X = place.X;
                link.Y = place.Y;
                await repo.UpdateAsync(link.Id, link, ct);
                placed++;
            }
        }

        // Relations : manuelles (jamais effacées par la synchronisation), sans doublon dans un sens ou dans l'autre.
        var pairs = (await repo.GetRelationsOfDatabaseAsync(databaseId, ct))
            .Select(r => (r.FirstId, r.SecondId)).ToHashSet();
        int relationsDone = 0, relationsSkipped = 0;
        foreach (var doc in input.Relations)
        {
            var first = Find(tables, columns, doc.RefTable, doc.RefColumn);
            var second = Find(tables, columns, doc.Table, doc.Column);
            if (first == null || second == null || first.Id == second.Id ||
                pairs.Contains((first.Id, second.Id)) || pairs.Contains((second.Id, first.Id)))
            {
                relationsSkipped++;
                continue;
            }

            relationsDone++;
            pairs.Add((first.Id, second.Id));
            if (input.DryRun) continue;
            await repo.CreateAsync(new RelationInfo
            {
                FirstId = first.Id, SecondId = second.Id,
                RelationType = string.IsNullOrWhiteSpace(doc.RelationType) ? "One-to-Many" : doc.RelationType,
                Description = doc.Description, MetaInfo = RelationMarker
            }, ct);
        }

        return new DocumentationImportResult(tablesDone, columnsDone, tablesMissing, columnsMissing, domainsCreated,
            linksCreated, placed, relationsDone, relationsSkipped, missing.Take(200).ToList());
    }

    private static ColumnInfo? Find(Dictionary<string, TableInfo> tables,
        Dictionary<string, Dictionary<string, ColumnInfo>> columns, string table, string column)
    {
        return tables.TryGetValue(table, out var t) && columns.TryGetValue(t.Id, out var byName) &&
               byName.TryGetValue(column, out var c)
            ? c
            : null;
    }
}
