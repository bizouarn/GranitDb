using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;

namespace GranitDB.API.Models.Inputs;

/// <summary>
///     Connexion à une base SQL Server à documenter. L'API lit uniquement le schéma (tables, colonnes, clés, index) : un
///     compte qui n'a que <c>VIEW DEFINITION</c>, sans accès aux données, suffit.
/// </summary>
public class SqlServerConnectionInput : IValidatableObject
{
    public const string SqlAuth = "Sql";
    public const string IntegratedAuth = "Integrated";

    [Required, StringLength(255, MinimumLength = 1)]
    public string Server { get; set; } = "";

    [Range(1, 65535)] public int? Port { get; set; }

    [Required, StringLength(128, MinimumLength = 1)]
    public string Database { get; set; } = "";

    /// <summary><c>Sql</c> (identifiant et mot de passe) ou <c>Integrated</c> (compte Windows du processus de l'API).</summary>
    [RegularExpression("^(Sql|Integrated)$", ErrorMessage = "AuthMode doit valoir Sql ou Integrated.")]
    public string AuthMode { get; set; } = SqlAuth;

    [StringLength(128)] public string? User { get; set; }

    [StringLength(256)] public string? Password { get; set; }

    /// <summary>Chiffre la connexion (recommandé). Désactivé, le mot de passe circule en clair sur le réseau.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Accepte un certificat non signé par une autorité connue (serveur de test) : à éviter en production.</summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Schémas à documenter (tous si vide). <c>dbo</c> compris.</summary>
    public List<string>? Schemas { get; set; }

    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Nom affiché de la base (par défaut : son nom côté serveur).</summary>
    [StringLength(255)] public string? Name { get; set; }

    [StringLength(4000)] public string? Description { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (AuthMode == SqlAuth && string.IsNullOrWhiteSpace(User))
            yield return new ValidationResult("User est obligatoire avec l'authentification SQL.", [nameof(User)]);
        if (AuthMode == SqlAuth && string.IsNullOrEmpty(Password))
            yield return new ValidationResult("Password est obligatoire avec l'authentification SQL.", [nameof(Password)]);
        if (Schemas != null && Schemas.Any(string.IsNullOrWhiteSpace))
            yield return new ValidationResult("Un nom de schéma ne peut pas être vide.", [nameof(Schemas)]);
    }

    /// <summary>Chaîne de connexion construite avec le générateur du pilote : aucune valeur ne peut y injecter d'option.</summary>
    public string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Port == null ? Server.Trim() : $"{Server.Trim()},{Port}",
            InitialCatalog = Database.Trim(),
            Encrypt = Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = TimeoutSeconds,
            ApplicationName = "GranitDb",
            // La documentation ne lit que des métadonnées : on le dit au serveur (réplicas en lecture seule).
            ApplicationIntent = ApplicationIntent.ReadOnly
        };
        if (AuthMode == IntegratedAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = User!.Trim();
            builder.Password = Password!;
        }

        return builder.ConnectionString;
    }

    /// <summary>« serveur[:port] / base » : sans identifiant ni mot de passe, affichable.</summary>
    public string Label => $"{Server.Trim()}{(Port == null ? "" : ":" + Port)} / {Database.Trim()}";

    /// <summary>Schémas choisis, sans doublon ni espaces ; <c>null</c> pour « tous ».</summary>
    public string[]? SelectedSchemas =>
        Schemas is { Count: > 0 } ? Schemas.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
}
