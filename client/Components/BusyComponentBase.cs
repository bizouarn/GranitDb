using GranitDb.Client.api;
using Microsoft.AspNetCore.Components;

namespace GranitDb.Client.Components;

/// <summary>
///     Une page ou un composant dont les actions passent une à une par <see cref="RunAsync" /> : le bouton se grise pendant l'appel
///     (<c>_busy</c>) et l'erreur du serveur, ou l'API qui ne répond pas, s'affiche à côté de l'action (<c>_error</c>).
/// </summary>
public abstract class BusyComponentBase : ComponentBase
{
    /// <summary>Dernière erreur à afficher ; effacée quand une action réussit.</summary>
    protected string? _error;

    /// <summary>Une action est en cours.</summary>
    protected bool _busy;

    protected async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
            _error = null;
        }
        catch (ApiException e)
        {
            _error = e.Message;
        }
        catch (HttpRequestException)
        {
            _error = "L'API ne répond pas.";
        }

        _busy = false;
    }
}
