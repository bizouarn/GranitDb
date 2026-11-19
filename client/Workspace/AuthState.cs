using ApiClient = GranitDb.Client.api.Client;
using GranitDb.Client.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GranitDb.Client.Workspace;

/// <summary>
///     La session de l'utilisateur : qui est connecté, avec quel rôle (Reader, Editor, Admin), et si l'API demande une
///     connexion. Sans authentification côté API (poste de développement) tout est permis. L'API a le dernier mot : cet état ne
///     sert qu'à ne proposer que ce qui est permis.
/// </summary>
public sealed class AuthState : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly NavigationManager _navigation;

    public AuthState(IServiceProvider services, NavigationManager navigation)
    {
        _services = services;
        _navigation = navigation;
        ApiClient.Unauthorized += OnUnauthorized;
    }

    public SessionView? Session { get; private set; }

    /// <summary>Code d'erreur de la dernière tentative de connexion (<c>refused</c> ou <c>failed</c>), lu dans l'adresse de retour.</summary>
    public string? LoginError { get; private set; }

    public event Action? Changed;

    public bool Enabled => Session?.Enabled == true;
    public bool IsAuthenticated => Session?.Authenticated == true;

    /// <summary>Le serveur laisse lire sans compte les bases publiques.</summary>
    public bool AllowAnonymousRead => Session?.AllowAnonymousRead == true;

    /// <summary>Pas de compte, mais les bases publiques se lisent : l'application s'ouvre en lecture seule avec un bouton de connexion.</summary>
    public bool IsVisitor => Enabled && !IsAuthenticated && AllowAnonymousRead;

    /// <summary>La connexion est exigée et l'utilisateur n'est pas connecté : l'interface montre l'écran de connexion.</summary>
    public bool NeedsLogin => Enabled && !IsAuthenticated && !AllowAnonymousRead;

    public string? Role => Session?.Role;
    public bool CanEdit => !Enabled || Role is "Editor" or "Admin";
    public bool IsAdmin => !Enabled || Role == "Admin";
    public bool IsReadOnly => Enabled && IsAuthenticated && !CanEdit;

    public IReadOnlyList<AuthProviderView> Providers => Session?.Providers.ToArray() ?? [];

    public static string RoleLabel(string? role) => role switch
    {
        "Admin" => "Administrateur",
        "Editor" => "Éditeur",
        "Reader" => "Lecteur",
        _ => ""
    };

    public async Task LoadAsync()
    {
        LoginError = ErrorOf(_navigation.Uri);
        Session = await _services.GetRequiredService<ApiClient>().GetSessionAsync();
        Changed?.Invoke();
    }

    private const string ErrorKey = "auth_error";

    private static string? ErrorOf(string uri) =>
        new Uri(uri).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .Where(p => p[0] == ErrorKey && p.Length == 2).Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();

    /// <summary>Adresse de connexion chez ce fournisseur ; l'utilisateur revient sur la page où il était, sans le code d'erreur.</summary>
    public string LoginUrl(string provider) =>
        _services.GetRequiredService<ApiClient>().LoginUrl(provider, _navigation.GetUriWithQueryParameter(ErrorKey, (string?)null));

    public async Task LogoutAsync()
    {
        await _services.GetRequiredService<ApiClient>().LogoutAsync();
        await LoadAsync();
    }

    /// <summary>Une réponse 401 : la session a expiré, on repasse à l'écran de connexion sans attendre un rechargement.</summary>
    private void OnUnauthorized()
    {
        if (!Enabled || !IsAuthenticated) return;
        Session = new SessionView { Enabled = true, Authenticated = false, Providers = Session!.Providers, AllowAnonymousRead = Session.AllowAnonymousRead };
        Changed?.Invoke();
    }

    public void Dispose() => ApiClient.Unauthorized -= OnUnauthorized;
}
