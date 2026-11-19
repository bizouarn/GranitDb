namespace GranitDB.API.Services;

/// <summary>
///     Une valeur de configuration qui peut être un secret placé ailleurs que dans le fichier : <c>env:NOM</c> lit la
///     variable d'environnement, <c>file:/chemin</c> lit un fichier (secrets Docker ou Kubernetes, coffre monté) ; toute autre
///     valeur est prise telle quelle.
/// </summary>
public static class SecretReference
{
    public const string EnvPrefix = "env:";
    public const string FilePrefix = "file:";

    public static bool IsReference(string? value) =>
        value != null && (value.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>La valeur réelle ; lève <see cref="InvalidOperationException" /> si la variable ou le fichier est introuvable ou vide.</summary>
    public static string Resolve(string value)
    {
        if (value.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = value[EnvPrefix.Length..].Trim();
            var found = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(found)) throw new InvalidOperationException($"La variable d'environnement {name} est absente ou vide.");
            return found;
        }

        if (value.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = value[FilePrefix.Length..].Trim();
            if (!File.Exists(path)) throw new InvalidOperationException($"Le fichier de secret {path} est introuvable.");
            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0) throw new InvalidOperationException($"Le fichier de secret {path} est vide.");
            return text;
        }

        return value;
    }
}
