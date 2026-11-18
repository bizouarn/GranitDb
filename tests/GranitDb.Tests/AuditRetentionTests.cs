using GranitDB.API.Models;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GranitDb.Tests;

/// <summary>Durée de conservation du journal d'audit : les vieilles entrées partent à l'écriture suivante.</summary>
public sealed class AuditRetentionTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private static AuditEntry Entry() => new() { Method = "POST", Path = "/DatabaseInfo", Status = 201 };

    [Fact]
    public async Task Entries_older_than_the_retention_are_purged_when_a_new_one_is_written()
    {
        await _env.Repository.InitializeAsync();
        var log = new AuditLog(_env.Factory, Options.Create(new AuditOptions { RetentionDays = 30 }));
        await log.RecordAsync(Entry()); // première écriture : purge (rien à supprimer)
        Assert.Equal(1, await log.CountAsync(new AuditQuery()));

        Assert.Equal(1, await log.PurgeAsync(DateTime.UtcNow.AddDays(1))); // tout est plus vieux que demain
        Assert.Equal(0, await log.CountAsync(new AuditQuery()));
    }

    [Fact]
    public async Task A_retention_of_zero_keeps_everything()
    {
        await _env.Repository.InitializeAsync();
        var log = new AuditLog(_env.Factory, Options.Create(new AuditOptions { RetentionDays = 0 }));

        await log.RecordAsync(Entry());
        await log.RecordAsync(Entry());

        Assert.Equal(2, await log.CountAsync(new AuditQuery()));
    }
}
