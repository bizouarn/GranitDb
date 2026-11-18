using MudBlazor;

namespace GranitDb.Client.Components.Dialogs;

/// <summary>
///     Règles communes de toutes les fenêtres de saisie : titre qui dit l'action, Échap ferme sans rien enregistrer,
///     un clic à côté ne ferme pas (une saisie ne se perd pas par accident), plein écran sur un petit écran.
/// </summary>
public static class DialogHelper
{
    public static DialogOptions Options(MaxWidth width = MaxWidth.Small) => new()
    {
        MaxWidth = width,
        FullWidth = true,
        CloseOnEscapeKey = true,
        CloseButton = true,
        BackdropClick = false,
        NoHeader = false
    };

    /// <summary>Ouvre la fenêtre <typeparamref name="T" /> ; renvoie vrai si elle a été validée (et non annulée).</summary>
    public static async Task<bool> ShowFormAsync<T>(this IDialogService dialogs, string title, DialogParameters<T> parameters,
        MaxWidth width = MaxWidth.Small) where T : Microsoft.AspNetCore.Components.IComponent
    {
        var dialog = await dialogs.ShowAsync<T>(title, parameters, Options(width));
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    /// <summary>Comme <see cref="ShowFormAsync{T}" />, mais renvoie la valeur que la fenêtre a fournie en se fermant (null si annulée).</summary>
    public static async Task<TData?> ShowDataAsync<T, TData>(this IDialogService dialogs, string title, DialogParameters<T> parameters,
        MaxWidth width = MaxWidth.Small) where T : Microsoft.AspNetCore.Components.IComponent where TData : class
    {
        var dialog = await dialogs.ShowAsync<T>(title, parameters, Options(width));
        var result = await dialog.Result;
        return result is { Canceled: false } ? result.Data as TData : null;
    }
}
