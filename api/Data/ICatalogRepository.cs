using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Data;

/// <summary>Accès au catalogue (bases, tables, colonnes, relations), indépendant du moteur.</summary>
public interface ICatalogRepository
{
    /// <summary>Crée le schéma du catalogue s'il n'existe pas (dossier et fichier SQLite compris).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    ///     Exécute <paramref name="work" /> dans une seule transaction : tout est validé si elle se termine, tout est
    ///     annulé si elle lève. Le dépôt reçu doit être utilisé à la place de celui-ci. Imbriquable.
    /// </summary>
    Task<TResult> InTransactionAsync<TResult>(Func<ICatalogRepository, Task<TResult>> work,
        CancellationToken ct = default);

    Task<T?> GetAsync<T>(string id, CancellationToken ct = default) where T : InfoBase;

    /// <summary>Relations dont au moins une colonne appartient à la base donnée.</summary>
    Task<IReadOnlyList<GranitDB.API.Models.Descriptions.RelationInfo>> GetRelationsOfDatabaseAsync(
        string databaseId, CancellationToken ct = default);

    Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken ct = default) where T : InfoBase;

    /// <summary>Une page, triée par nom (sinon par identifiant) pour un ordre stable.</summary>
    Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take, CancellationToken ct = default) where T : InfoBase;

    Task<int> CountAsync<T>(CancellationToken ct = default) where T : InfoBase;

    /// <summary>Comme <see cref="GetAllAsync{T}(CancellationToken)" />, restreint par des filtres (cumulés par ET).</summary>
    Task<IReadOnlyList<T>> GetAllAsync<T>(IReadOnlyList<CatalogFilter> filters, CancellationToken ct = default)
        where T : InfoBase;

    /// <summary>Comme <see cref="GetPageAsync{T}(int,int,CancellationToken)" />, restreint par des filtres.</summary>
    Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take, IReadOnlyList<CatalogFilter> filters,
        CancellationToken ct = default) where T : InfoBase;

    /// <summary>Nombre d'éléments qui satisfont les filtres (le total d'une page filtrée).</summary>
    Task<int> CountAsync<T>(IReadOnlyList<CatalogFilter> filters, CancellationToken ct = default) where T : InfoBase;

    /// <summary>
    ///     Modifie une entité : <paramref name="apply" /> reçoit l'entité lue, puis elle est enregistrée. Chaque
    ///     changement de <c>Description</c> ou de <c>MetaInfo</c> est ajouté à l'historique, dans la même transaction.
    ///     <paramref name="changedBy" /> (identité de l'appelant, si elle est connue) est inscrit dans l'historique.
    ///     Renvoie l'entité modifiée, ou <c>null</c> si elle n'existe pas.
    /// </summary>
    Task<T?> ModifyAsync<T>(string id, Action<T> apply, CancellationToken ct = default, string? changedBy = null)
        where T : InfoBase;

    /// <summary>Ajoute une ligne à l'historique (ex : « Deleted » à la suppression d'une base, qui n'est pas une modification de texte).</summary>
    Task RecordHistoryAsync<T>(string entityId, string field, string? oldValue, string? newValue,
        string? changedBy = null, CancellationToken ct = default) where T : InfoBase;

    /// <summary>Historique des modifications, du plus récent au plus ancien, éventuellement d'une seule entité.</summary>
    Task<IReadOnlyList<GranitDB.API.Models.HistoryEntry>> GetHistoryAsync(string? entityId, int skip, int take,
        CancellationToken ct = default);

    Task<int> CountHistoryAsync(string? entityId, CancellationToken ct = default);

    /// <summary>
    ///     Cherche <paramref name="term" /> (sans tenir compte de la casse) dans les noms et descriptions des bases,
    ///     tables, colonnes et index, et dans les colonnes des index. Au plus <paramref name="limitPerKind" /> résultats
    ///     par type, triés par nom. Les caractères <c>%</c>, <c>_</c> et <c>!</c> du terme sont littéraux.
    /// </summary>
    Task<IReadOnlyList<GranitDB.API.Models.SearchHit>> SearchAsync(string term, int limitPerKind,
        CancellationToken ct = default);

    /// <summary>Filtre côté SQL sur une propriété de l'entité (ex : "DatabaseId").</summary>
    Task<IReadOnlyList<T>> FindAsync<T>(string property, string value, CancellationToken ct = default)
        where T : InfoBase;

    /// <summary>Insère l'entité ; génère un Id GUID s'il est vide.</summary>
    Task<T> CreateAsync<T>(T info, CancellationToken ct = default) where T : InfoBase;

    Task<bool> UpdateAsync<T>(string id, T info, CancellationToken ct = default) where T : InfoBase;

    /// <summary>Supprime l'entité et ses dépendances (tables, colonnes, relations).</summary>
    Task<bool> DeleteAsync<T>(string id, CancellationToken ct = default) where T : InfoBase;
}
