using Dapper;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>Filtres de lecture du journal d'audit (tous facultatifs, cumulés).</summary>
public record AuditQuery(string? Actor = null, string? Method = null, int? Status = null, string? Controller = null,
    string? EntityId = null, DateTime? From = null, DateTime? To = null);

/// <summary>Journal d'audit : écrit par <see cref="AuditMiddleware" />, lu par <c>AuditController</c>.</summary>
public class AuditLog(IDbConnectionFactory factory, IOptions<AuditOptions>? options = null)
{
    private long _lastPurgeTicks;

    /// <summary>Supprime les entrées plus vieilles que la durée de conservation (au plus une fois par heure).</summary>
    private async Task PurgeIfDueAsync(CancellationToken ct)
    {
        var days = options?.Value.RetentionDays ?? 0;
        var now = DateTime.UtcNow;
        var last = Interlocked.Read(ref _lastPurgeTicks);
        if (days <= 0 || now.Ticks - last < TimeSpan.TicksPerHour ||
            Interlocked.CompareExchange(ref _lastPurgeTicks, now.Ticks, last) != last) return;
        await PurgeAsync(now.AddDays(-days), ct);
    }

    /// <summary>Supprime les entrées antérieures à la date donnée ; renvoie leur nombre.</summary>
    public async Task<int> PurgeAsync(DateTime before, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM \"AuditLog\" WHERE \"At\" < @before", new { before }, cancellationToken: ct));
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO "AuditLog" ("Id", "At", "Actor", "Method", "Path", "Status", "Controller", "EntityId", "Ip")
            VALUES (@Id, @At, @Actor, @Method, @Path, @Status, @Controller, @EntityId, @Ip)
            """,
            new
            {
                Id = Guid.NewGuid().ToString(), At = DateTime.UtcNow, Actor = Trim(entry.Actor, 255),
                Method = Trim(entry.Method, 10) ?? "?", Path = Trim(entry.Path, 500) ?? "/", entry.Status,
                Controller = Trim(entry.Controller, 100), EntityId = Trim(entry.EntityId, 100), Ip = Trim(entry.Ip, 64)
            }, cancellationToken: ct));
        await PurgeIfDueAsync(ct);
    }

    private static (string Where, DynamicParameters Parameters) Filter(AuditQuery q)
    {
        var parameters = new DynamicParameters();
        var clauses = new List<string>();
        void Add(string clause, string name, object? value)
        {
            clauses.Add(clause);
            parameters.Add(name, value);
        }

        if (!string.IsNullOrWhiteSpace(q.Actor)) Add("\"Actor\" = @actor", "actor", q.Actor.Trim());
        if (!string.IsNullOrWhiteSpace(q.Method)) Add("\"Method\" = @method", "method", q.Method.Trim().ToUpperInvariant());
        if (q.Status != null) Add("\"Status\" = @status", "status", q.Status);
        if (!string.IsNullOrWhiteSpace(q.Controller)) Add("\"Controller\" = @controller", "controller", q.Controller.Trim());
        if (!string.IsNullOrWhiteSpace(q.EntityId)) Add("\"EntityId\" = @entityId", "entityId", q.EntityId.Trim());
        if (q.From != null) Add("\"At\" >= @from", "from", q.From.Value.ToUniversalTime());
        if (q.To != null) Add("\"At\" <= @to", "to", q.To.Value.ToUniversalTime());
        return (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), parameters);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, int skip, int take, CancellationToken ct = default)
    {
        var (where, parameters) = Filter(query);
        parameters.Add("skip", skip);
        parameters.Add("take", take);
        var sql = factory.Provider == "Sqlite"
            ? $"SELECT * FROM \"AuditLog\"{where} ORDER BY \"At\" DESC, \"Id\" LIMIT @take OFFSET @skip"
            : $"SELECT * FROM \"AuditLog\"{where} ORDER BY \"At\" DESC, \"Id\" OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        using var connection = await factory.OpenAsync(ct);
        return (await connection.QueryAsync<AuditEntry>(new CommandDefinition(sql, parameters, cancellationToken: ct))).AsList();
    }

    public async Task<int> CountAsync(AuditQuery query, CancellationToken ct = default)
    {
        var (where, parameters) = Filter(query);
        using var connection = await factory.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM \"AuditLog\"{where}", parameters, cancellationToken: ct));
    }
}

/// <summary>
///     Note dans le journal d'audit chaque requête qui modifie le catalogue (POST, PUT, DELETE, PATCH) et chaque export de
///     la documentation. Le contenu des requêtes n'est jamais enregistré. Une panne du journal n'interrompt pas la requête.
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, ILogger<AuditMiddleware> logger)
{
    private static readonly string[] ReadMethods = ["GET", "HEAD", "OPTIONS"];

    public async Task InvokeAsync(HttpContext context, AuditLog log)
    {
        await next(context);

        var path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        var isExport = path.Contains("/export/", StringComparison.OrdinalIgnoreCase);
        if (ReadMethods.Contains(context.Request.Method, StringComparer.OrdinalIgnoreCase) && !isExport) return;

        try
        {
            var route = context.Request.RouteValues;
            string? Route(string key) => route.TryGetValue(key, out var value) ? value?.ToString() : null;
            await log.RecordAsync(new AuditEntry
            {
                Actor = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null,
                Method = context.Request.Method.ToUpperInvariant(),
                Path = path,
                Status = context.Response.StatusCode,
                Controller = Route("controller"),
                EntityId = Route("id") ?? Route("databaseId") ?? Route("tableId"),
                Ip = context.Connection.RemoteIpAddress?.ToString()
            }, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Écriture du journal d'audit impossible");
        }
    }
}
