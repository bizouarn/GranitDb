using System.ComponentModel.DataAnnotations;
using GranitDB.API.Connectors;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace GranitDB.API.Models.Inputs;

/// <summary>
///     Connexion à une base client/serveur à documenter : SQL Server (<c>Engine</c> = <c>SqlServer</c>, défaut), PostgreSQL
///     (<c>PostgreSql</c>) ou MySQL (<c>MySql</c>). L'API lit uniquement le schéma (tables, colonnes, clés, index) : un compte
///     qui n'a que le droit de lire les métadonnées, sans accès aux données, suffit (sauf pour le profilage des colonnes).
/// </summary>
public class ServerConnectionInput : IValidatableObject
{
    public const string SqlAuth = "Sql";
    public const string IntegratedAuth = "Integrated";

    /// <summary><c>SqlServer</c> (défaut), <c>PostgreSql</c> ou <c>MySql</c>.</summary>
    [RegularExpression("^(SqlServer|PostgreSql|MySql)$", ErrorMessage = "Engine doit valoir SqlServer, PostgreSql ou MySql.")]
    public string Engine { get; set; } = ServerEngines.SqlServer.Type;

    [Required, StringLength(255, MinimumLength = 1)]
    public string Server { get; set; } = "";

    [Range(1, 65535)] public int? Port { get; set; }

    [Required, StringLength(128, MinimumLength = 1)]
    public string Database { get; set; } = "";

    /// <summary><c>Sql</c> (identifiant et mot de passe) ou <c>Integrated</c> (compte Windows du processus de l'API, SQL Server seulement).</summary>
    [RegularExpression("^(Sql|Integrated)$", ErrorMessage = "AuthMode doit valoir Sql ou Integrated.")]
    public string AuthMode { get; set; } = SqlAuth;

    [StringLength(128)] public string? User { get; set; }

    [StringLength(256)] public string? Password { get; set; }

    /// <summary>Chiffre la connexion (recommandé). Désactivé, le mot de passe circule en clair sur le réseau.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Accepte un certificat non signé par une autorité connue (serveur de test) : à éviter en production.</summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Schémas à documenter (tous si vide). Sans effet pour MySQL, où la base est l'unité.</summary>
    public List<string>? Schemas { get; set; }

    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Nom affiché de la base (par défaut : son nom côté serveur).</summary>
    [StringLength(255)] public string? Name { get; set; }

    [StringLength(4000)] public string? Description { get; set; }

    /// <summary>Le moteur choisi (SQL Server si <see cref="Engine" /> est inconnu : la validation le refuse avant).</summary>
    public ServerEngine ServerEngine => ServerEngines.Of(Engine);

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (AuthMode == IntegratedAuth && !ServerEngine.SupportsIntegrated)
            yield return new ValidationResult($"L'authentification intégrée n'existe pas pour {ServerEngine.DisplayName}.", [nameof(AuthMode)]);
        if (AuthMode == SqlAuth && string.IsNullOrWhiteSpace(User))
            yield return new ValidationResult("User est obligatoire avec l'authentification SQL.", [nameof(User)]);
        if (AuthMode == SqlAuth && string.IsNullOrEmpty(Password))
            yield return new ValidationResult("Password est obligatoire avec l'authentification SQL.", [nameof(Password)]);
        if (Schemas != null && Schemas.Any(string.IsNullOrWhiteSpace))
            yield return new ValidationResult("Un nom de schéma ne peut pas être vide.", [nameof(Schemas)]);
    }

    /// <summary>Chaîne de connexion construite avec le générateur du pilote : aucune valeur ne peut y injecter d'option.</summary>
    public string BuildConnectionString() => ServerEngine.Type switch
    {
        PostgreSqlSchemaConnector.TypeName => BuildPostgreSql(),
        MySqlSchemaConnector.TypeName => BuildMySql(),
        _ => BuildSqlServer()
    };

    private string BuildSqlServer()
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

    private string BuildPostgreSql()
    {
        return new NpgsqlConnectionStringBuilder
        {
            Host = Server.Trim(),
            Port = Port ?? ServerEngines.PostgreSql.DefaultPort,
            Database = Database.Trim(),
            Username = User!.Trim(),
            Password = Password!,
            SslMode = Encrypt ? SslMode.Require : SslMode.Disable,
            // SslMode.Require ne vérifie pas le certificat ; verrouiller la vérification demande un fichier d'autorité que l'API n'a pas.
            Timeout = TimeoutSeconds,
            ApplicationName = "GranitDb",
            Pooling = false,
            // Documenter ne modifie jamais la source : toute transaction est en lecture seule.
            Options = "-c default_transaction_read_only=on"
        }.ConnectionString;
    }

    private string BuildMySql()
    {
        return new MySqlConnectionStringBuilder
        {
            Server = Server.Trim(),
            Port = (uint)(Port ?? ServerEngines.MySql.DefaultPort),
            Database = Database.Trim(),
            UserID = User!.Trim(),
            Password = Password!,
            SslMode = !Encrypt ? MySqlSslMode.None : TrustServerCertificate ? MySqlSslMode.Required : MySqlSslMode.VerifyCA,
            ConnectionTimeout = (uint)TimeoutSeconds,
            ApplicationName = "GranitDb",
            Pooling = false
        }.ConnectionString;
    }

    /// <summary>« serveur[:port] / base » : sans identifiant ni mot de passe, affichable.</summary>
    public string Label => $"{Server.Trim()}{(Port == null ? "" : ":" + Port)} / {Database.Trim()}";

    /// <summary>Schémas choisis, sans doublon ni espaces ; <c>null</c> pour « tous » (et toujours pour MySQL).</summary>
    public string[]? SelectedSchemas =>
        ServerEngine.SupportsSchemas && Schemas is { Count: > 0 }
            ? Schemas.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : null;
}
