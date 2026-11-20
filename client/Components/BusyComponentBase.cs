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

    /// <summary>
    ///     Exécute l'action. Une erreur d'API va à <paramref name="onError" />, ou à <c>_error</c> par défaut ; le résultat dit si
    ///     l'action a réussi.
    /// </summary>
    protected async Task<bool> RunAsync(Func<Task> action, Action<string>? onError = null)
    {
        _busy = true;
        try
        {
            var ok = await ApiErrors.TryAsync(action, message => (onError ?? (m => _error = m))(message));
            if (ok) _error = null;
            return ok;
        }
        finally
        {
            _busy = false;
        }
    }
}
