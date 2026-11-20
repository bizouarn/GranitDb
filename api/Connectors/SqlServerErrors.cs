using Microsoft.Data.SqlClient;

namespace GranitDB.API.Connectors;

/// <summary>Traduit les erreurs de connexion SQL Server en phrases qui disent quoi corriger.</summary>
public static class SqlServerErrors
{
    public static string Describe(SqlException e)
    {
        // Le message d'origine reste dans l'exception interne (journaux) ; l'utilisateur lit la consigne.
        var numbers = e.Errors.Cast<SqlError>().Select(error => error.Number).Append(e.Number).ToHashSet();
        // Une base inconnue (ou interdite) lève 4060 puis 18456 : on teste 4060 d'abord pour ne pas accuser le mot de passe.
        if (numbers.Contains(4060))
            return "Base introuvable, ou le compte n'a pas le droit de s'y connecter : vérifiez le nom de la base et les droits du compte.";
        if (numbers.Overlaps([18486, 18487, 18488]))
            return "Le compte est verrouillé ou son mot de passe a expiré : voyez avec l'administrateur du serveur.";
        if (numbers.Overlaps([18456, 18452]))
            return "Identifiants refusés par le serveur : vérifiez l'identifiant, le mot de passe et le mode d'authentification.";
        if (numbers.Overlaps([229, 230, 262, 297, 300]))
            return "Droits insuffisants : le compte doit pouvoir lire les métadonnées (autorisation VIEW DEFINITION).";
        if (e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("chaîne de certificats", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("SSL", StringComparison.Ordinal))
            return SchemaReading.CertificateNotTrusted;
        if (numbers.Overlaps([-2, -1, 2, 53, 258, 10060, 10061, 11001]) ||
            e.Message.Contains("network-related", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("réseau", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return SchemaReading.Unreachable;
        // Le message du pilote peut citer le serveur ou la base : il reste dans les journaux, pas dans la réponse.
        return $"Connexion SQL Server impossible (erreur {e.Number}) : consultez le journal de l'API pour le détail.";
    }
}
