using System.Security.Claims;
using GranitDB.API.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Security;

/// <summary>
///     Applique les rôles à toutes les routes des contrôleurs quand <c>Auth:Enabled</c> : 401 sans session, 403 si le rôle
///     est insuffisant. Lecture : lecteur ; écriture : éditeur ; <see cref="MinimumRoleAttribute" /> relève ou précise
///     (administrateur pour le journal d'audit, les webhooks, les sources SQL Server). Une écriture doit porter
///     l'en-tête <see cref="ClientHeader" /> : un site tiers ne peut pas l'ajouter sans autorisation CORS, ce qui ferme la
///     voie aux requêtes forgées avec le cookie de session.
/// </summary>
public sealed class RoleAuthorizationFilter(IOptions<AuthOptions> options, PublicAccess publicAccess) : IAsyncAuthorizationFilter
{
    public const string ClientHeader = "X-GranitDb-Client";

    private static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS"];

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!options.Value.Enabled) return;
        var http = context.HttpContext;
        var isRead = SafeMethods.Contains(http.Request.Method, StringComparer.OrdinalIgnoreCase);

        if (!isRead && !http.Request.Headers.ContainsKey(ClientHeader))
        {
            context.Result = Problem(StatusCodes.Status400BadRequest, $"L'en-tête {ClientHeader} est obligatoire pour une écriture.");
            return;
        }

        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()) return;

        if (http.User.Identity?.IsAuthenticated != true)
        {
            // Sans compte : seules les lectures d'une base publique passent.
            if (isRead && await publicAccess.MayReadAsync(context.RouteData.Values["controller"]?.ToString(),
                    context.RouteData.Values["action"]?.ToString(), context.RouteData.Values!, http.RequestAborted))
                return;
            context.Result = Problem(StatusCodes.Status401Unauthorized, "Connexion requise.");
            return;
        }

        var required = metadata.OfType<MinimumRoleAttribute>().LastOrDefault()?.Role ?? (isRead ? Role.Reader : Role.Editor);
        if (RoleOf(http.User) < required)
            context.Result = Problem(StatusCodes.Status403Forbidden, $"Cette action demande le rôle {Label(required)}.");
    }

    public static Role RoleOf(ClaimsPrincipal user) => RoleResolver.Parse(user.FindFirst(ClaimTypes.Role)?.Value);

    private static string Label(Role role) => role switch
    {
        Role.Admin => "administrateur",
        Role.Editor => "éditeur",
        _ => "lecteur"
    };

    private static ObjectResult Problem(int status, string detail) =>
        new(new ProblemDetails { Status = status, Title = detail }) { StatusCode = status };
}
