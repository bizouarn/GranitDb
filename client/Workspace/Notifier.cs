using MudBlazor;

namespace GranitDb.Client.Workspace;

/// <summary>Un message bref, éventuellement assorti d'une action pour annuler ce qui vient d'être fait.</summary>
public record Toast(Guid Id, string Message, Severity Severity, Func<Task>? Undo);

/// <summary>
///     Retours à l'utilisateur : une confirmation qui disparaît seule, et qui propose « Annuler » quand l'action peut
///     être défaite (plutôt qu'une boîte de dialogue bloquante).
/// </summary>
public class Notifier
{
    private readonly List<Toast> _toasts = [];

    public IReadOnlyList<Toast> Toasts => _toasts;

    /// <summary>Durée d'affichage ; plus longue quand il y a une annulation à proposer.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(6);

    public TimeSpan UndoDuration { get; set; } = TimeSpan.FromSeconds(10);

    public event Action? Changed;

    public Toast Show(string message, Severity severity = Severity.Success, Func<Task>? undo = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, severity, undo);
        _toasts.Add(toast);
        // Au plus quelques messages à la fois : les plus anciens cèdent la place.
        while (_toasts.Count > 4) _toasts.RemoveAt(0);
        Changed?.Invoke();
        _ = DismissLaterAsync(toast, undo == null ? Duration : UndoDuration);
        return toast;
    }

    public void Error(string message) => Show(message, Severity.Error);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>Exécute l'annulation du message puis le retire ; une annulation qui échoue est signalée.</summary>
    public async Task UndoAsync(Guid id)
    {
        var toast = _toasts.FirstOrDefault(t => t.Id == id);
        if (toast?.Undo == null) return;
        Dismiss(id);
        try
        {
            await toast.Undo();
            Show("Action annulée.", Severity.Info);
        }
        catch (Exception e) when (e is GranitDb.Client.api.ApiException or HttpRequestException)
        {
            Show($"Annulation impossible : {e.Message}", Severity.Error);
        }
    }

    private async Task DismissLaterAsync(Toast toast, TimeSpan delay)
    {
        await Task.Delay(delay);
        Dismiss(toast.Id);
    }
}

public static class WorkspaceSavedExtensions
{
    /// <summary>
    ///     Dit aux autres vues que le catalogue a changé et confirme par un message (avec annulation possible) : les deux gestes vont
    ///     ensemble, en oublier un laisse l'explorateur ou un onglet périmé.
    /// </summary>
    public static Toast Saved(this WorkspaceState workspace, Notifier notifier, string? databaseId, object? source, string message,
        Severity severity = Severity.Success, Func<Task>? undo = null)
    {
        workspace.NotifyCatalogChanged(databaseId, source);
        return notifier.Show(message, severity, undo);
    }
}
