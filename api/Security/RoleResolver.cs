using GranitDB.API.Options;

namespace GranitDB.API.Security;

/// <summary>Détermine le rôle d'un utilisateur authentifié d'après la configuration (<see cref="RoleRules" />).</summary>
public static class RoleResolver
{
    public static Role Resolve(RoleRules rules, string provider, string login, IEnumerable<string> groups)
    {
        var groupSet = groups.Select(g => g.Trim().ToLowerInvariant()).Where(g => g.Length > 0).ToHashSet();
        var best = Parse(rules.DefaultRole);

        bool Listed(IEnumerable<string> logins) => logins.Any(l => IsSame(l, provider, login));
        bool InGroup(IEnumerable<string> names) => names.Any(g => groupSet.Contains(g.Trim().ToLowerInvariant()));

        if (Listed(rules.Admins) || InGroup(rules.AdminGroups)) best = Role.Admin;
        else if (Listed(rules.Editors) || InGroup(rules.EditorGroups)) best = (Role)Math.Max((int)best, (int)Role.Editor);
        else if (Listed(rules.Readers) || InGroup(rules.ReaderGroups)) best = (Role)Math.Max((int)best, (int)Role.Reader);
        return best;
    }

    private static bool IsSame(string entry, string provider, string login)
    {
        entry = entry.Trim();
        return entry.Equals(login, StringComparison.OrdinalIgnoreCase) ||
               entry.Equals($"{provider}:{login}", StringComparison.OrdinalIgnoreCase);
    }

    public static Role Parse(string? value) =>
        Enum.TryParse<Role>(value, true, out var role) && Enum.IsDefined(role) ? role : Role.None;
}
