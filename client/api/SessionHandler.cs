using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace GranitDb.Client.api;

/// <summary>
///     Envoie le cookie de session avec chaque appel quand l'API demande une session, et marque les écritures comme venant de
///     l'interface.
/// </summary>
public sealed class SessionHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>
    ///     Joindre les identifiants à un appel vers une autre origine : le navigateur n'en lit alors la réponse que si l'API
    ///     nomme l'origine de l'interface (<c>Cors:AllowedOrigins</c>). Sans authentification il n'y a pas de cookie à envoyer,
    ///     et les demander ferait bloquer tous les appels. <see cref="Client.GetSessionAsync" /> l'active.
    /// </summary>
    public bool SendCredentials { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(SendCredentials ? BrowserRequestCredentials.Include : BrowserRequestCredentials.SameOrigin);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            request.Headers.TryAddWithoutValidation("X-GranitDb-Client", "1");
        return base.SendAsync(request, cancellationToken);
    }
}
