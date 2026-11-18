using Dapper;
using GranitDB.API.Data;
using GranitDB.API.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GranitDb.Tests;

public class MigrationRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "granitdb-tests", Guid.NewGuid().ToString("N"));
    private readonly DbConnectionFactory _factory;

    public MigrationRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new DbConnectionFactory(Options.Create(new CatalogOptions { DataDirectory = _dir }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        using var db = await _factory.OpenAsync();
        return (await db.ExecuteScalarAsync<T>(sql))!;
    }

    private async Task<List<string>> NamesAsync(string type)
    {
        using var db = await _factory.OpenAsync();
        return (await db.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = @type ORDER BY name", new { type })).AsList();
    }

    [Fact]
    public void Migration_versions_are_contiguous_and_start_at_one()
    {
        var versions = CatalogMigrations.All.Select(m => m.Version).ToList();
        Assert.Equal(Enumerable.Range(1, versions.Count), versions);
    }

    [Fact]
    public void Every_migration_has_a_name_and_sql_server_statements_except_noops()
    {
        Assert.All(CatalogMigrations.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        Assert.All(CatalogMigrations.All.Where(m => m.Version is 1 or 2),
            m =>
            {
                Assert.NotEmpty(m.Sqlite);
                Assert.NotEmpty(m.SqlServer);
            });
        Assert.NotEmpty(CatalogMigrations.All.Single(m => m.Version == 4).SqlServer);
    }

    [Fact]
    public async Task Fresh_catalog_is_migrated_to_the_latest_version()
    {
        var runner = new MigrationRunner(_factory);

        var report = await runner.MigrateAsync();

        Assert.Equal(0, report.FromVersion);
        Assert.Equal(runner.LatestVersion, report.ToVersion);
        Assert.Equal(Enumerable.Range(1, CatalogMigrations.All.Count), report.Applied);
        Assert.Equal(runner.LatestVersion, await runner.CurrentVersionAsync());
        Assert.Equal(runner.LatestVersion, await ScalarAsync<int>("SELECT COUNT(*) FROM SchemaVersion"));
        var tables = await NamesAsync("table");
        Assert.Contains("Databases", tables);
        Assert.Contains("Tables", tables);
        Assert.Contains("Columns", tables);
        Assert.Contains("Relations", tables);
        Assert.Contains("IX_Columns_TableId", await NamesAsync("index"));
        Assert.Contains("IX_Relations_SecondId", await NamesAsync("index"));
    }

    [Fact]
    public async Task Migrating_twice_does_nothing_the_second_time()
    {
        var runner = new MigrationRunner(_factory);
        await runner.MigrateAsync();

        var report = await runner.MigrateAsync();

        Assert.Empty(report.Applied);
        Assert.Equal(report.FromVersion, report.ToVersion);
    }

    [Fact]
    public async Task Catalog_created_before_versioning_is_adopted_without_losing_data()
    {
        // Schéma tel que créé avant l'introduction de la table SchemaVersion.
        using (var db = await _factory.OpenAsync())
        {
            await db.ExecuteAsync("""
                CREATE TABLE "Databases" ("Id" TEXT PRIMARY KEY, "Name" TEXT NOT NULL, "Type" TEXT NOT NULL,
                    "IsPublic" INTEGER NOT NULL DEFAULT 0, "Description" TEXT, "ProjectUrl" TEXT, "DatabaseUrl" TEXT,
                    "SourcePath" TEXT, "MetaInfo" TEXT);
                CREATE TABLE "Tables" ("Id" TEXT PRIMARY KEY, "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "Name" TEXT NOT NULL, "TableType" TEXT NOT NULL DEFAULT 'Table', "Description" TEXT, "MetaInfo" TEXT);
                CREATE TABLE "Columns" ("Id" TEXT PRIMARY KEY, "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "TableId" TEXT NOT NULL REFERENCES "Tables"("Id"), "Name" TEXT NOT NULL, "DataType" TEXT,
                    "IsNullable" INTEGER NOT NULL DEFAULT 1, "IsPrimaryKey" INTEGER NOT NULL DEFAULT 0,
                    "DefaultValue" TEXT, "Description" TEXT, "MetaInfo" TEXT);
                CREATE TABLE "Relations" ("Id" TEXT PRIMARY KEY, "FirstId" TEXT NOT NULL REFERENCES "Columns"("Id"),
                    "SecondId" TEXT NOT NULL REFERENCES "Columns"("Id"), "RelationType" TEXT, "Description" TEXT,
                    "MetaInfo" TEXT);
                INSERT INTO "Databases" ("Id", "Name", "Type", "Description") VALUES ('d1', 'Ancienne', 'Sqlite', 'gardée');
                """);
        }

        var report = await new MigrationRunner(_factory).MigrateAsync();

        Assert.Equal(Enumerable.Range(1, CatalogMigrations.All.Count), report.Applied);
        Assert.Equal("gardée", await ScalarAsync<string>("SELECT \"Description\" FROM \"Databases\" WHERE \"Id\" = 'd1'"));
    }

    [Fact]
    public async Task Only_pending_migrations_are_applied()
    {
        await new MigrationRunner(_factory, CatalogMigrations.All.Take(2).ToList()).MigrateAsync();

        var report = await new MigrationRunner(_factory).MigrateAsync();

        Assert.Equal(2, report.FromVersion);
        Assert.Equal(Enumerable.Range(3, CatalogMigrations.All.Count - 2), report.Applied);
    }

    [Fact]
    public async Task Failing_migration_is_rolled_back_and_not_recorded()
    {
        var migrations = new List<Migration>
        {
            new(1, "ok", ["CREATE TABLE a (x INTEGER)"], []),
            new(2, "cassée", ["CREATE TABLE b (x INTEGER)", "THIS IS NOT SQL"], [])
        };
        var runner = new MigrationRunner(_factory, migrations);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());

        Assert.Contains("migration 2", ex.Message);
        Assert.Equal(1, await runner.CurrentVersionAsync());
        var tables = await NamesAsync("table");
        Assert.Contains("a", tables);
        Assert.DoesNotContain("b", tables); // la création de b a été annulée
    }

    [Fact]
    public async Task Two_instances_migrating_at_the_same_time_both_succeed()
    {
        for (var round = 0; round < 5; round++)
        {
            var options = Options.Create(new CatalogOptions { DataDirectory = Path.Combine(_dir, $"race{round}") });
            Directory.CreateDirectory(options.Value.DataDirectory);
            var runners = Enumerable.Range(0, 3)
                .Select(_ => new MigrationRunner(new DbConnectionFactory(options))).ToList();

            await Task.WhenAll(runners.Select(r => r.MigrateAsync()));

            Assert.Equal(runners[0].LatestVersion, await runners[0].CurrentVersionAsync());
        }
    }

    [Fact]
    public async Task Catalog_newer_than_the_application_is_refused()
    {
        var runner = new MigrationRunner(_factory);
        await runner.MigrateAsync();
        using (var db = await _factory.OpenAsync())
        {
            await db.ExecuteAsync("INSERT INTO SchemaVersion (Version, Name, AppliedAt) VALUES (99, 'futur', 'x')");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());

        Assert.Contains("plus récente", ex.Message);
    }

    [Fact]
    public async Task Repository_initialization_runs_the_migrations()
    {
        var options = Options.Create(new CatalogOptions { DataDirectory = Path.Combine(_dir, "repo") });
        var repository = new CatalogRepository(new DbConnectionFactory(options), options);

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        var runner = new MigrationRunner(new DbConnectionFactory(options));
        Assert.Equal(runner.LatestVersion, await runner.CurrentVersionAsync());
    }
}
