using MudBlazor;

namespace GranitDb.Client.Components;

/// <summary>Le message en haut d'une page (résultat d'une action, erreur) et sa gravité ; <see cref="PageMessageAlert" /> l'affiche.</summary>
public sealed class PageMessage
{
    public string? Text { get; private set; }

    public Severity Severity { get; private set; } = Severity.Info;

    public void Show(string text, Severity severity) => (Text, Severity) = (text, severity);

    public void Error(string text) => Show(text, Severity.Error);

    public void Warning(string text) => Show(text, Severity.Warning);

    public void Success(string text) => Show(text, Severity.Success);

    public void Clear() => Text = null;
}
