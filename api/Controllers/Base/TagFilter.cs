using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;

namespace GranitDB.API.Controllers.Base;

/// <summary>Filtres qui passent par une table de liaison : étiquette et domaine.</summary>
internal static class LinkFilters
{
    /// <summary>Éléments (du type donné) qui portent l'étiquette de ce nom ; une étiquette inconnue ne donne rien.</summary>
    public static async Task<CatalogFilter> ByTagAsync(ICatalogRepository repository, string entityType, string tagName,
        CancellationToken ct)
    {
        var tags = await repository.FindAsync<TagInfo>(nameof(TagInfo.Name), tagName.Trim(), ct);
        if (tags.Count == 0) return CatalogFilter.In("Id", []);
        var links = await repository.GetAllAsync<TagLinkInfo>(
            [new CatalogFilter(nameof(TagLinkInfo.TagId), tags[0].Id), new CatalogFilter(nameof(TagLinkInfo.EntityType), entityType)], ct);
        return CatalogFilter.In("Id", links.Select(l => l.EntityId));
    }

    /// <summary>Tables qui appartiennent à ce domaine.</summary>
    public static async Task<CatalogFilter> ByDomainAsync(ICatalogRepository repository, string domainId,
        CancellationToken ct)
    {
        var links = await repository.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.DomainId), domainId, ct);
        return CatalogFilter.In("Id", links.Select(l => l.TableId));
    }
}
