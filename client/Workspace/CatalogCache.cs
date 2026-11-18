using GranitDb.Client.Models;
using ApiClient = GranitDb.Client.api.Client;

namespace GranitDb.Client.Workspace;

/// <summary>
///     Tables et domaines des bases déjà ouvertes dans l'explorateur ou le panneau de détail : une lecture par base,
///     jusqu'à ce que <see cref="WorkspaceState.NotifyCatalogChanged" /> la périme.
/// </summary>
public sealed class CatalogCache : IDisposable
{
    private readonly ApiClient _api;
    private readonly WorkspaceState _workspace;
    private readonly Dictionary<string, Task<TableInfo[]>> _tables = [];
    private readonly Dictionary<string, Task<DomainInfo[]>> _domains = [];

    public CatalogCache(ApiClient api, WorkspaceState workspace)
    {
        _api = api;
        _workspace = workspace;
        _workspace.CatalogChanged += OnCatalogChanged;
    }

    public void Dispose()
    {
        _workspace.CatalogChanged -= OnCatalogChanged;
    }

    public Task<TableInfo[]> GetTablesAsync(string databaseId)
    {
        lock (_tables)
        {
            if (!_tables.TryGetValue(databaseId, out var task))
                _tables[databaseId] = task = LoadTablesAsync(databaseId);
            return task;
        }
    }

    public Task<DomainInfo[]> GetDomainsAsync(string databaseId)
    {
        lock (_domains)
        {
            if (!_domains.TryGetValue(databaseId, out var task))
                _domains[databaseId] = task = _api.GetDomainsAsync(databaseId);
            return task;
        }
    }

    private async Task<TableInfo[]> LoadTablesAsync(string databaseId)
    {
        return (await _api.GetTablesAsync(databaseId)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Oublie ce qui est connu d'une base (de toutes si <c>null</c>) : la prochaine lecture sera fraîche.</summary>
    public void Invalidate(string? databaseId)
    {
        lock (_tables)
        {
            if (databaseId == null) _tables.Clear();
            else _tables.Remove(databaseId);
        }

        lock (_domains)
        {
            if (databaseId == null) _domains.Clear();
            else _domains.Remove(databaseId);
        }
    }

    private void OnCatalogChanged(string? databaseId, object? source) => Invalidate(databaseId);
}
