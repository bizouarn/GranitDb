using GranitDb.Client.api;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace GranitDb.Client.Components.Dialogs;

/// <summary>
///     Ce que les dialogues d'enregistrement ont en commun : un seul envoi à la fois, refusé tant que le formulaire est incomplet,
///     l'erreur du serveur affichée dans le dialogue (qui reste ouvert), et fermeture avec le résultat quand tout a réussi.
/// </summary>
public abstract class SaveDialogBase : ComponentBase
{
    [CascadingParameter] public IMudDialogInstance MudDialog { get; set; } = null!;

    /// <summary>Dernière erreur du serveur, affichée dans le dialogue.</summary>
    protected string? _error;

    /// <summary>Un envoi est en cours.</summary>
    protected bool _busy;

    /// <summary>Le formulaire est complet et valide : le bouton « Enregistrer » est actif.</summary>
    protected abstract bool CanSave { get; }

    /// <summary>
    ///     Exécute l'enregistrement ; sa valeur de retour est le résultat du dialogue. Une <see cref="ApiException" /> reste dans le
    ///     dialogue comme <c>_error</c>.
    /// </summary>
    protected async Task SubmitAsync(Func<Task<object>> save)
    {
        if (_busy || !CanSave) return;
        _busy = true;
        _error = null;
        try
        {
            MudDialog.Close(DialogResult.Ok(await save()));
        }
        catch (ApiException e)
        {
            _error = e.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
