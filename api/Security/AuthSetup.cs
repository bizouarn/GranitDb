using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using GranitDB.API.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace GranitDB.API.Security;

/// <summary>Un compte refusé à la connexion : aucun rôle ne lui est attribué.</summary>
public sealed class AccessRefusedException(string login, string? returnUrl) : Exception($"Aucun rôle n'est attribué à {login}.")
{
    /// <summary>Où renvoyer l'utilisateur : l'exception levée pendant la connexion n'emporte pas ses propriétés avec elle.</summary>
    public string? ReturnUrl { get; } = returnUrl;
}

/// <summary>Un fournisseur de connexion proposé par l'interface.</summary>
public record AuthProviderView(string Id, string DisplayName);

/// <summary>L'état de la session de l'appelant : l'interface s'en sert pour proposer la connexion et adapter ses droits.</summary>
public record SessionView(bool Enabled, bool Authenticated, string? Login, string? Name, string? AvatarUrl, string? Role,
    string? Provider, List<AuthProviderView> Providers, bool AllowAnonymousRead = false);

/// <summary>
///     Connexion par GitHub (OAuth) et par des fournisseurs OpenID Connect génériques, session par cookie émis par l'API,
///     rôles lecteur / éditeur / administrateur calculés à la connexion d'après <see cref="RoleRules" />.
/// </summary>
public static class AuthSetup
{
    public const string CookieScheme = "granitdb.session";
    public const string GitHubId = "github";
    public const string DisplayNameClaim = "granitdb:name";
    public const string AvatarClaim = "granitdb:avatar";
    public const string ProviderClaim = "granitdb:provider";
    public const string ErrorParameter = "auth_error";

    private static readonly Regex ProviderName = new("^[A-Za-z0-9-]{1,40}$", RegexOptions.Compiled);

    public static string OidcScheme(string name) => "oidc-" + name.ToLowerInvariant();

    /// <summary>Schéma d'authentification d'un fournisseur (<c>github</c> ou le nom d'un fournisseur OpenID Connect).</summary>
    public static string? SchemeOf(AuthOptions options, string provider)
    {
        if (provider.Equals(GitHubId, StringComparison.OrdinalIgnoreCase) && options.GitHub.IsConfigured) return GitHubId;
        var match = options.Providers.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase));
        return match.Value is { IsConfigured: true } ? OidcScheme(match.Key) : null;
    }

    public static List<AuthProviderView> ProvidersOf(AuthOptions options)
    {
        var list = new List<AuthProviderView>();
        if (options.GitHub.IsConfigured) list.Add(new AuthProviderView(GitHubId, "GitHub"));
        foreach (var (name, p) in options.Providers.Where(p => p.Value.IsConfigured).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            list.Add(new AuthProviderView(name.ToLowerInvariant(), string.IsNullOrWhiteSpace(p.DisplayName) ? name : p.DisplayName!));
        return list;
    }

    /// <summary>Vérifie la configuration au démarrage : une authentification activée sans fournisseur verrouillerait tout le monde dehors.</summary>
    public static void Validate(AuthOptions options)
    {
        if (!options.Enabled) return;
        if (ProvidersOf(options).Count == 0)
            throw new InvalidOperationException(
                "Auth:Enabled est vrai mais aucun fournisseur n'est configuré : renseignez Auth:GitHub (ClientId, ClientSecret) " +
                "ou au moins un fournisseur dans Auth:Providers (Authority, ClientId, ClientSecret).");
        foreach (var name in options.Providers.Keys)
            if (!ProviderName.IsMatch(name) || name.Equals(GitHubId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Nom de fournisseur invalide : « {name} » (lettres, chiffres et tirets ; « {GitHubId} » est réservé).");
        if (!Enum.TryParse<Role>(options.Roles.DefaultRole, true, out _))
            throw new InvalidOperationException($"Auth:Roles:DefaultRole inconnu : {options.Roles.DefaultRole}.");
        if (options.Roles.Admins.Count + options.Roles.AdminGroups.Count == 0 && RoleResolver.Parse(options.Roles.DefaultRole) != Role.Admin)
            throw new InvalidOperationException(
                "Auth:Roles ne désigne aucun administrateur : renseignez Auth:Roles:Admins (ou AdminGroups) pour déclarer le premier.");
    }

    public static IServiceCollection AddGranitAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        var options = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        Validate(options);
        if (!options.Enabled) return services;

        var sameSite = Enum.TryParse<SameSiteMode>(options.CookieSameSite, true, out var mode) ? mode : SameSiteMode.Lax;
        var secure = sameSite == SameSiteMode.None ? CookieSecurePolicy.Always
            : Enum.TryParse<CookieSecurePolicy>(options.CookieSecure, true, out var policy) ? policy : CookieSecurePolicy.SameAsRequest;

        // Une connexion qui échoue sans qu'on sache d'où elle venait revient à l'interface (première origine déclarée).
        var fallback = configuration.GetSection(CorsSettings.Section).Get<CorsSettings>()?.AllowedOrigins.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o))?.Trim() ?? "/";
        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieScheme;
            o.DefaultChallengeScheme = CookieScheme;
        }).AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "granitdb.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = sameSite;
            o.Cookie.SecurePolicy = secure;
            o.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, options.SessionHours));
            o.SlidingExpiration = true;
            // Une API : pas de redirection vers une page de connexion, juste le code de réponse.
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });

        if (options.GitHub.IsConfigured) AddGitHub(auth, options, secure, fallback);
        foreach (var (name, provider) in options.Providers.Where(p => p.Value.IsConfigured))
            AddOpenIdConnect(auth, options, name, provider, secure, fallback);
        return services;
    }

    private static ClaimsPrincipal Principal(string provider, string login, string? name, string? avatar, Role role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, login),
            new(ClaimTypes.NameIdentifier, $"{provider}:{login}"),
            new(DisplayNameClaim, string.IsNullOrWhiteSpace(name) ? login : name),
            new(AvatarClaim, avatar ?? ""),
            new(ProviderClaim, provider),
            new(ClaimTypes.Role, role.ToString())
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>Retour vers l'interface avec un code d'erreur (jamais le détail technique) quand la connexion échoue.</summary>
    private static Func<RemoteFailureContext, Task> Failed(string fallback) => context =>
    {
        context.HandleResponse();
        // Le détail reste dans le journal du serveur ; l'interface ne reçoit qu'un code.
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth")
            .LogWarning(context.Failure, "Connexion refusée ou échouée ({Scheme})", context.Scheme.Name);
        var target = context.Properties?.RedirectUri ?? (context.Failure as AccessRefusedException)?.ReturnUrl;
        RedirectWithError(context.Response, target, fallback, context.Failure is AccessRefusedException ? "refused" : "failed");
        return Task.CompletedTask;
    };

    private static void RedirectWithError(HttpResponse response, string? target, string fallback, string code) =>
        response.Redirect(QueryHelpers.AddQueryString(string.IsNullOrEmpty(target) ? fallback : target, ErrorParameter, code));

    private static void AddGitHub(AuthenticationBuilder auth, AuthOptions options, CookieSecurePolicy secure, string fallback)
    {
        var github = options.GitHub;
        auth.AddOAuth(GitHubId, "GitHub", o =>
        {
            o.SignInScheme = CookieScheme;
            o.ClientId = github.ClientId!;
            o.ClientSecret = github.ClientSecret!;
            o.AuthorizationEndpoint = github.AuthorizationEndpoint;
            o.TokenEndpoint = github.TokenEndpoint;
            o.CallbackPath = "/auth/callback/" + GitHubId;
            o.Scope.Add("read:user");
            if (options.Roles.UsesGroups) o.Scope.Add("read:org");
            o.SaveTokens = false;
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = secure; // « toujours sécurisé » par défaut : introuvable en HTTP
            o.Events.OnRemoteFailure = Failed(fallback);
            o.Events.OnCreatingTicket = async ctx =>
            {
                async Task<JsonDocument> Get(string path)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, github.ApiBase.TrimEnd('/') + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                    request.Headers.UserAgent.ParseAdd("GranitDb");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ctx.HttpContext.RequestAborted),
                        cancellationToken: ctx.HttpContext.RequestAborted);
                }

                using var user = await Get("/user");
                var login = user.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
                if (string.IsNullOrWhiteSpace(login)) throw new InvalidOperationException("GitHub n'a pas donné de login.");

                string? Text(string key) =>
                    user.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                // Organisations (« org ») et équipes (« org/equipe ») : seulement si la configuration s'en sert.
                var groups = new List<string>();
                if (options.Roles.UsesGroups)
                {
                    using var orgs = await Get("/user/orgs?per_page=100");
                    groups.AddRange(orgs.RootElement.EnumerateArray().Select(x => x.GetProperty("login").GetString() ?? "").Where(g => g != ""));
                    using var teams = await Get("/user/teams?per_page=100");
                    groups.AddRange(teams.RootElement.EnumerateArray()
                        .Select(t => $"{t.GetProperty("organization").GetProperty("login").GetString()}/{t.GetProperty("slug").GetString()}"));
                }

                var role = RoleResolver.Resolve(options.Roles, GitHubId, login, groups);
                if (role == Role.None) throw new AccessRefusedException(login, ctx.Properties?.RedirectUri);
                ctx.Principal = Principal(GitHubId, login, Text("name"), Text("avatar_url"), role);
            };
        });
    }

    private static void AddOpenIdConnect(AuthenticationBuilder auth, AuthOptions options, string name, OidcAuthOptions provider,
        CookieSecurePolicy secure, string fallback)
    {
        var id = name.ToLowerInvariant();
        auth.AddOpenIdConnect(OidcScheme(name), string.IsNullOrWhiteSpace(provider.DisplayName) ? name : provider.DisplayName, o =>
        {
            o.SignInScheme = CookieScheme;
            if (!string.IsNullOrWhiteSpace(provider.Authority)) o.Authority = provider.Authority;
            if (!string.IsNullOrWhiteSpace(provider.MetadataAddress)) o.MetadataAddress = provider.MetadataAddress;
            o.ClientId = provider.ClientId;
            o.ClientSecret = provider.ClientSecret;
            o.ResponseType = "code";
            o.UsePkce = true;
            o.Scope.Clear();
            foreach (var scope in provider.Scopes) o.Scope.Add(scope);
            o.CallbackPath = "/auth/callback/" + id;
            o.SignedOutCallbackPath = "/auth/signed-out/" + id;
            o.RemoteSignOutPath = "/auth/remote-sign-out/" + id;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = provider.GetClaimsFromUserInfoEndpoint;
            o.RequireHttpsMetadata = provider.RequireHttpsMetadata;
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.NonceCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = secure;
            o.NonceCookie.SecurePolicy = secure;
            o.Events.OnRemoteFailure = Failed(fallback);
            o.Events.OnTicketReceived = ctx =>
            {
                var source = ctx.Principal!;
                var login = source.FindFirst(provider.LoginClaim)?.Value ?? source.FindFirst("sub")?.Value;
                if (string.IsNullOrWhiteSpace(login))
                    throw new InvalidOperationException($"Le fournisseur n'a pas donné la revendication « {provider.LoginClaim} ».");
                var groups = source.FindAll(provider.GroupsClaim).Select(c => c.Value).ToList();
                var role = RoleResolver.Resolve(options.Roles, id, login, groups);
                if (role == Role.None)
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth")
                        .LogWarning("Connexion refusée : aucun rôle pour {Login} ({Scheme})", login, ctx.Scheme.Name);
                    RedirectWithError(ctx.Response, ctx.Properties?.RedirectUri, fallback, "refused");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }

                ctx.Principal = Principal(id, login, source.FindFirst(provider.NameClaim)?.Value,
                    source.FindFirst(provider.AvatarClaim)?.Value, role);
                return Task.CompletedTask;
            };
        });
    }
}
