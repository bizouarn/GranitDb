using System.Security.Claims;
using GranitDB.API.Options;
using GranitDB.API.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Controllers;

/// <summary>Session de l'appelant et connexion (GitHub, fournisseurs OpenID Connect configurés).</summary>
[Route("api/v1/[controller]")]
[Route("[controller]")]
[ApiController]
[AllowAnonymous]
public class AuthController(IOptions<AuthOptions> authOptions, IOptions<CorsSettings> cors) : ControllerBase
{
    private AuthOptions Options => authOptions.Value;

    /// <summary>
    ///     Qui est connecté : l'interface propose la connexion tant que <c>authenticated</c> est faux et que
    ///     <c>enabled</c> est vrai, et adapte ses droits au rôle (Reader, Editor, Admin).
    /// </summary>
    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!Options.Enabled) return Ok(new SessionView(false, false, null, null, null, null, null, []));
        var providers = AuthSetup.ProvidersOf(Options);
        if (User.Identity?.IsAuthenticated != true) return Ok(new SessionView(true, false, null, null, null, null, null, providers, Options.AllowPublicDatabases));
        var avatar = User.FindFirstValue(AuthSetup.AvatarClaim);
        return Ok(new SessionView(true, true, User.Identity.Name, User.FindFirstValue(AuthSetup.DisplayNameClaim),
            string.IsNullOrEmpty(avatar) ? null : avatar, RoleAuthorizationFilter.RoleOf(User).ToString(),
            User.FindFirstValue(AuthSetup.ProviderClaim), providers));
    }

    /// <summary>
    ///     Démarre la connexion (le navigateur est redirigé vers le fournisseur, puis revient à <c>returnUrl</c> : un chemin de
    ///     l'API ou une adresse d'une origine de <c>Cors:AllowedOrigins</c>). En cas d'échec le retour porte
    ///     <c>auth_error=refused|failed</c>.
    /// </summary>
    [HttpGet("login/{provider}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Login(string provider, [FromQuery] string? returnUrl)
    {
        if (!Options.Enabled) return NotFound();
        var scheme = AuthSetup.SchemeOf(Options, provider);
        if (scheme == null) return NotFound();
        return Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, scheme);
    }

    /// <summary>Ferme la session.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Options.Enabled) await HttpContext.SignOutAsync(AuthSetup.CookieScheme);
        return Ok();
    }

    /// <summary>Une adresse de retour n'est suivie que si elle reste sur l'API ou sur une origine déclarée (pas de redirection ouverte).</summary>
    private string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        if (returnUrl.StartsWith('/')) return returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\") ? "/" : returnUrl;
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "/";
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return cors.Value.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : "/";
    }
}
