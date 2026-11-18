namespace GranitDb.Interfaces;

public interface IRelationInfo : IInfoBase
{
    /// <summary>Id de la colonne référencée (côté « un »).</summary>
    public string FirstId { get; set; }

    /// <summary>Id de la colonne qui référence (côté « plusieurs »).</summary>
    public string SecondId { get; set; }

    public string RelationType { get; set; } // Ex: One-to-One, One-to-Many, Many-to-Many
}
