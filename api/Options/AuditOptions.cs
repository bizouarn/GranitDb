namespace GranitDB.API.Options;

/// <summary>Réglages du journal d'audit (section <c>Audit</c>).</summary>
public class AuditOptions
{
    public const string Section = "Audit";

    /// <summary>Durée de conservation des entrées, en jours ; 0 ou moins : on garde tout.</summary>
    public int RetentionDays { get; set; } = 365;
}
