using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace GranitDB.API.Services;

/// <summary>Chiffre les secrets stockés dans le catalogue (connexions des sources) : on ne les garde jamais en clair.</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <summary>Déchiffre ; lève <see cref="CryptographicException" /> si les clés ont changé ou le contenu est altéré.</summary>
    string Unprotect(string protectedSecret);
}

/// <summary>
///     Chiffrement par ASP.NET Core Data Protection. Les clés sont des fichiers du dossier
///     <c>Catalog:KeysDirectory</c> (par défaut <c>DataDirectory/keys</c>) : à sauvegarder avec le catalogue, et à ne pas
///     confondre avec lui — un catalogue copié sans ses clés ne livre pas les connexions.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("GranitDb.SourceSecrets.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}
