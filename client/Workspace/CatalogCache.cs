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
    private readonly Dictionary<string, Task<DomainView[]>> _domains = [];
    private readonly Dictionary<string, Task<ColumnInfo[]>> _columns = [];

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

    public Task<DomainView[]> GetDomainsAsync(string databaseId)
    {
        lock (_domains)
        {
            if (!_domains.TryGetValue(databaseId, out var task))
                _domains[databaseId] = task = _api.GetDomainsAsync(databaseId);
            return task;
        }
    }

    /// <summary>
    ///     Donne à une table du résumé ses colonnes (lues une fois, à la demande) ; sans effet si elle les a déjà.
    ///     Évite de charger les colonnes des centaines de tables d'une grosse base pour n'en montrer que quelques-unes.
    /// </summary>
    public async Task EnsureColumnsAsync(TableInfo table)
    {
        if (table.Columns.Count > 0) return;
        Task<ColumnInfo[]> task;
        lock (_columns)
        {
            if (!_columns.TryGetValue(table.Id, out task!))
                _columns[table.Id] = task = _api.GetColumnsAsync(table.Id);
        }

        table.Columns = (await task).OrderBy(c => c.Position).ToList();
    }

    private async Task<TableInfo[]> LoadTablesAsync(string databaseId)
    {
        return (await _api.GetTableSummariesAsync(databaseId)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Oublie ce qui est connu d'une base (de toutes si <c>null</c>) : la prochaine lecture sera fraîche.</summary>
    public void Invalidate(string? databaseId)
    {
        lock (_tables)
        {
            if (databaseId == null) _tables.Clear();
            else _tables.Remove(databaseId);
        }

        lock (_columns)
        {
            _columns.Clear();
        }

        lock (_domains)
        {
            if (databaseId == null) _domains.Clear();
            else _domains.Remove(databaseId);
        }
    }

    private void OnCatalogChanged(string? databaseId, object? source) => Invalidate(databaseId);
}
