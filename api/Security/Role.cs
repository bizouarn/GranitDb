namespace GranitDB.API.Security;

/// <summary>Rôle d'un utilisateur : chacun inclut les droits des précédents.</summary>
public enum Role
{
    None = 0,
    Reader = 1,
    Editor = 2,
    Admin = 3
}

/// <summary>
///     Rôle minimal d'une route. Sans cet attribut : lecteur pour les lectures (GET, HEAD, OPTIONS), éditeur pour les
///     écritures.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class MinimumRoleAttribute(Role role) : Attribute
{
    public Role Role { get; } = role;
}
