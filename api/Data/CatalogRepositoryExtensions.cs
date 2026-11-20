using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Data;

public static class CatalogRepositoryExtensions
{
    /// <summary>Tous les éléments d'un type qui appartiennent à une base.</summary>
    public static Task<IReadOnlyList<T>> GetByDatabaseAsync<T>(this ICatalogRepository repository, string databaseId,
        CancellationToken ct = default) where T : InfoBase =>
        repository.GetAllAsync<T>(CatalogFilter.ForDatabase(databaseId), ct);
}
