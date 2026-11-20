using System.Text.Json;

namespace GranitDb.Client.Workspace;

public enum TabKind
{
    Home,
    Database,
    Table,
    Diagram,
    Search,
    Other
}

/// <summary>Un onglet de l'espace de travail : une page du client (son adresse) avec un titre lisible.</summary>
public record WorkspaceTab(string Href, string Title, TabKind Kind, bool Pinned = false);

/// <summary>Un favori : <c>Kind</c> vaut database, table ou domain.</summary>
public record FavoriteItem(string Kind, string Id, string Name, string Href);

/// <summary>Élément sélectionné dans l'explorateur : <c>Kind</c> vaut database, table ou column.</summary>
public record SelectionItem(string Kind, string Id, string DatabaseId, string? TableId = null);

/// <summary>
///     État de l'espace de travail (un onglet par page ouverte, favoris, panneaux, sélection). Il ne dépend d'aucun
///     composant : la mise en page l'affiche et le met à jour, les pages lui donnent leur titre.
/// </summary>
public class WorkspaceState
{
    public const int MaxTabs = 12;

    private readonly List<WorkspaceTab> _tabs = [];
    private readonly List<FavoriteItem> _favorites = [];

    // Ce que l'utilisateur a choisi : seul ce choix est mémorisé. L'état affiché peut en différer (un petit écran
    // referme les tiroirs tout seul) sans que cela devienne une préférence.
    private bool _explorerPreferred = true;
    private bool _detailPreferred = true;

    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;

    public IReadOnlyList<FavoriteItem> Favorites => _favorites;

    /// <summary>Adresse de l'onglet affiché (celle de la page courante).</summary>
    public string? ActiveHref { get; private set; }

    public SelectionItem? Selection { get; private set; }

    public bool ExplorerOpen { get; private set; } = true;

    public bool DetailOpen { get; private set; } = true;

    public bool PaletteOpen { get; private set; }

    /// <summary>Un onglet, un favori, un panneau ou la sélection a changé : la mise en page se redessine.</summary>
    public event Action? Changed;

    /// <summary>
    ///     Le catalogue a changé (import, synchronisation, édition…) : <c>databaseId</c> est la base concernée, ou
    ///     <c>null</c> si c'est toute la liste. <c>source</c> est l'émetteur, qui n'a pas besoin de se recharger.
    /// </summary>
    public event Action<string?, object?>? CatalogChanged;

    // --- Adresses ---

    /// <summary>Adresse relative normalisée : « /chemin?requête », sans fragment, « / » pour l'accueil.</summary>
    public static string Normalize(string hrefOrUri)
    {
        var text = hrefOrUri.Trim();
        // Sous Linux (et dans le navigateur), « /chemin?x » est aussi un chemin de fichier absolu valide : seules les adresses http(s) sont des URI.
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") text = absolute.PathAndQuery;
        var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash];
        if (!text.StartsWith('/')) text = "/" + text;
        return text.Length > 1 ? text.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/" : "/";
    }

    /// <summary>Un onglet se reconnaît à son chemin : <c>?table=</c> ou <c>?domain=</c> ne créent pas un autre onglet.</summary>
    private static string KeyOf(string href)
    {
        var q = href.IndexOf('?');
        return q < 0 ? href : href[..q];
    }

    public static TabKind KindOf(string href)
    {
        var path = KeyOf(Normalize(href));
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            [] => TabKind.Home,
            ["databases"] => TabKind.Home,
            ["search"] => TabKind.Search,
            ["databases", _] => TabKind.Database,
            ["databases", _, "diagram"] => TabKind.Diagram,
            ["databases", _, "tables", _] => TabKind.Table,
            _ => TabKind.Other
        };
    }

    public static string DefaultTitle(string href)
    {
        return KindOf(href) switch
        {
            TabKind.Home => "Bases",
            TabKind.Database => "Base",
            TabKind.Table => "Table",
            TabKind.Diagram => "Diagramme",
            TabKind.Search => "Recherche",
            _ => "Page"
        };
    }

    // --- Onglets ---

    /// <summary>Affiche l'onglet de cette page (créé s'il n'existe pas) ; au-delà de <see cref="MaxTabs" />, le plus ancien non épinglé part.</summary>
    public WorkspaceTab Open(string hrefOrUri)
    {
        var href = Normalize(hrefOrUri);
        var index = Find(href);
        WorkspaceTab tab;
        if (index >= 0)
        {
            tab = _tabs[index] with { Href = href };
            _tabs[index] = tab;
        }
        else
        {
            tab = new WorkspaceTab(href, DefaultTitle(href), KindOf(href));
            _tabs.Add(tab);
            Evict(href);
        }

        ActiveHref = href;
        Changed?.Invoke();
        return tab;
    }

    /// <summary>Donne son titre à l'onglet de cette page (le crée si besoin, sans l'afficher).</summary>
    public void Describe(string hrefOrUri, string title)
    {
        var href = Normalize(hrefOrUri);
        var index = Find(href);
        if (index < 0)
        {
            _tabs.Add(new WorkspaceTab(href, title, KindOf(href)));
            Evict(ActiveHref ?? href);
        }
        else if (_tabs[index].Title != title)
        {
            _tabs[index] = _tabs[index] with { Title = title };
        }
        else
        {
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>
    ///     Ferme un onglet non épinglé. Renvoie l'adresse où aller si c'était l'onglet affiché (son voisin, ou
    ///     l'accueil s'il ne reste rien), sinon <c>null</c>.
    /// </summary>
    public string? Close(string hrefOrUri)
    {
        var index = Find(Normalize(hrefOrUri));
        if (index < 0 || _tabs[index].Pinned) return null;
        var wasActive = KeyOf(_tabs[index].Href) == (ActiveHref == null ? null : KeyOf(ActiveHref));
        _tabs.RemoveAt(index);
        string? next = null;
        if (wasActive)
        {
            next = _tabs.Count == 0 ? "/" : _tabs[Math.Min(index, _tabs.Count - 1)].Href;
            ActiveHref = next;
        }

        Changed?.Invoke();
        return next;
    }

    /// <summary>Ferme les onglets non épinglés à droite de celui-ci. Renvoie où aller si l'onglet affiché a disparu.</summary>
    public string? CloseToTheRight(string hrefOrUri)
    {
        var index = Find(Normalize(hrefOrUri));
        return index < 0 ? null : CloseWhere((i, _) => i > index, _tabs[index].Href);
    }

    /// <summary>Ferme tous les onglets non épinglés sauf celui-ci.</summary>
    public string? CloseOthers(string hrefOrUri)
    {
        var index = Find(Normalize(hrefOrUri));
        return index < 0 ? null : CloseWhere((i, _) => i != index, _tabs[index].Href);
    }

    /// <summary>Ferme tous les onglets non épinglés ; renvoie l'accueil (ou le premier onglet épinglé) si l'onglet affiché a disparu.</summary>
    public string? CloseAll() => CloseWhere((_, _) => true, null);

    /// <summary>Nombre d'onglets que « fermer à droite » fermerait (les épinglés restent).</summary>
    public int CountToTheRight(string hrefOrUri)
    {
        var index = Find(Normalize(hrefOrUri));
        return index < 0 ? 0 : _tabs.Where((t, i) => i > index && !t.Pinned).Count();
    }

    private string? CloseWhere(Func<int, WorkspaceTab, bool> predicate, string? preferred)
    {
        var activeKey = ActiveHref == null ? null : KeyOf(ActiveHref);
        var before = _tabs.Count;
        var activeClosed = false;
        for (var i = _tabs.Count - 1; i >= 0; i--)
        {
            if (_tabs[i].Pinned || !predicate(i, _tabs[i])) continue;
            if (KeyOf(_tabs[i].Href) == activeKey) activeClosed = true;
            _tabs.RemoveAt(i);
        }

        if (_tabs.Count == before) return null;
        string? next = null;
        if (activeClosed)
        {
            next = preferred ?? (_tabs.Count == 0 ? "/" : _tabs[0].Href);
            ActiveHref = next;
        }

        Changed?.Invoke();
        return next;
    }

    public void TogglePin(string hrefOrUri)
    {
        var index = Find(Normalize(hrefOrUri));
        if (index < 0) return;
        _tabs[index] = _tabs[index] with { Pinned = !_tabs[index].Pinned };
        Changed?.Invoke();
    }

    /// <summary>Place l'onglet juste avant <paramref name="beforeHref" /> (à la fin si celui-ci est inconnu).</summary>
    public void MoveTab(string hrefOrUri, string? beforeHref)
    {
        var from = Find(Normalize(hrefOrUri));
        if (from < 0) return;
        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        var to = beforeHref == null ? -1 : Find(Normalize(beforeHref));
        _tabs.Insert(to < 0 ? _tabs.Count : to, tab);
        Changed?.Invoke();
    }

    private int Find(string href)
    {
        var key = KeyOf(href);
        return _tabs.FindIndex(t => KeyOf(t.Href) == key);
    }

    private void Evict(string keep)
    {
        while (_tabs.Count > MaxTabs)
        {
            var victim = _tabs.FindIndex(t => !t.Pinned && KeyOf(t.Href) != KeyOf(keep));
            if (victim < 0) return;
            _tabs.RemoveAt(victim);
        }
    }

    // --- Favoris ---

    public bool IsFavorite(string kind, string id) => _favorites.Any(f => f.Kind == kind && f.Id == id);

    /// <summary>Ajoute ou retire le favori ; renvoie <c>true</c> s'il en est un maintenant.</summary>
    public bool ToggleFavorite(FavoriteItem item)
    {
        var index = _favorites.FindIndex(f => f.Kind == item.Kind && f.Id == item.Id);
        bool now;
        if (index >= 0)
        {
            _favorites.RemoveAt(index);
            now = false;
        }
        else
        {
            _favorites.Add(item);
            now = true;
        }

        Changed?.Invoke();
        return now;
    }

    /// <summary>Place le favori juste avant <paramref name="beforeId" /> (à la fin si celui-ci est inconnu).</summary>
    public void MoveFavorite(string kind, string id, string? beforeId)
    {
        var from = _favorites.FindIndex(f => f.Kind == kind && f.Id == id);
        if (from < 0 || id == beforeId) return;
        var item = _favorites[from];
        _favorites.RemoveAt(from);
        var to = beforeId == null ? -1 : _favorites.FindIndex(f => f.Id == beforeId);
        _favorites.Insert(to < 0 ? _favorites.Count : to, item);
        Changed?.Invoke();
    }

    // --- Panneaux et sélection ---

    public void Select(SelectionItem? item)
    {
        Selection = item;
        Changed?.Invoke();
    }

    /// <param name="open">Tiroir ouvert ou fermé.</param>
    /// <param name="remember">Vrai pour un choix de l'utilisateur (mémorisé), faux pour un changement automatique de la mise en page.</param>
    public void SetExplorerOpen(bool open, bool remember = true)
    {
        ExplorerOpen = open;
        if (remember) _explorerPreferred = open;
        Changed?.Invoke();
    }

    public void SetDetailOpen(bool open, bool remember = true)
    {
        DetailOpen = open;
        if (remember) _detailPreferred = open;
        Changed?.Invoke();
    }

    public void SetPalette(bool open)
    {
        PaletteOpen = open;
        Changed?.Invoke();
    }

    public void NotifyCatalogChanged(string? databaseId = null, object? source = null)
    {
        CatalogChanged?.Invoke(databaseId, source);
    }

    // --- Persistance ---

    private record Snapshot(List<WorkspaceTab>? Tabs, List<FavoriteItem>? Favorites, bool ExplorerOpen = true, bool DetailOpen = true);

    public string ToJson()
    {
        return JsonSerializer.Serialize(new Snapshot(_tabs, _favorites, _explorerPreferred, _detailPreferred));
    }

    /// <summary>Reprend un état enregistré (onglets, favoris, panneaux) ; un contenu illisible est ignoré.</summary>
    public bool TryRestore(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(json);
            if (snapshot == null) return false;
            _tabs.Clear();
            foreach (var tab in snapshot.Tabs ?? [])
                if (!string.IsNullOrWhiteSpace(tab.Href) && Find(Normalize(tab.Href)) < 0)
                    _tabs.Add(tab with { Href = Normalize(tab.Href), Kind = KindOf(tab.Href) });
            _favorites.Clear();
            foreach (var favorite in snapshot.Favorites ?? [])
                if (!string.IsNullOrWhiteSpace(favorite.Id) && !IsFavorite(favorite.Kind, favorite.Id))
                    _favorites.Add(favorite);
            ExplorerOpen = _explorerPreferred = snapshot.ExplorerOpen;
            DetailOpen = _detailPreferred = snapshot.DetailOpen;
            Changed?.Invoke();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
