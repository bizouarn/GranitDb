using GranitDb.Client.api;
using GranitDb.Client.Models;
using Microsoft.AspNetCore.Components;

namespace GranitDb.Client.Components;

/// <summary>
///     Une page « de fonction » d'une base (<c>/databases/{Id}/…</c>) : elle charge les capacités du serveur et le nom de la base
///     (pour le fil d'Ariane), puis ses propres données par <see cref="LoadAsync" />, à chaque changement de base. Une erreur de
///     lecture s'affiche en haut de la page.
/// </summary>
public abstract class DatabasePageBase : BusyComponentBase
{
    [Inject] protected GranitDb.Client.api.Client Api { get; set; } = null!;

    [Parameter] public string Id { get; set; } = "";

    protected string? _databaseName;

    protected CapabilitiesView _capabilities = new() { AllowSqlServerSources = false, AllowLocalPaths = false };

    protected bool _loading = true;

    /// <summary>Lit les données propres à la page (la base <see cref="Id" /> et les capacités sont déjà chargées).</summary>
    protected abstract Task LoadAsync();

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        await ApiErrors.TryAsync(async () =>
        {
            _capabilities = await Api.GetCapabilitiesAsync();
            _databaseName = (await Api.GetDatabaseAsync(Id))?.Name;
            await LoadAsync();
            _error = null;
        }, message => _error = message);

        _loading = false;
    }
}
