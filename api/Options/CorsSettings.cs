namespace GranitDB.API.Options;

/// <summary>Origines autorisées à appeler l'API depuis un navigateur (section <c>Cors</c>).</summary>
public class CorsSettings
{
    public const string Section = "Cors";

    /// <summary>
    ///     Origines de l'interface (<c>https://doc.exemple.fr</c>), avec cookies de session. Vide : l'API répond à toutes les
    ///     origines mais sans cookies (poste de développement) ; avec l'authentification activée, seule l'origine de l'API
    ///     elle-même peut alors se connecter.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}
