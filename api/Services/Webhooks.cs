using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

public static class WebhookEvents
{
    /// <summary>Une resynchronisation a changé le schéma documenté (tables, colonnes ou index ajoutés, modifiés, retirés).</summary>
    public const string SchemaChanged = "schema.changed";

    /// <summary>Une base vient d'être importée dans le catalogue (SQLite, SQL Server ou chemin local).</summary>
    public const string DatabaseImported = "database.imported";

    /// <summary>Une base a été supprimée du catalogue (la base source n'est jamais touchée).</summary>
    public const string DatabaseDeleted = "database.deleted";

    /// <summary>Une resynchronisation planifiée a échoué (source injoignable, compte refusé…) ; la charge dit pourquoi, sans secret.</summary>
    public const string SyncFailed = "sync.failed";

    /// <summary>Envoi d'essai demandé par <c>POST Webhooks/{id}/test</c>.</summary>
    public const string Ping = "ping";

    public static readonly string[] Subscribable = [SchemaChanged, DatabaseImported, DatabaseDeleted, SyncFailed];
}

/// <summary>Quelles adresses un webhook a le droit de joindre.</summary>
public static class WebhookUrlPolicy
{
    /// <summary>Message d'erreur si l'URL est refusée, <c>null</c> si elle convient (avant même de la résoudre).</summary>
    public static string? Validate(string? url, bool allowPrivate)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "L'adresse du webhook doit être une URL http ou https complète.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "L'adresse du webhook ne doit pas contenir d'identifiants.";
        if (allowPrivate) return null;
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPrivate(ip)))
            return "Cette adresse est locale ou privée : elle n'est pas autorisée (Webhooks:AllowPrivateHosts).";
        return null;
    }

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                   (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 0 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}

/// <summary>Lecture et écriture des webhooks et de leurs envois dans le catalogue.</summary>
public class WebhookStore(IDbConnectionFactory factory)
{
    public async Task<IReadOnlyList<WebhookInfo>> ListAsync(CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        return (await connection.QueryAsync<WebhookInfo>(new CommandDefinition(
            "SELECT * FROM \"Webhooks\" ORDER BY \"CreatedAt\", \"Id\"", cancellationToken: ct))).AsList();
    }

    public async Task<WebhookInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<WebhookInfo>(new CommandDefinition(
            "SELECT * FROM \"Webhooks\" WHERE \"Id\" = @id", new { id }, cancellationToken: ct));
    }

    public async Task SaveAsync(WebhookInfo hook, bool isNew, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        var sql = isNew
            ? """
              INSERT INTO "Webhooks" ("Id", "DatabaseId", "Url", "Secret", "Events", "IsActive", "Description", "CreatedAt")
              VALUES (@Id, @DatabaseId, @Url, @Secret, @Events, @IsActive, @Description, @CreatedAt)
              """
            : """
              UPDATE "Webhooks" SET "DatabaseId" = @DatabaseId, "Url" = @Url, "Secret" = @Secret, "Events" = @Events,
                  "IsActive" = @IsActive, "Description" = @Description WHERE "Id" = @Id
              """;
        await connection.ExecuteAsync(new CommandDefinition(sql, hook, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @id", new { id }, cancellationToken: ct));
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM \"Webhooks\" WHERE \"Id\" = @id", new { id }, cancellationToken: ct)) > 0;
    }

    /// <summary>Webhooks actifs qui veulent cet événement pour cette base (ceux d'une seule base, ou de toutes).</summary>
    public async Task<IReadOnlyList<WebhookInfo>> MatchingAsync(string eventName, string? databaseId, CancellationToken ct = default)
    {
        return (await ListAsync(ct)).Where(h => h.IsActive && (h.DatabaseId == null || h.DatabaseId == databaseId) &&
                                                 h.Events.Split(',', StringSplitOptions.TrimEntries).Contains(eventName)).ToList();
    }

    public async Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(string webhookId, int take, CancellationToken ct = default)
    {
        var sql = factory.Provider == "Sqlite"
            ? "SELECT * FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @webhookId ORDER BY \"At\" DESC, \"Id\" LIMIT @take"
            : "SELECT * FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @webhookId ORDER BY \"At\" DESC, \"Id\" OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY";
        using var connection = await factory.OpenAsync(ct);
        return (await connection.QueryAsync<WebhookDelivery>(new CommandDefinition(sql, new { webhookId, take }, cancellationToken: ct))).AsList();
    }

    /// <summary>Note un envoi et ne garde que les <paramref name="keep" /> plus récents du webhook.</summary>
    public async Task RecordDeliveryAsync(WebhookDelivery delivery, int keep, CancellationToken ct = default)
    {
        using var connection = await factory.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO "WebhookDeliveries" ("Id", "WebhookId", "At", "Event", "StatusCode", "Success", "Attempts", "Error")
            VALUES (@Id, @WebhookId, @At, @Event, @StatusCode, @Success, @Attempts, @Error)
            """, delivery, cancellationToken: ct));

        var sql = factory.Provider == "Sqlite"
            ? "SELECT \"At\" FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @id ORDER BY \"At\" DESC LIMIT 1 OFFSET @keep"
            : "SELECT \"At\" FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @id ORDER BY \"At\" DESC OFFSET @keep ROWS FETCH NEXT 1 ROWS ONLY";
        var oldest = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition(sql,
            new { id = delivery.WebhookId, keep = Math.Max(1, keep) }, cancellationToken: ct));
        if (oldest != null)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM \"WebhookDeliveries\" WHERE \"WebhookId\" = @id AND \"At\" <= @at",
                new { id = delivery.WebhookId, at = oldest.Value }, cancellationToken: ct));
    }
}

/// <summary>
///     Envoie les événements aux webhooks : en arrière-plan (un import ne attend pas un destinataire lent), avec
///     reprises, signature HMAC-SHA256 du corps (<c>X-GranitDb-Signature: sha256=…</c>) quand un secret est défini, sans
///     suivre les redirections, et en refusant les adresses locales ou privées (vérifié à la connexion, donc aussi
///     contre un nom de domaine qui pointerait vers le réseau interne).
/// </summary>
public sealed class WebhookDispatcher : BackgroundService
{
    private readonly Channel<(string Event, string? DatabaseId, object Payload)> _queue =
        Channel.CreateUnbounded<(string, string?, object)>();

    private readonly WebhookStore _store;
    private readonly ISecretProtector _protector;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly HttpClient _http;

    public WebhookDispatcher(WebhookStore store, ISecretProtector protector, IOptions<WebhookOptions> options,
        ILogger<WebhookDispatcher> logger)
    {
        _store = store;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = ConnectAsync, UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)) };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = _options.AllowPrivateHosts ? addresses : addresses.Where(a => !WebhookUrlPolicy.IsPrivate(a)).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException("Adresse locale ou privée refusée.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Met l'événement en file : l'envoi se fait en arrière-plan.</summary>
    public void Enqueue(string eventName, string? databaseId, object payload) =>
        _queue.Writer.TryWrite((eventName, databaseId, payload));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (eventName, databaseId, payload) in _queue.Reader.ReadAllAsync(stoppingToken))
            try
            {
                foreach (var hook in await MatchingWithRetryAsync(eventName, databaseId, stoppingToken))
                    await DeliverAsync(hook, eventName, payload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Envoi des webhooks impossible pour {Event}", eventName);
            }
    }

    /// <summary>Lit les webhooks concernés ; une lecture qui échoue (base verrouillée un instant…) est retentée avant d'abandonner.</summary>
    private async Task<IReadOnlyList<WebhookInfo>> MatchingWithRetryAsync(string eventName, string? databaseId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
            try
            {
                return await _store.MatchingAsync(eventName, databaseId, ct);
            }
            catch (Exception e) when (attempt < 4 && e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Lecture des webhooks impossible (essai {Attempt}), nouvel essai", attempt);
                await Task.Delay(300 * attempt, ct);
            }
    }

    /// <summary>Envoie maintenant (avec les reprises) et note le résultat.</summary>
    public async Task<WebhookDelivery> DeliverAsync(WebhookInfo hook, string eventName, object payload, CancellationToken ct)
    {
        var deliveryId = Guid.NewGuid().ToString();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { id = deliveryId, @event = eventName, occurredAt = DateTime.UtcNow, data = payload },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string? secret = null;
        if (!string.IsNullOrEmpty(hook.Secret))
            try { secret = _protector.Unprotect(hook.Secret); }
            catch (CryptographicException) { /* clés changées : envoi sans signature plutôt que pas d'envoi, noté dans l'erreur */ }

        var attempts = Math.Max(1, _options.MaxAttempts);
        int? status = null;
        string? error = null;
        var done = 0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            done = attempt;
            try
            {
                if (WebhookUrlPolicy.Validate(hook.Url, _options.AllowPrivateHosts) is { } refused) throw new HttpRequestException(refused);
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                request.Headers.Add("X-GranitDb-Event", eventName);
                request.Headers.Add("X-GranitDb-Delivery", deliveryId);
                if (secret != null)
                    request.Headers.Add("X-GranitDb-Signature", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant());
                using var response = await _http.SendAsync(request, ct);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) { error = null; break; }
                error = $"Réponse {(int)response.StatusCode}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) throw;
                status = null;
                error = e is TaskCanceledException ? "Délai dépassé" : e.Message;
            }

            if (attempt < attempts)
            {
                var delays = _options.RetryDelaysMs;
                var wait = delays.Length == 0 ? 0 : delays[Math.Min(attempt - 1, delays.Length - 1)];
                if (wait > 0) await Task.Delay(wait, ct);
            }
        }

        var delivery = new WebhookDelivery
        {
            Id = deliveryId, WebhookId = hook.Id, At = DateTime.UtcNow, Event = eventName, StatusCode = status,
            Success = error == null, Attempts = done, Error = error?[..Math.Min(error.Length, 500)]
        };
        await _store.RecordDeliveryAsync(delivery, _options.DeliveriesKept, ct);
        return delivery;
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
