using System.Security.Cryptography;
using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;

namespace GranitDb.Tests;

public class SqliteSchemaConnectorTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly SqliteSchemaConnector _connector = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    [Fact]
    public async Task Reads_tables_views_and_excludes_internal_tables()
    {
        var path = _env.CreateSampleDb();

        var schema = await _connector.ReadSchemaAsync(path);

        Assert.Equal("Sqlite", schema.Type);
        Assert.Equal("sample", schema.DatabaseName);
        Assert.Equal(new[] { "customer_emails", "customers", "order_lines", "orders", "tags" },
            schema.Tables.Select(t => t.Name).ToArray());
        Assert.Equal("View", schema.Tables.Single(t => t.Name == "customer_emails").TableType);
        Assert.All(schema.Tables.Where(t => t.Name != "customer_emails"), t => Assert.Equal("Table", t.TableType));
        Assert.DoesNotContain(schema.Tables, t => t.Name.StartsWith("sqlite_"));
    }

    [Fact]
    public async Task Reads_column_types_nullability_default_and_primary_key()
    {
        var schema = await _connector.ReadSchemaAsync(_env.CreateSampleDb());
        var customers = schema.Tables.Single(t => t.Name == "customers").Columns;

        var id = customers.Single(c => c.Name == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsNullable);
        Assert.Equal("INTEGER", id.DataType);

        var email = customers.Single(c => c.Name == "email");
        Assert.False(email.IsNullable);
        Assert.False(email.IsPrimaryKey);

        Assert.True(customers.Single(c => c.Name == "nickname").IsNullable);

        var created = customers.Single(c => c.Name == "created");
        Assert.Equal("VARCHAR(30)", created.DataType);
        Assert.Equal("'now'", created.DefaultValue);

        Assert.Equal(new[] { 0, 1, 2, 3 }, customers.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public async Task Column_without_declared_type_is_reported_as_ANY()
    {
        var schema = await _connector.ReadSchemaAsync(_env.CreateSampleDb());
        var sku = schema.Tables.Single(t => t.Name == "order_lines").Columns.Single(c => c.Name == "sku");
        Assert.Equal("ANY", sku.DataType);
    }

    [Fact]
    public async Task Composite_primary_key_marks_every_column()
    {
        var schema = await _connector.ReadSchemaAsync(_env.CreateSampleDb());
        var lines = schema.Tables.Single(t => t.Name == "order_lines").Columns;
        Assert.True(lines.Single(c => c.Name == "order_id").IsPrimaryKey);
        Assert.True(lines.Single(c => c.Name == "line_no").IsPrimaryKey);
        Assert.False(lines.Single(c => c.Name == "qty").IsPrimaryKey);
    }

    [Fact]
    public async Task Reads_explicit_and_implicit_foreign_keys()
    {
        var schema = await _connector.ReadSchemaAsync(_env.CreateSampleDb());

        Assert.Contains(new SourceForeignKey("orders", "customer_id", "customers", "id"), schema.ForeignKeys);
        // « REFERENCES orders » sans colonne : résolu sur la clé primaire du parent.
        Assert.Contains(new SourceForeignKey("order_lines", "order_id", "orders", "id"), schema.ForeignKeys);
        Assert.Equal(2, schema.ForeignKeys.Count);
    }

    [Fact]
    public async Task Empty_database_has_no_tables()
    {
        var path = _env.NewFile("empty.db");
        TestCatalog.Execute(path, "CREATE TABLE t(x); DROP TABLE t;");

        var schema = await _connector.ReadSchemaAsync(path);

        Assert.Empty(schema.Tables);
        Assert.Empty(schema.ForeignKeys);
    }

    [Fact]
    public async Task Table_names_with_quotes_and_spaces_are_supported()
    {
        var path = _env.NewFile("odd.db");
        TestCatalog.Execute(path, "CREATE TABLE \"my \"\"odd\"\" table\" (\"a b\" TEXT);");

        var schema = await _connector.ReadSchemaAsync(path);

        var table = Assert.Single(schema.Tables);
        Assert.Equal("my \"odd\" table", table.Name);
        Assert.Equal("a b", Assert.Single(table.Columns).Name);
    }

    [Fact]
    public async Task Missing_file_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidSourceException>(() =>
            _connector.ReadSchemaAsync(_env.NewFile("nope.db")));
    }

    [Fact]
    public async Task Non_sqlite_file_is_rejected()
    {
        var path = _env.NewFile("garbage.db");
        await File.WriteAllTextAsync(path, new string('x', 500));

        await Assert.ThrowsAsync<InvalidSourceException>(() => _connector.ReadSchemaAsync(path));
    }

    [Fact]
    public async Task Reading_never_modifies_the_source_file()
    {
        var path = _env.CreateSampleDb();
        var before = Hash(path);
        var writeTime = File.GetLastWriteTimeUtc(path);

        await _connector.ReadSchemaAsync(path);

        Assert.Equal(before, Hash(path));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(path));
        // Pas de fichier journal laissé à côté de la source.
        Assert.False(File.Exists(path + "-journal"));
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public async Task Source_file_can_be_deleted_after_reading()
    {
        var path = _env.CreateSampleDb();
        await _connector.ReadSchemaAsync(path);
        File.Delete(path); // échoue si une connexion reste ouverte (Windows)
        Assert.False(File.Exists(path));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
