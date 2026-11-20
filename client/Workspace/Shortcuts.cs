namespace GranitDb.Client.Workspace;

/// <summary>
///     Les raccourcis clavier, écrits une fois : la feuille « Raccourcis », la palette de commandes et les infobulles des boutons
///     disent la même chose (les touches elles-mêmes sont écoutées dans <c>index.html</c>).
/// </summary>
public static class Shortcuts
{
    public const string Palette = "Ctrl+K";
    public const string Help = "?";
    public const string Explorer = "Ctrl+B";
    public const string CloseTab = "Alt+W";
    public const string Favorite = "F";

    /// <summary>Une infobulle : le libellé suivi de sa touche, « Explorateur (Ctrl+B) ».</summary>
    public static string Hint(string label, string keys) => $"{label} ({keys})";

    /// <summary>La touche écrite pour la feuille des raccourcis : « Ctrl + K ».</summary>
    public static string Spaced(string keys) => keys.Replace("+", " + ");
}
