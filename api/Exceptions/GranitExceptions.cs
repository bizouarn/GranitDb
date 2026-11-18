namespace GranitDB.API.Exceptions;

public class GranitExceptions : Exception
{
    public GranitExceptions()
    {
    }

    public GranitExceptions(string message) : base(message)
    {
    }

    public GranitExceptions(string message, Exception inner) : base(message, inner)
    {
    }
}
