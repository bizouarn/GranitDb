using GranitDB.API.Data;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>
///     Synchronisation planifiée : à intervalle régulier le planificateur cherche les bases dont l'échéance est passée
///     (<c>SyncIntervalMinutes</c> réglé, dernière relecture plus ancienne que l'intervalle) et relit leur source comme le fait
///     « Resynchroniser ». Un changement de schéma part aux webhooks (<c>schema.changed</c>), un échec aussi (<c>sync.failed</c>) ;
///     le résultat reste sur la base (<c>LastSyncAt</c>, <c>LastSyncOk</c>, <c>LastSyncError</c>). Une base en échec n'est pas
///     retentée avant l'intervalle suivant.
/// </summary>
public class SyncScheduler(ICatalogRepository repository, DatabaseImportService import, IOptions<CatalogOptions> options,
    ILogger<SyncScheduler> logger) : BackgroundService
{
    private readonly SemaphoreSlim _one = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse le démarrage (catalogue, sources de démarrage) se terminer avant la première échéance.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Passage du planificateur de synchronisation en échec");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.SchedulerTickSeconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // arrêt du service
        }
    }

    /// <summary>Les bases dont la relecture est due à <paramref name="now" />.</summary>
    public async Task<IReadOnlyList<DatabaseInfo>> DueAsync(DateTime now, CancellationToken ct = default)
    {
        if (!options.Value.AllowScheduledSync) return [];
        var all = await repository.GetAllAsync<DatabaseInfo>(ct);
        return all.Where(d => d.SyncIntervalMinutes is > 0
                              && (!string.IsNullOrWhiteSpace(d.SourcePath) || !string.IsNullOrWhiteSpace(d.SourceSecret))
                              && (d.LastSyncAt == null || d.LastSyncAt.Value.AddMinutes(d.SyncIntervalMinutes!.Value) <= now))
            .OrderBy(d => d.LastSyncAt ?? DateTime.MinValue).ToList();
    }

    /// <summary>Relit les bases dues ; renvoie le nombre de bases traitées (réussies ou non). Un seul passage à la fois.</summary>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(0, ct)) return 0;
        try
        {
            var done = 0;
            foreach (var database in await DueAsync(now, ct))
            {
                ct.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.Value.SyncTimeoutSeconds)));
                try
                {
                    // ResyncAsync note la réussite et prévient les webhooks d'un changement de schéma.
                    await import.ResyncAsync(database.Id, timeout.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    // Le message dit quoi corriger ; le détail technique reste dans le journal du serveur.
                    var reason = e is Exceptions.GranitExceptions ? e.Message : e is OperationCanceledException
                        ? $"La relecture a dépassé {options.Value.SyncTimeoutSeconds} s."
                        : "Erreur inattendue : consultez le journal de l'API.";
                    logger.LogWarning(e, "Synchronisation planifiée de {Database} en échec", database.Name);
                    await import.RecordSyncAsync(database.Id, reason, CancellationToken.None);
                    import.NotifySyncFailed(database, reason);
                }

                done++;
            }

            return done;
        }
        finally
        {
            _one.Release();
        }
    }
}
