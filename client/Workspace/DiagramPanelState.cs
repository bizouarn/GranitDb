namespace GranitDb.Client.Workspace;

/// <summary>Ce que la page du diagramme met dans la barre latérale : la liste des tables à afficher (ou du domaine regardé).</summary>
public class DiagramPanel
{
    public string DatabaseId { get; init; } = "";

    /// <summary>Le domaine regardé, ou null pour « toutes les tables ».</summary>
    public string? DomainName { get; init; }

    /// <summary>Les tables de la base : identifiant et nom.</summary>
    public IReadOnlyList<(string Id, string Name)> Tables { get; init; } = [];

    /// <summary>Les tables dessinées (cochées), ou celles du domaine.</summary>
    public IReadOnlySet<string> Shown { get; init; } = new HashSet<string>();

    /// <summary>Coche ou décoche une table.</summary>
    public Func<string, bool, Task> Toggle { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>« Toutes » et « Aucune » (absents dans la vue d'un domaine, où l'on coche pour ranger).</summary>
    public Func<Task>? SelectAll { get; init; }

    public Func<Task>? SelectNone { get; init; }

    /// <summary>Début et fin du glisser d'une table vers un domaine de la page.</summary>
    public Action<string>? DragStart { get; init; }

    public Action? DragEnd { get; init; }
}

/// <summary>
///     Pont entre la page du diagramme et l'explorateur : la page publie sa liste de tables, l'explorateur (s'il est affiché)
///     la montre dans la barre latérale et la page ne la répète plus dans son contenu.
/// </summary>
public class DiagramPanelState
{
    private int _hosts;

    /// <summary>Un explorateur est affiché et montre la liste : la page n'a pas à la répéter.</summary>
    public bool SidebarHosts => _hosts > 0;

    public DiagramPanel? Current { get; private set; }

    /// <summary>Appelé quand la liste publiée change.</summary>
    public event Action? Changed;

    public void AddHost()
    {
        _hosts++;
        Changed?.Invoke();
    }

    public void RemoveHost()
    {
        _hosts = Math.Max(0, _hosts - 1);
        Changed?.Invoke();
    }

    public void Publish(DiagramPanel panel)
    {
        Current = panel;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (Current == null) return;
        Current = null;
        Changed?.Invoke();
    }
}
