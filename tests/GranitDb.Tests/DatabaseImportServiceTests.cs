using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;

namespace GranitDb.Tests;

public class DatabaseImportServiceTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private Stream Sample(string name = "sample.db")
    {
        return new MemoryStream(File.ReadAllBytes(_env.CreateSampleDb(name)));
    }

    private string[] SourceFiles()
    {
        return Directory.Exists(_env.Options.SourcesDirectory)
            ? Directory.GetFiles(_env.Options.SourcesDirectory)
            : [];
    }

    [Fact]
    public async Task Import_creates_the_documented_database_without_any_server()
    {
        await using var content = Sample();

        var result = await _env.Importer.ImportSqliteAsync(content, "boutique.db", null, "Base de démo", true);

        Assert.Equal("boutique", result.Database.Name);
        Assert.Equal("Sqlite", result.Database.Type);
        Assert.Equal("Base de démo", result.Database.Description);
        Assert.Equal(5, result.Sync.TablesAdded);
        Assert.Equal(2, result.Sync.RelationsCreated);
        var stored = await _env.Repository.GetAsync<DatabaseInfo>(result.Database.Id);
        Assert.Equal("Base de démo", stored!.Description);
    }

    [Fact]
    public async Task Explicit_name_wins_over_the_file_name()
    {
        await using var content = Sample();
        var result = await _env.Importer.ImportSqliteAsync(content, "x.db", "Mon nom", null, true);
        Assert.Equal("Mon nom", result.Database.Name);
    }

    [Fact]
    public async Task Kept_file_is_stored_under_a_server_generated_name()
    {
        await using var content = Sample();

        var result = await _env.Importer.ImportSqliteAsync(content, "../../evil.db", null, null, true);

        var path = Assert.IsType<string>(result.Database.SourcePath);
        Assert.True(File.Exists(path));
        Assert.Equal(Path.GetFullPath(_env.Options.SourcesDirectory), Path.GetDirectoryName(path));
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _));
        Assert.Single(SourceFiles()); // pas de fichier temporaire oublié
    }

    [Fact]
    public async Task File_is_deleted_when_keepFile_is_false()
    {
        await using var content = Sample();

        var result = await _env.Importer.ImportSqliteAsync(content, "s.db", null, null, false);

        Assert.Null(result.Database.SourcePath);
        Assert.Empty(SourceFiles());
        Assert.Equal(5, (await _env.Repository.GetAllAsync<TableInfo>()).Count);
    }

    [Fact]
    public async Task Non_sqlite_content_is_rejected_and_leaves_nothing_behind()
    {
        await using var content = new MemoryStream(new byte[2000].Select(_ => (byte)'z').ToArray());

        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _env.Importer.ImportSqliteAsync(content, "bad.db", null, null, true));

        Assert.Empty(SourceFiles());
        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
    }

    [Fact]
    public async Task Empty_upload_is_rejected()
    {
        await using var content = new MemoryStream();
        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _env.Importer.ImportSqliteAsync(content, "e.db", null, null, true));
    }

    [Fact]
    public async Task Valid_header_but_corrupt_body_is_rejected_cleanly()
    {
        var bytes = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(bytes);
        Array.Fill(bytes, (byte)0xFF, 16, 200);
        await using var content = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _env.Importer.ImportSqliteAsync(content, "corrupt.db", null, null, true));

        Assert.Empty(SourceFiles());
        Assert.Empty(await _env.Repository.GetAllAsync<DatabaseInfo>());
    }

    [Fact]
    public async Task Oversized_upload_is_rejected()
    {
        using var small = new TestCatalog(o => o.MaxUploadMb = 1);
        await using var content = new MemoryStream(new byte[2 * 1024 * 1024]);

        var ex = await Assert.ThrowsAsync<InvalidSourceException>(() =>
            small.Importer.ImportSqliteAsync(content, "big.db", null, null, true));

        Assert.Contains("volumineux", ex.Message);
        Assert.Empty(Directory.GetFiles(small.Options.SourcesDirectory));
    }

    [Fact]
    public async Task Replace_resyncs_and_keeps_descriptions()
    {
        await using var first = Sample();
        var imported = await _env.Importer.ImportSqliteAsync(first, "s.db", null, null, true);
        var id = imported.Database.Id;
        var oldPath = imported.Database.SourcePath!;
        var customers = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", id)).Single(t => t.Name == "customers");
        customers.Description = "Mes clients";
        await _env.Repository.UpdateAsync(customers.Id, customers);

        var v2 = _env.NewFile("v2.db");
        TestCatalog.Execute(v2, TestCatalog.SampleSql + "CREATE TABLE coupons (code TEXT PRIMARY KEY);");
        await using var content = File.OpenRead(v2);
        var result = await _env.Importer.ReplaceSqliteAsync(id, content, true);

        Assert.NotNull(result);
        Assert.Equal(1, result.Sync.TablesAdded);
        Assert.Equal(id, result.Database.Id);
        Assert.NotEqual(oldPath, result.Database.SourcePath);
        Assert.False(File.Exists(oldPath)); // l'ancien fichier géré est supprimé
        Assert.True(File.Exists(result.Database.SourcePath));
        var tables = await _env.Repository.FindAsync<TableInfo>("DatabaseId", id);
        Assert.Equal("Mes clients", tables.Single(t => t.Name == "customers").Description);
        Assert.Single(await _env.Repository.GetAllAsync<DatabaseInfo>());
    }

    [Fact]
    public async Task Replace_of_unknown_database_returns_null()
    {
        await using var content = Sample();
        Assert.Null(await _env.Importer.ReplaceSqliteAsync("missing", content, true));
    }

    [Fact]
    public async Task Resync_rereads_the_kept_file()
    {
        await using var content = Sample();
        var imported = await _env.Importer.ImportSqliteAsync(content, "s.db", null, null, true);
        TestCatalog.Execute(imported.Database.SourcePath!, "CREATE TABLE extra (id INTEGER);");

        var result = await _env.Importer.ResyncAsync(imported.Database.Id);

        Assert.NotNull(result);
        Assert.Equal(1, result.Sync.TablesAdded);
    }

    [Fact]
    public async Task Resync_fails_clearly_when_no_file_was_kept()
    {
        await using var content = Sample();
        var imported = await _env.Importer.ImportSqliteAsync(content, "s.db", null, null, false);

        await Assert.ThrowsAsync<InvalidSourceException>(() => _env.Importer.ResyncAsync(imported.Database.Id));
    }

    [Fact]
    public async Task Resync_of_unknown_database_returns_null()
    {
        Assert.Null(await _env.Importer.ResyncAsync("missing"));
    }

    [Fact]
    public async Task Resync_refuses_a_source_path_outside_the_managed_folder()
    {
        var outside = _env.CreateSampleDb("outside.db");
        var database = await _env.Repository.CreateAsync(new DatabaseInfo
            { Name = "x", Type = "Sqlite", SourcePath = outside });

        await Assert.ThrowsAsync<InvalidSourceException>(() => _env.Importer.ResyncAsync(database.Id));
    }

    [Fact]
    public async Task Local_path_is_refused_by_default()
    {
        var path = _env.CreateSampleDb();
        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _env.Importer.ImportLocalSqliteAsync(path, null, null));
    }

    [Fact]
    public async Task Local_path_documents_the_file_in_place_when_allowed()
    {
        using var env = new TestCatalog(o => o.AllowLocalPaths = true);
        var path = env.CreateSampleDb("local.db");

        var result = await env.Importer.ImportLocalSqliteAsync(path, null, "locale");

        Assert.Equal("local", result.Database.Name);
        Assert.Equal(Path.GetFullPath(path), result.Database.SourcePath);
        Assert.True(File.Exists(path)); // jamais copié ni supprimé
        Assert.Empty(Directory.GetFiles(env.Options.SourcesDirectory));
        Assert.NotNull(await env.Importer.ResyncAsync(result.Database.Id));
    }

    [Fact]
    public async Task Startup_source_is_synced_with_the_imposed_id()
    {
        var path = _env.CreateSampleDb();

        var sync = await _env.Importer.SyncSourceAsync(new StartupSource
            { Id = "fixed", Type = "Sqlite", Source = path, Name = "Fixe" });

        Assert.Equal("fixed", sync.Database.Id);
        Assert.Equal("Fixe", (await _env.Repository.GetAsync<DatabaseInfo>("fixed"))!.Name);
    }

    [Fact]
    public async Task Unknown_source_type_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _env.Importer.SyncSourceAsync(new StartupSource { Id = "x", Type = "Oracle", Source = "-" }));
    }
}
