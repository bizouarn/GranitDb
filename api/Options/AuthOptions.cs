namespace GranitDB.API.Options;

/// <summary>
///     Authentification et autorisation (section <c>Auth</c>). Désactivée par défaut : le poste de développement et
///     le catalogue SQLite local restent utilisables sans compte. Activée, tout appel doit venir d'une session ouverte
///     par GitHub ou par un fournisseur OpenID Connect déclaré ici.
/// </summary>
public class AuthOptions
{
    public const string Section = "Auth";

    public bool Enabled { get; set; }

    /// <summary>Durée d'une session (glissante), en heures.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>
    ///     Lax (défaut) quand l'interface et l'API sont sur le même site (ex. localhost, ou un même domaine) ; None si
    ///     l'interface est servie depuis un autre site (exige HTTPS).
    /// </summary>
    public string CookieSameSite { get; set; } = "Lax";

    /// <summary>Always, SameAsRequest (défaut) ou None. Toujours Always avec <c>CookieSameSite = None</c>.</summary>
    public string CookieSecure { get; set; } = "SameAsRequest";

    /// <summary>Connexion « Se connecter avec GitHub » (OAuth App) ; absente si <c>ClientId</c> est vide.</summary>
    public GitHubAuthOptions GitHub { get; set; } = new();

    /// <summary>Fournisseurs OpenID Connect génériques : nom (lettres, chiffres, tiret) → réglages.</summary>
    public Dictionary<string, OidcAuthOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Les bases marquées publiques (<c>IsPublic</c>, réglage réservé aux administrateurs) sont lisibles sans compte : fiche,
    ///     tables, colonnes, relations, domaines, diagrammes, exports. Rien d'autre ne l'est. Mettre à faux pour tout fermer.
    /// </summary>
    public bool AllowPublicDatabases { get; set; } = true;

    /// <summary>Qui entre, et avec quel rôle.</summary>
    public RoleRules Roles { get; set; } = new();
}

/// <summary>OAuth App GitHub (ou GitHub Enterprise Server en changeant les adresses).</summary>
public class GitHubAuthOptions
{
    public string? ClientId { get; set; }

    /// <summary>Secret client : à fournir par configuration d'environnement ou coffre, jamais dans le dépôt.</summary>
    public string? ClientSecret { get; set; }

    public string AuthorizationEndpoint { get; set; } = "https://github.com/login/oauth/authorize";
    public string TokenEndpoint { get; set; } = "https://github.com/login/oauth/access_token";

    /// <summary>Racine de l'API GitHub (<c>/user</c>, <c>/user/orgs</c>, <c>/user/teams</c>).</summary>
    public string ApiBase { get; set; } = "https://api.github.com";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>Un fournisseur OpenID Connect (Entra ID, Keycloak, Okta, Google…) déclaré par configuration seule.</summary>
public class OidcAuthOptions
{
    /// <summary>Libellé du bouton de connexion (par défaut le nom du fournisseur).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Adresse de l'autorité ; la découverte se fait à <c>/.well-known/openid-configuration</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>Adresse de découverte si elle n'est pas celle de l'autorité.</summary>
    public string? MetadataAddress { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>Revendication qui porte le login (<c>preferred_username</c>, <c>email</c>, <c>upn</c>…).</summary>
    public string LoginClaim { get; set; } = "preferred_username";

    public string NameClaim { get; set; } = "name";
    public string AvatarClaim { get; set; } = "picture";

    /// <summary>Revendication qui porte les groupes (liste) ; sert aux rôles.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>Complète les revendications avec le point d'accès userinfo.</summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; }

    /// <summary>Mettre à faux seulement pour un fournisseur de test en HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) &&
                                (!string.IsNullOrWhiteSpace(Authority) || !string.IsNullOrWhiteSpace(MetadataAddress));
}

/// <summary>
///     Rôles : <c>Admins</c>, <c>Editors</c> et <c>Readers</c> listent des logins (<c>octocat</c> ou
///     <c>github:octocat</c>, <c>oidc-nom:login</c> pour être précis) ; les listes <c>*Groups</c> des groupes du
///     fournisseur (GitHub : <c>organisation</c> pour tout membre, <c>organisation/equipe</c> pour une équipe). Le
///     rôle le plus élevé gagne ; sans correspondance, <c>DefaultRole</c> (Aucun = accès refusé).
/// </summary>
public class RoleRules
{
    public List<string> Admins { get; set; } = [];
    public List<string> Editors { get; set; } = [];
    public List<string> Readers { get; set; } = [];
    public List<string> AdminGroups { get; set; } = [];
    public List<string> EditorGroups { get; set; } = [];
    public List<string> ReaderGroups { get; set; } = [];

    /// <summary>None (défaut), Reader, Editor ou Admin : rôle de qui s'authentifie sans figurer dans une liste.</summary>
    public string DefaultRole { get; set; } = "None";

    public bool UsesGroups => AdminGroups.Count + EditorGroups.Count + ReaderGroups.Count > 0;
}
