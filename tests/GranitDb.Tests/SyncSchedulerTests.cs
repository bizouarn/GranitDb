using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Synchronisation planifiée : échéances, relecture, échec, notification.</summary>
public sealed class SyncSchedulerTests : IDisposable
{
    private readonly TestCatalog _env = new(o => o.AllowLocalPaths = true);

    public void Dispose() => _env.Dispose();

    private SyncScheduler Scheduler() => new(_env.Repository, _env.Importer, Microsoft.Extensions.Options.Options.Create(_env.Options),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncScheduler>.Instance);

    private async Task<(DatabaseInfo Database, string File)> ImportAsync(int? interval = 15)
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY);");
        var imported = await _env.Importer.ImportLocalSqliteAsync(path, "Source locale", null);
        var database = imported.Database;
        database.SyncIntervalMinutes = interval;
        await _env.Repository.UpdateAsync(database.Id, database);
        return (database, path);
    }

    private Task<DatabaseInfo?> ReloadAsync(DatabaseInfo database) => _env.Repository.GetAsync<DatabaseInfo>(database.Id);

    [Fact]
    public async Task Only_databases_with_an_interval_and_a_source_that_are_overdue_are_due()
    {
        var (due, _) = await ImportAsync(15);
        var (never, _) = await ImportAsync(null);
        var (recent, _) = await ImportAsync(15);
        recent.LastSyncAt = DateTime.UtcNow.AddMinutes(-3);
        await _env.Repository.UpdateAsync(recent.Id, recent);
        var (old, _) = await ImportAsync(15);
        old.LastSyncAt = DateTime.UtcNow.AddMinutes(-20);
        await _env.Repository.UpdateAsync(old.Id, old);

        var ids = (await Scheduler().DueAsync(DateTime.UtcNow)).Select(d => d.Id).ToArray();

        Assert.Contains(due.Id, ids);
        Assert.Contains(old.Id, ids);
        Assert.DoesNotContain(never.Id, ids);
        Assert.DoesNotContain(recent.Id, ids);
        Assert.Equal([due.Id, old.Id], ids); // jamais relue d'abord, puis la plus anciennement relue
    }

    [Fact]
    public async Task A_due_database_is_reread_and_noted_and_is_not_due_again_before_its_interval()
    {
        var (database, file) = await ImportAsync(15);
        TestCatalog.Execute(file, "ALTER TABLE a ADD COLUMN nom TEXT;");
        var now = DateTime.UtcNow;

        var done = await Scheduler().RunDueAsync(now);

        Assert.Equal(1, done);
        var stored = (await ReloadAsync(database))!;
        Assert.True(stored.LastSyncOk);
        Assert.Null(stored.LastSyncError);
        Assert.NotNull(stored.LastSyncAt);
        var table = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", database.Id)).Single();
        Assert.Equal(2, (await _env.Repository.FindAsync<ColumnInfo>("TableId", table.Id)).Count);
        Assert.Equal(0, await Scheduler().RunDueAsync(now.AddMinutes(1)));
        Assert.Equal(1, await Scheduler().RunDueAsync(now.AddMinutes(16)));
    }

    [Fact]
    public async Task A_failing_source_is_noted_with_the_reason_and_retried_only_at_the_next_interval()
    {
        var (database, file) = await ImportAsync(10);
        SqliteConnection.ClearAllPools();
        File.Delete(file);
        var now = DateTime.UtcNow;

        var done = await Scheduler().RunDueAsync(now);

        Assert.Equal(1, done);
        var stored = (await ReloadAsync(database))!;
        Assert.False(stored.LastSyncOk);
        Assert.Contains("introuvable", stored.LastSyncError);
        Assert.Equal(0, await Scheduler().RunDueAsync(now.AddMinutes(5)));
        Assert.Equal(1, await Scheduler().RunDueAsync(now.AddMinutes(11)));
    }

    [Fact]
    public async Task A_successful_manual_resync_is_noted_too_and_a_failure_does_not_stop_the_other_databases()
    {
        var (broken, file) = await ImportAsync(5);
        var (healthy, healthyFile) = await ImportAsync(5);
        SqliteConnection.ClearAllPools();
        File.Delete(file);
        TestCatalog.Execute(healthyFile, "CREATE TABLE b (id INTEGER PRIMARY KEY);");

        var done = await Scheduler().RunDueAsync(DateTime.UtcNow);

        Assert.Equal(2, done);
        Assert.False((await ReloadAsync(broken))!.LastSyncOk);
        Assert.True((await ReloadAsync(healthy))!.LastSyncOk);
        Assert.Equal(2, (await _env.Repository.FindAsync<TableInfo>("DatabaseId", healthy.Id)).Count);
        await _env.Importer.ResyncAsync(healthy.Id);
        Assert.True((await ReloadAsync(healthy))!.LastSyncOk);
    }

    [Fact]
    public async Task Nothing_runs_when_scheduled_sync_is_disabled()
    {
        await ImportAsync(5);
        _env.Options.AllowScheduledSync = false;

        Assert.Empty(await Scheduler().DueAsync(DateTime.UtcNow));
        Assert.Equal(0, await Scheduler().RunDueAsync(DateTime.UtcNow));
    }

    [Fact]
    public void A_failure_can_be_subscribed_to_as_a_webhook_event()
    {
        Assert.Contains("sync.failed", WebhookEvents.Subscribable);
    }
}

/// <summary>Réglage de la planification par l'API.</summary>
public sealed class SyncScheduleApiTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _http;

    public SyncScheduleApiTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api"),
            ["Catalog:MinSyncIntervalMinutes"] = "5"
        });
        _http = _factory.CreateKestrelClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private async Task<string> ImportAsync()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY);");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "a.db");
        var imported = await (await _http.PostAsync("/api/v1/import/sqlite", content)).Content.ReadFromJsonAsync<JsonElement>();
        return imported.GetProperty("database").GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> PutAsync(string id, object body) => _http.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}/schedule", body);

    [Fact]
    public async Task An_interval_is_set_shown_and_removed()
    {
        var id = await ImportAsync();

        var set = await PutAsync(id, new { intervalMinutes = 60 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(60, (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("syncIntervalMinutes").GetInt32());
        Assert.Equal(60, (await _http.GetFromJsonAsync<JsonElement>($"/api/v1/DatabaseInfo/{id}")).GetProperty("syncIntervalMinutes").GetInt32());

        var off = await PutAsync(id, new { intervalMinutes = 0 });
        Assert.Equal(JsonValueKind.Null, (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("syncIntervalMinutes").ValueKind);
    }

    [Fact]
    public async Task Too_short_an_interval_an_unknown_database_or_a_database_without_source_are_refused()
    {
        var id = await ImportAsync();
        var manual = await (await _http.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Manuelle", type = "Sqlite" })).Content.ReadFromJsonAsync<JsonElement>();

        var tooShort = await PutAsync(id, new { intervalMinutes = 1 });
        var noSource = await PutAsync(manual.GetProperty("id").GetString()!, new { intervalMinutes = 30 });

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("minimal", await tooShort.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        Assert.Contains("pas de source", await noSource.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync("absente", new { intervalMinutes = 30 })).StatusCode);
    }

    [Fact]
    public async Task A_webhook_can_subscribe_to_sync_failures()
    {
        var response = await _http.PostAsJsonAsync("/api/v1/Webhooks", new { url = "https://hooks.exemple.fr/granit", events = new[] { "sync.failed" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("sync.failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("events")[0].GetString());
    }
}
