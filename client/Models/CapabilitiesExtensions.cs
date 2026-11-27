namespace GranitDb.Client.Models;

/// <summary>La taille maximale d'un envoi est celle que l'API annonce (<c>Catalog:MaxUploadMb</c>).</summary>
public static class CapabilitiesExtensions
{
    /// <summary>Sans réponse de l'API, l'interface ne bloque rien : c'est l'API qui refuse.</summary>
    private const int FallbackMb = 1024;

    private static int MaxMb(this CapabilitiesView capabilities) => capabilities.MaxUploadMb > 0 ? capabilities.MaxUploadMb : FallbackMb;

    public static long MaxUploadBytes(this CapabilitiesView capabilities) => capabilities.MaxMb() * 1024L * 1024L;

    public static string TooLargeMessage(this CapabilitiesView capabilities) =>
        $"Fichier trop volumineux ({capabilities.MaxMb()} Mo maximum) ou illisible.";
}
