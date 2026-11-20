namespace GranitDb.Client.Workspace;

/// <summary>Le vocabulaire technique de l'application, expliqué en une phrase (infobulles) : une seule source pour les infobulles et la page d'aide.</summary>
public static class Glossary
{
    public sealed record Entry(string Key, string Label, string Definition);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("cle-primaire", "Clé primaire (PK)", "La ou les colonnes qui identifient une ligne de façon unique dans sa table."),
        new("cle-etrangere", "Clé étrangère (FK)", "Une colonne dont la valeur renvoie à la clé d'une autre table : c'est ainsi que deux tables sont reliées."),
        new("cardinalite", "Cardinalité (1 / N)", "Combien de lignes d'un côté peuvent correspondre à l'autre côté. 1 = le côté référencé (parent), N = le côté qui référence (enfant)."),
        new("relation-manuelle", "Relation manuelle", "Relation saisie à la main : la synchronisation ne la supprime jamais (sauf si une de ses colonnes disparaît)."),
        new("relation-masquee", "Relation masquée", "Relation qui existe toujours dans le catalogue mais qui n'est pas dessinée dans le diagramme de ce domaine."),
        new("domaine", "Domaine", "Un regroupement de tables par sujet (facturation, stock…), avec son propre diagramme et sa couleur."),
        new("etiquette", "Étiquette", "Un mot-clé de couleur posé sur des bases, des tables ou des colonnes pour les retrouver et les filtrer."),
        new("colonne-sensible", "Colonne sensible", "Une colonne qui contient des données personnelles ou confidentielles : elle est signalée, et jamais envoyée à l'assistant d'IA."),
        new("table-voisine", "Table voisine", "Table d'un autre domaine reliée au domaine affiché ; dessinée en pointillés quand l'option est active."),
        new("synchronisation", "Synchronisation", "Relecture du schéma de la source : les tables, colonnes et clés qui ont changé sont mises à jour, vos descriptions sont conservées."),
    ];

    public static Entry? Find(string key) => Entries.FirstOrDefault(e => e.Key == key);

    /// <summary>La définition d'un terme (vide si la clé est inconnue).</summary>
    public static string Define(string key) => Find(key)?.Definition ?? "";
}
