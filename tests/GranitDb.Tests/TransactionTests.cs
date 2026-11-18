using System.Diagnostics;
using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDB.API.Services;
using Microsoft.Extensions.Options;

namespace GranitDb.Tests;

/// <summary>Dépôt de test qui échoue à la demande, pour vérifier que les opérations sont atomiques.</summary>
internal sealed class FailingRepository(ICatalogRepository inner, Func<string, bool> shouldFail) : ICatalogRepository
{
    private void Hook(string operation)
    {
        if (shouldFail(operation)) throw new InvalidOperationException($"panne simulée : {operation}");
    }

    public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

    public Task<TResult> InTransactionAsync<TResult>(Func<ICatalogRepository, Task<TResult>> work,
        CancellationToken ct = default)
    {
        return inner.InTransactionAsync(repo => work(new FailingRepository(repo, shouldFail)), ct);
    }

    public Task<T?> GetAsync<T>(string id, CancellationToken ct = default) where T : InfoBase => inner.GetAsync<T>(id, ct);

    public Task<IReadOnlyList<RelationInfo>> GetRelationsOfDatabaseAsync(string databaseId, CancellationToken ct = default)
        => inner.GetRelationsOfDatabaseAsync(databaseId, ct);

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken ct = default) where T : InfoBase => inner.GetAllAsync<T>(ct);

    public Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take, CancellationToken ct = default) where T : InfoBase
        => inner.GetPageAsync<T>(skip, take, ct);

    public Task<int> CountAsync<T>(CancellationToken ct = default) where T : InfoBase => inner.CountAsync<T>(ct);

    public Task<IReadOnlyList<T>> GetAllAsync<T>(IReadOnlyList<GranitDB.API.Models.CatalogFilter> filters,
        CancellationToken ct = default) where T : InfoBase => inner.GetAllAsync<T>(filters, ct);

    public Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take,
        IReadOnlyList<GranitDB.API.Models.CatalogFilter> filters, CancellationToken ct = default) where T : InfoBase
        => inner.GetPageAsync<T>(skip, take, filters, ct);

    public Task<int> CountAsync<T>(IReadOnlyList<GranitDB.API.Models.CatalogFilter> filters,
        CancellationToken ct = default) where T : InfoBase => inner.CountAsync<T>(filters, ct);

    public Task<T?> ModifyAsync<T>(string id, Action<T> apply, CancellationToken ct = default, string? changedBy = null)
        where T : InfoBase
    {
        Hook($"modify {typeof(T).Name}");
        return inner.ModifyAsync(id, apply, ct, changedBy);
    }

    public Task RecordHistoryAsync<T>(string entityId, string field, string? oldValue, string? newValue,
        string? changedBy = null, CancellationToken ct = default) where T : InfoBase
        => inner.RecordHistoryAsync<T>(entityId, field, oldValue, newValue, changedBy, ct);

    public Task<IReadOnlyList<GranitDB.API.Models.HistoryEntry>> GetHistoryAsync(string? entityId, int skip, int take,
        CancellationToken ct = default) => inner.GetHistoryAsync(entityId, skip, take, ct);

    public Task<int> CountHistoryAsync(string? entityId, CancellationToken ct = default)
        => inner.CountHistoryAsync(entityId, ct);

    public Task<IReadOnlyList<GranitDB.API.Models.SearchHit>> SearchAsync(string term, int limitPerKind, CancellationToken ct = default)
        => inner.SearchAsync(term, limitPerKind, ct);

    public Task<IReadOnlyList<T>> FindAsync<T>(string property, string value, CancellationToken ct = default)
        where T : InfoBase => inner.FindAsync<T>(property, value, ct);

    public Task<T> CreateAsync<T>(T info, CancellationToken ct = default) where T : InfoBase
    {
        Hook($"create {typeof(T).Name}");
        return inner.CreateAsync(info, ct);
    }

    public Task<bool> UpdateAsync<T>(string id, T info, CancellationToken ct = default) where T : InfoBase
    {
        Hook($"update {typeof(T).Name}");
        return inner.UpdateAsync(id, info, ct);
    }

    public Task<bool> DeleteAsync<T>(string id, CancellationToken ct = default) where T : InfoBase
    {
        Hook($"delete {typeof(T).Name}");
        return inner.DeleteAsync<T>(id, ct);
    }
}

public class TransactionTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    [Fact]
    public async Task Work_that_completes_is_committed()
    {
        await _env.Repository.InTransactionAsync(async repo =>
        {
            await repo.CreateAsync(new DatabaseInfo { Id = "a", Name = "a", Type = "Sqlite" });
            return 0;
        });

        Assert.NotNull(await _env.Repository.GetAsync<DatabaseInfo>("a"));
    }

    [Fact]
    public async Task Work_that_throws_is_rolled_back_entirely()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Repository.InTransactionAsync<int>(async repo =>
        {
            await repo.CreateAsync(new DatabaseInfo { Id = "a", Name = "a", Type = "Sqlite" });
            await repo.CreateAsync(new TableInfo { Id = "t", Name = "t", DatabaseId = "a" });
            throw new InvalidOperationException("boom");
        }));

        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<TableInfo>());
    }

    [Fact]
    public async Task Reads_inside_the_transaction_see_its_own_writes_but_others_do_not()
    {
        await _env.Repository.InTransactionAsync(async repo =>
        {
            await repo.CreateAsync(new DatabaseInfo { Id = "a", Name = "a", Type = "Sqlite" });
            Assert.NotNull(await repo.GetAsync<DatabaseInfo>("a"));
            return 0;
        });
        Assert.NotNull(await _env.Repository.GetAsync<DatabaseInfo>("a"));
    }

    [Fact]
    public async Task Nested_transactions_share_the_outer_one()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Repository.InTransactionAsync<int>(async outer =>
        {
            await outer.InTransactionAsync(async inner =>
            {
                await inner.CreateAsync(new DatabaseInfo { Id = "a", Name = "a", Type = "Sqlite" });
                return 0;
            });
            throw new InvalidOperationException("l'extérieur échoue après l'intérieur");
        }));

        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>()); // l'intérieur est annulé avec l'extérieur
    }

    [Fact]
    public async Task Delete_inside_a_transaction_is_rolled_back_with_it()
    {
        var db = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "d", Type = "Sqlite" });
        await _env.Repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Repository.InTransactionAsync<int>(async repo =>
        {
            await repo.DeleteAsync<DatabaseInfo>(db.Id);
            throw new InvalidOperationException("boom");
        }));

        Assert.NotNull(await _env.Repository.GetAsync<DatabaseInfo>(db.Id));
        Assert.Single(await _env.Repository.GetAllAsync<TableInfo>());
    }

    [Fact]
    public async Task Sync_that_fails_midway_leaves_the_catalog_untouched()
    {
        var schema = await new SqliteSchemaConnector().ReadSchemaAsync(_env.CreateSampleDb());
        var failing = new FailingRepository(_env.Repository, op => op == "create RelationInfo");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SchemaSynchronizer(failing).SyncAsync("db1", schema));

        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<TableInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<ColumnInfo>());
    }

    [Fact]
    public async Task Failed_resync_keeps_the_previous_state()
    {
        var path = _env.CreateSampleDb();
        var connector = new SqliteSchemaConnector();
        await _env.Synchronizer.SyncAsync("db1", await connector.ReadSchemaAsync(path));
        TestCatalog.Execute(path, "DROP TABLE tags; ALTER TABLE orders ADD COLUMN extra TEXT;");
        var failing = new FailingRepository(_env.Repository, op => op == "delete TableInfo");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SchemaSynchronizer(failing).SyncAsync("db1", await connector.ReadSchemaAsync(path)));

        Assert.Equal(5, (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "db1")).Count);
        Assert.DoesNotContain(await _env.Repository.GetAllAsync<ColumnInfo>(), c => c.Name == "extra");
    }

    [Fact]
    public async Task Import_that_fails_after_the_sync_leaves_no_database_and_no_file()
    {
        var failing = new FailingRepository(_env.Repository, op => op == "update DatabaseInfo");
        var options = Options.Create(_env.Options);
        var importer = new DatabaseImportService(failing, new SchemaSynchronizer(failing), _env.Connectors, options, _env.Protector, _env.Webhooks);
        await using var content = new MemoryStream(File.ReadAllBytes(_env.CreateSampleDb()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ImportSqliteAsync(content, "s.db", null, null, true));

        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<TableInfo>());
        Assert.Empty(Directory.GetFiles(_env.Options.SourcesDirectory));
    }

    [Fact]
    public async Task Cancelled_import_leaves_nothing_behind()
    {
        using var cts = new CancellationTokenSource();
        var cancelling = new FailingRepository(_env.Repository, op =>
        {
            if (op == "create ColumnInfo") cts.Cancel();
            return false;
        });
        var importer = new DatabaseImportService(cancelling, new SchemaSynchronizer(cancelling), _env.Connectors,
            Options.Create(_env.Options), _env.Protector, _env.Webhooks);
        await using var content = new MemoryStream(File.ReadAllBytes(_env.CreateSampleDb()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importer.ImportSqliteAsync(content, "s.db", null, null, true, cts.Token));

        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
        Assert.Empty(Directory.GetFiles(_env.Options.SourcesDirectory));
    }

    [Fact]
    public async Task Relations_of_a_database_are_found_in_sql()
    {
        await _env.Synchronizer.SyncAsync("one", await new SqliteSchemaConnector().ReadSchemaAsync(_env.CreateSampleDb("one.db")));
        await _env.Synchronizer.SyncAsync("two", await new SqliteSchemaConnector().ReadSchemaAsync(_env.CreateSampleDb("two.db")));

        Assert.Equal(2, (await _env.Repository.GetRelationsOfDatabaseAsync("one")).Count);
        Assert.Equal(2, (await _env.Repository.GetRelationsOfDatabaseAsync("two")).Count);
        Assert.Empty(await _env.Repository.GetRelationsOfDatabaseAsync("none"));
    }

    [Fact]
    public async Task Large_schema_is_synchronized_in_a_few_seconds()
    {
        var tables = Enumerable.Range(0, 150).Select(t => new SourceTable($"table_{t}", "Table",
            Enumerable.Range(0, 20).Select(c => new SourceColumn($"col_{c}", "TEXT", true, c == 0, null, c)).ToList())).ToList();
        var foreignKeys = Enumerable.Range(1, 149)
            .Select(t => new SourceForeignKey($"table_{t}", "col_1", "table_0", "col_0")).ToList();
        var schema = new SourceSchema("big", "Sqlite", tables, foreignKeys);

        var watch = Stopwatch.StartNew();
        var first = await _env.Synchronizer.SyncAsync("big", schema);
        var firstTime = watch.Elapsed;
        watch.Restart();
        var second = await _env.Synchronizer.SyncAsync("big", schema);
        var secondTime = watch.Elapsed;

        Assert.Equal(150, first.TablesAdded);
        Assert.Equal(3000, first.ColumnsAdded);
        Assert.Equal(149, first.RelationsCreated);
        Assert.Equal(0, second.ColumnsAdded + second.ColumnsUpdated);
        // Une transaction par synchronisation : bien en dessous de ces bornes larges (évite les faux positifs).
        Assert.True(firstTime < TimeSpan.FromSeconds(15), $"première synchro : {firstTime}");
        Assert.True(secondTime < TimeSpan.FromSeconds(15), $"seconde synchro : {secondTime}");
    }
}
