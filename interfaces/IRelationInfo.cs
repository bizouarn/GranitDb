namespace GranitDb.Interfaces;

public interface IRelationInfo : IInfoBase
{
    public string FirstId { get; set; }

    public string SecondId { get; set; }

    public string RelationType { get; set; } // Ex: One-to-One, One-to-Many, Many-to-Many
}