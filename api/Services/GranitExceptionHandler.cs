using GranitDB.API.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace GranitDB.API.Services;

/// <summary>Traduit les erreurs métier en réponses HTTP ProblemDetails.</summary>
public class GranitExceptionHandler(ILogger<GranitExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            GranitExceptions => (StatusCodes.Status400BadRequest, exception.Message),
            SqliteException { SqliteErrorCode: 19 } => (StatusCodes.Status409Conflict,
                "Conflit avec les données du catalogue (contrainte non respectée)."),
            SqlException { Number: 547 or 2627 or 2601 } => (StatusCodes.Status409Conflict,
                "Conflit avec les données du catalogue (contrainte non respectée)."),
            _ => (0, "")
        };
        if (status == 0) return false;

        logger.LogWarning(exception, "Requête refusée : {Title}", title);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title }, ct);
        return true;
    }
}
