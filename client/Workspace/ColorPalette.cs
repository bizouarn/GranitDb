namespace GranitDb.Client.Workspace;

/// <summary>Les couleurs proposées d'office pour un domaine, une étiquette ou une table (le sélecteur laisse aussi choisir une autre couleur).</summary>
public static class ColorPalette
{
    public sealed record Swatch(string Hex, string Name);

    /// <summary>Des teintes franches, lisibles avec un texte blanc ou foncé dans l'en-tête d'une table.</summary>
    public static readonly IReadOnlyList<Swatch> Colors =
    [
        new("#1976d2", "Bleu"),
        new("#0288d1", "Bleu ciel"),
        new("#00838f", "Turquoise"),
        new("#2e7d32", "Vert"),
        new("#7cb342", "Vert clair"),
        new("#f9a825", "Jaune"),
        new("#ef6c00", "Orange"),
        new("#c62828", "Rouge"),
        new("#d81b60", "Rose"),
        new("#7b1fa2", "Violet"),
        new("#3949ab", "Indigo"),
        new("#6d4c41", "Marron"),
        new("#546e7a", "Gris bleu"),
        new("#757575", "Gris")
    ];

    /// <summary>Le nom d'une couleur de la palette (« Bleu »), ou son code quand elle n'en fait pas partie.</summary>
    public static string NameOf(string? hex) =>
        hex == null ? "aucune" : Colors.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase))?.Name ?? hex;
}
