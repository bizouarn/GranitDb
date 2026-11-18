namespace GranitDB.API.Models;

/// <summary>
///     Une requête qui a modifié le catalogue (ou sorti sa documentation) : qui, quand, quoi, et ce que le serveur a
///     répondu. Jamais le contenu de la requête (il peut porter des mots de passe). Pas de clé étrangère : le journal
///     survit à la suppression de ce qu'il décrit.
/// </summary>
public class AuditEntry
{
    public string Id { get; set; } = "";

    public DateTime At { get; set; }

    /// <summary>Appelant identifié (<c>User.Identity.Name</c>) ; vide tant qu'il n'y a pas d'authentification.</summary>
    public string? Actor { get; set; }

    public string Method { get; set; } = "";

    /// <summary>Chemin sans préfixe de version ni paramètres de requête.</summary>
    public string Path { get; set; } = "";

    public int Status { get; set; }

    /// <summary>Contrôleur visé (DatabaseInfo, TableInfo…).</summary>
    public string? Controller { get; set; }

    /// <summary>Identifiant de l'élément visé, quand la route en porte un.</summary>
    public string? EntityId { get; set; }

    public string? Ip { get; set; }
}

/// <summary>Un abonnement à des événements du catalogue : une URL appelée en POST, avec un secret facultatif pour signer.</summary>
public class WebhookInfo
{
    public string Id { get; set; } = "";

    /// <summary>Base surveillée ; vide = toutes les bases.</summary>
    public string? DatabaseId { get; set; }

    public string Url { get; set; } = "";

    /// <summary>Secret de signature, chiffré dans le catalogue ; jamais renvoyé par l'API.</summary>
    public string? Secret { get; set; }

    /// <summary>Événements demandés, séparés par des virgules.</summary>
    public string Events { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Une tentative d'envoi (avec ses reprises) vers un webhook.</summary>
public class WebhookDelivery
{
    public string Id { get; set; } = "";

    public string WebhookId { get; set; } = "";

    public DateTime At { get; set; }

    public string Event { get; set; } = "";

    public int? StatusCode { get; set; }

    public bool Success { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }
}
