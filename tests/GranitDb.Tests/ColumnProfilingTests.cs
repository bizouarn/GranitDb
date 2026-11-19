using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Profilage léger : des comptes calculés par la source, jamais une valeur.</summary>
public sealed class ColumnProfilingTests : IDisposable
{
    private readonly TestCatalog _env;

    public ColumnProfilingTests()
    {
        _env = new TestCatalog(o => o.AllowProfiling = true);
    }

    public void Dispose() => _env.Dispose();

    private ColumnProfilingService Service(bool allowed = true, int maxTables = 100)
    {
        _env.Options.AllowProfiling = allowed;
        _env.Options.ProfilingMaxTables = maxTables;
        return new ColumnProfilingService(_env.Repository, _env.Importer, [new SqliteColumnProfiler(), new SqlServerColumnProfiler()],
            Microsoft.Extensions.Options.Options.Create(_env.Options));
    }

    /// <summary>Importe une base de test : <c>clients</c> (5 lignes dont 3 sans note), <c>vide</c> (0 ligne) et une vue.</summary>
    private async Task<(string Database, Dictionary<string, TableInfo> Tables, string File)> ImportAsync()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, """
            CREATE TABLE clients (id INTEGER PRIMARY KEY, pays TEXT, note TEXT, secret TEXT);
            INSERT INTO clients VALUES (1, 'FR', NULL, 'MOT-DE-PASSE-1'), (2, 'FR', 'a', 'MOT-DE-PASSE-2'), (3, 'BE', NULL, 'MOT-DE-PASSE-3'),
                                       (4, 'FR', 'a', 'MOT-DE-PASSE-4'), (5, 'BE', NULL, 'MOT-DE-PASSE-5');
            CREATE TABLE vide (id INTEGER PRIMARY KEY, valeur TEXT);
            CREATE VIEW v_clients AS SELECT id, pays FROM clients;
            """);
        await using var stream = File.OpenRead(path);
        var imported = await _env.Importer.ImportSqliteAsync(stream, "boutique.db", "Boutique", null, true);
        var tables = (await _env.Repository.GetAllAsync<TableInfo>([new GranitDB.API.Models.CatalogFilter("DatabaseId", imported.Database.Id)]))
            .ToDictionary(t => t.Name);
        return (imported.Database.Id, tables, imported.Database.SourcePath!);
    }

    [Fact]
    public void A_column_is_classified_from_its_counts()
    {
        Assert.Equal("empty", ProfileKind.Of(0, 0, 0));
        Assert.Equal("empty", ProfileKind.Of(10, 10, 0));
        Assert.Equal("unknown", ProfileKind.Of(10, 0, null));
        Assert.Equal("constant", ProfileKind.Of(10, 2, 1));
        Assert.Equal("unique", ProfileKind.Of(10, 0, 10));
        Assert.Equal("unique", ProfileKind.Of(10, 4, 6)); // identifiant facultatif : autant de valeurs que de lignes non nulles
        Assert.Equal("low", ProfileKind.Of(1000, 0, 15));
        Assert.Equal("low", ProfileKind.Of(100000, 0, 500)); // moins de 1 % de valeurs distinctes
        Assert.Equal("high", ProfileKind.Of(1000, 0, 400));
    }

    [Fact]
    public async Task Profiling_a_table_counts_rows_nulls_and_distinct_values_per_column()
    {
        var (_, tables, _) = await ImportAsync();

        var profile = (await Service().ProfileTableAsync(tables["clients"].Id))!;

        Assert.Equal(5, profile.RowCount);
        Assert.NotNull(profile.ProfiledAt);
        var byName = profile.Columns.ToDictionary(c => c.Column);
        Assert.Equal(["id", "pays", "note", "secret"], profile.Columns.Select(c => c.Column).ToArray());
        Assert.Equal((0L, 5L, "unique"), (byName["id"].NullCount, byName["id"].DistinctCount!.Value, byName["id"].Kind));
        Assert.Equal((0L, 2L, "low"), (byName["pays"].NullCount, byName["pays"].DistinctCount!.Value, byName["pays"].Kind));
        Assert.Equal((3L, 1L, "constant"), (byName["note"].NullCount, byName["note"].DistinctCount!.Value, byName["note"].Kind));
        Assert.Equal(0.6, byName["note"].NullRatio);
        Assert.All(profile.Columns, c => Assert.Equal(5, c.RowCount));
    }

    [Fact]
    public async Task The_profile_never_carries_a_value_from_the_source()
    {
        var (_, tables, _) = await ImportAsync();
        var service = Service();

        await service.ProfileTableAsync(tables["clients"].Id);

        var stored = await _env.Repository.GetAllAsync<ColumnProfileInfo>();
        var everything = JsonSerializer.Serialize(stored) + JsonSerializer.Serialize(await service.GetAsync(tables["clients"].Id));
        Assert.DoesNotContain("MOT-DE-PASSE", everything);
        Assert.DoesNotContain("\"FR\"", everything);
    }

    [Fact]
    public async Task Profiling_again_replaces_the_previous_profile_and_an_empty_table_is_profiled_too()
    {
        var (_, tables, file) = await ImportAsync();
        var service = Service();
        await service.ProfileTableAsync(tables["clients"].Id);
        TestCatalog.Execute(file, "INSERT INTO clients VALUES (6, 'NL', 'b', 'x');");

        var again = (await service.ProfileTableAsync(tables["clients"].Id))!;

        Assert.Equal(6, again.RowCount);
        Assert.Equal(4, (await _env.Repository.FindAsync<ColumnProfileInfo>("TableId", tables["clients"].Id)).Count);
        var empty = (await service.ProfileTableAsync(tables["vide"].Id))!;
        Assert.Equal(0, empty.RowCount);
        Assert.All(empty.Columns, c => Assert.Equal("empty", c.Kind));
    }

    [Fact]
    public async Task A_table_that_was_never_profiled_has_an_empty_profile_and_an_unknown_table_is_missing()
    {
        var (_, tables, _) = await ImportAsync();
        var service = Service();

        var none = (await service.GetAsync(tables["clients"].Id))!;

        Assert.Null(none.ProfiledAt);
        Assert.Empty(none.Columns);
        Assert.Null(await service.GetAsync("absente"));
        Assert.Null(await service.ProfileTableAsync("absente"));
    }

    [Fact]
    public async Task Profiling_is_refused_when_disabled_and_for_views()
    {
        var (_, tables, _) = await ImportAsync();

        var disabled = await Assert.ThrowsAsync<GranitExceptions>(() => Service(false).ProfileTableAsync(tables["clients"].Id));
        var view = await Assert.ThrowsAsync<GranitExceptions>(() => Service().ProfileTableAsync(tables["v_clients"].Id));

        Assert.Contains("Catalog:AllowProfiling", disabled.Message);
        Assert.Contains("vue", view.Message);
    }

    [Fact]
    public async Task Profiling_a_database_goes_through_its_tables_oldest_profile_first_by_batches_of_the_configured_size()
    {
        var (database, tables, _) = await ImportAsync();
        var service = Service(maxTables: 1);

        var first = (await service.ProfileDatabaseAsync(database))!;
        var second = (await service.ProfileDatabaseAsync(database))!;

        Assert.Equal((1, 0, 1), (first.TablesProfiled, first.TablesFailed, first.TablesRemaining)); // 2 tables, la vue est ignorée
        Assert.Equal(1, second.TablesProfiled);
        Assert.NotNull((await service.GetAsync(tables["clients"].Id))!.ProfiledAt);
        Assert.NotNull((await service.GetAsync(tables["vide"].Id))!.ProfiledAt);
        Assert.Null(await service.ProfileDatabaseAsync("absente"));
    }

    [Fact]
    public async Task A_table_that_fails_is_reported_and_does_not_stop_the_others()
    {
        var (database, tables, file) = await ImportAsync();
        TestCatalog.Execute(file, "DROP VIEW v_clients; DROP TABLE vide;"); // le catalogue connaît encore « vide »

        var run = (await Service().ProfileDatabaseAsync(database))!;

        Assert.Equal((1, 1), (run.TablesProfiled, run.TablesFailed));
        Assert.Contains(run.Errors, e => e.StartsWith("vide :"));
        Assert.NotNull((await Service().GetAsync(tables["clients"].Id))!.ProfiledAt);
    }

    [Fact]
    public async Task Deleting_a_database_or_a_table_removes_its_profiles()
    {
        var (database, tables, _) = await ImportAsync();
        var service = Service();
        await service.ProfileDatabaseAsync(database);
        Assert.NotEmpty(await _env.Repository.GetAllAsync<ColumnProfileInfo>());

        await _env.Repository.DeleteAsync<TableInfo>(tables["vide"].Id);
        Assert.DoesNotContain(await _env.Repository.GetAllAsync<ColumnProfileInfo>(), p => p.TableId == tables["vide"].Id);
        await _env.Repository.DeleteAsync<DatabaseInfo>(database);

        Assert.Empty(await _env.Repository.GetAllAsync<ColumnProfileInfo>());
    }

    [Fact]
    public void SQL_Server_names_are_quoted_and_split_by_schema()
    {
        Assert.Equal("[dbo].[orders]", SqlServerColumnProfiler.TableReference("orders"));
        Assert.Equal("[sales].[Orders]", SqlServerColumnProfiler.TableReference("sales.Orders"));
        Assert.Equal("[dbo].[we]]ird]", SqlServerColumnProfiler.TableReference("we]ird"));
    }
}

/// <summary>Routes du profilage : réservées aux administrateurs, désactivées par défaut.</summary>
public sealed class ColumnProfilingApiTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    private async Task<(KestrelApiFactory Factory, HttpClient Http, string Table, string Database)> StartAsync(bool allow)
    {
        var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-" + Guid.NewGuid().ToString("N")),
            ["Catalog:AllowProfiling"] = allow ? "true" : "false"
        });
        var http = factory.CreateKestrelClient();
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE a (id INTEGER PRIMARY KEY, nom TEXT); INSERT INTO a VALUES (1, 'x'), (2, NULL);");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "a.db");
        var imported = await (await http.PostAsync("/api/v1/import/sqlite", content)).Content.ReadFromJsonAsync<JsonElement>();
        var database = imported.GetProperty("database").GetProperty("id").GetString()!;
        var tables = await http.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/database/{database}");
        return (factory, http, tables[0].GetProperty("id").GetString()!, database);
    }

    [Fact]
    public async Task A_table_is_profiled_through_the_api_and_the_profile_is_read_back_with_the_capability_advertised()
    {
        var (factory, http, table, database) = await StartAsync(true);
        using var _ = factory;
        using var __ = http;

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v1/Capabilities");
        Assert.True(capabilities.GetProperty("allowProfiling").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await http.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/{table}/profile")).GetProperty("profiledAt").ValueKind);

        var profiled = await http.PostAsync($"/api/v1/TableInfo/{table}/profile", null);
        Assert.Equal(HttpStatusCode.OK, profiled.StatusCode);
        var read = await http.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/{table}/profile");
        Assert.Equal(2, read.GetProperty("rowCount").GetInt64());
        var nom = read.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("column").GetString() == "nom");
        Assert.Equal(1, nom.GetProperty("nullCount").GetInt64());
        Assert.Equal(0.5, nom.GetProperty("nullRatio").GetDouble());

        var run = await http.PostAsync($"/api/v1/DatabaseInfo/{database}/profile", null);
        Assert.Equal(1, (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tablesProfiled").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/v1/TableInfo/absente/profile", null)).StatusCode);
    }

    [Fact]
    public async Task Profiling_answers_400_with_the_reason_when_it_is_disabled_the_default()
    {
        var (factory, http, table, _) = await StartAsync(false);
        using var _1 = factory;
        using var _2 = http;

        var response = await http.PostAsync($"/api/v1/TableInfo/{table}/profile", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AllowProfiling", await response.Content.ReadAsStringAsync());
        Assert.False((await http.GetFromJsonAsync<JsonElement>("/api/v1/Capabilities")).GetProperty("allowProfiling").GetBoolean());
        SqliteConnection.ClearAllPools();
    }
}
