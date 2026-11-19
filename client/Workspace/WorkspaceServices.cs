using Microsoft.Extensions.DependencyInjection;

namespace GranitDb.Client.Workspace;

public static class WorkspaceServices
{
    /// <summary>Enregistre l'état de l'espace de travail, les notifications et le cache du catalogue.</summary>
    public static IServiceCollection AddGranitWorkspace(this IServiceCollection services)
    {
        services.AddScoped<WorkspaceState>();
        services.AddScoped<Notifier>();
        services.AddScoped<CatalogCache>();
        services.AddScoped<AuthState>();
        return services;
    }
}
