namespace GranitDB.API.Exceptions;

/// <summary>La source à documenter (fichier ou connexion) est absente, invalide ou illisible.</summary>
public class InvalidSourceException : GranitExceptions
{
    public InvalidSourceException(string message) : base(message)
    {
    }

    public InvalidSourceException(string message, Exception inner) : base(message, inner)
    {
    }
}
