using GranitDB.API.Connectors;
using GranitDB.API.Models.Descriptions;

namespace GranitDb.Tests;

/// <summary>Index des tables et ordre des colonnes : lecture SQLite, synchronisation, suppression en cascade.</summary>
public class IndexesAndOrderTests : IDisposable
{
    private const string IndexSql = """
        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE UNIQUE INDEX ux_customers_email ON customers(email);
        CREATE INDEX ix_lines_sku_qty ON order_lines(sku, qty);
        CREATE INDEX ix_tags_lower ON tags(lower(label));
        """;

    private readonly TestCatalog _env = new();
    private readonly SqliteSchemaConnector _connector = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private string SampleWithIndexes(string name = "idx.db")
    {
        var path = _env.CreateSampleDb(name);
        TestCatalog.Execute(path, IndexSql);
        return path;
    }

    private async Task<IReadOnlyList<IndexInfo>> IndexesAsync(string id = "db1")
    {
        return await _env.Repository.FindAsync<IndexInfo>("DatabaseId", id);
    }

    [Fact]
    public async Task Connector_reads_created_indexes_with_their_columns_in_order()
    {
        var schema = await _connector.ReadSchemaAsync(SampleWithIndexes());

        Assert.Contains(new SourceIndex("orders", "ix_orders_customer", false, ["customer_id"]), schema.Indexes,
            SourceIndexComparer.Instance);
        var unique = schema.Indexes.Single(i => i.Name == "ux_customers_email");
        Assert.True(unique.IsUnique);
        Assert.Equal(new[] { "email" }, unique.Columns);
        Assert.Equal(new[] { "sku", "qty" }, schema.Indexes.Single(i => i.Name == "ix_lines_sku_qty").Columns);
    }

    [Fact]
    public async Task Connector_marks_expression_columns_and_excludes_primary_key_indexes()
    {
        var schema = await _connector.ReadSchemaAsync(SampleWithIndexes());

        Assert.Equal(new[] { "<expression>" }, schema.Indexes.Single(i => i.Name == "ix_tags_lower").Columns);
        // order_lines a une clé primaire composite (index automatique d'origine 'pk') : non listée.
        Assert.DoesNotContain(schema.Indexes, i => i.Table == "order_lines" && i.Name.StartsWith("sqlite_autoindex"));
    }

    [Fact]
    public async Task Connector_reads_unique_constraints_as_unique_indexes()
    {
        var path = _env.NewFile("uq.db");
        TestCatalog.Execute(path, "CREATE TABLE u (id INTEGER PRIMARY KEY, a TEXT UNIQUE, b TEXT, UNIQUE (a, b));");

        var schema = await _connector.ReadSchemaAsync(path);

        Assert.Equal(2, schema.Indexes.Count);
        Assert.All(schema.Indexes, i => Assert.True(i.IsUnique));
        Assert.Contains(schema.Indexes, i => i.Columns.SequenceEqual(["a"]));
        Assert.Contains(schema.Indexes, i => i.Columns.SequenceEqual(["a", "b"]));
    }

    [Fact]
    public async Task Views_have_no_indexes()
    {
        var schema = await _connector.ReadSchemaAsync(SampleWithIndexes());
        Assert.DoesNotContain(schema.Indexes, i => i.Table == "customer_emails");
    }

    [Fact]
    public async Task Sync_stores_indexes_and_reports_them()
    {
        var schema = await _connector.ReadSchemaAsync(SampleWithIndexes());

        var result = await _env.Synchronizer.SyncAsync("db1", schema);

        Assert.Equal(4, result.IndexesAdded);
        var indexes = await IndexesAsync();
        Assert.Equal(4, indexes.Count);
        var composite = indexes.Single(i => i.Name == "ix_lines_sku_qty");
        Assert.Equal("sku,qty", composite.ColumnNames);
        Assert.False(composite.IsUnique);
        Assert.True(indexes.Single(i => i.Name == "ux_customers_email").IsUnique);
    }

    [Fact]
    public async Task Resync_updates_changed_indexes_and_removes_dropped_ones()
    {
        var path = SampleWithIndexes();
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));
        var before = (await IndexesAsync()).Single(i => i.Name == "ix_orders_customer");

        TestCatalog.Execute(path,
            "DROP INDEX ix_orders_customer; CREATE INDEX ix_orders_customer ON orders(customer_id, note); DROP INDEX ix_tags_lower;");
        var result = await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        Assert.Equal(0, result.IndexesAdded);
        Assert.Equal(1, result.IndexesRemoved);
        var indexes = await IndexesAsync();
        Assert.Equal(3, indexes.Count);
        var after = indexes.Single(i => i.Name == "ix_orders_customer");
        Assert.Equal(before.Id, after.Id); // mise à jour sur place, descriptions conservables
        Assert.Equal("customer_id,note", after.ColumnNames);
    }

    [Fact]
    public async Task Index_descriptions_survive_a_resync()
    {
        var path = SampleWithIndexes();
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));
        var index = (await IndexesAsync()).Single(i => i.Name == "ux_customers_email");
        index.Description = "Un compte par adresse";
        await _env.Repository.UpdateAsync(index.Id, index);

        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        Assert.Equal("Un compte par adresse", (await IndexesAsync()).Single(i => i.Name == "ux_customers_email").Description);
    }

    [Fact]
    public async Task Resync_without_changes_creates_no_index()
    {
        var path = SampleWithIndexes();
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        var result = await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        Assert.Equal(0, result.IndexesAdded + result.IndexesRemoved);
        Assert.Equal(4, (await IndexesAsync()).Count);
    }

    [Fact]
    public async Task Dropping_a_table_removes_its_indexes()
    {
        var path = SampleWithIndexes();
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        TestCatalog.Execute(path, "DROP TABLE tags;");
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        Assert.DoesNotContain(await IndexesAsync(), i => i.Name == "ix_tags_lower");
        Assert.Equal(3, (await IndexesAsync()).Count);
    }

    [Fact]
    public async Task Deleting_a_database_or_a_table_removes_their_indexes()
    {
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(SampleWithIndexes()));
        var table = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "db1")).Single(t => t.Name == "customers");

        await _env.Repository.DeleteAsync<TableInfo>(table.Id);
        Assert.DoesNotContain(await IndexesAsync(), i => i.TableId == table.Id);

        await _env.Repository.DeleteAsync<DatabaseInfo>("db1");
        Assert.Empty(await _env.Repository.GetAllAsync<IndexInfo>());
    }

    [Fact]
    public async Task Column_positions_follow_the_source_order()
    {
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(_env.CreateSampleDb()));
        var table = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "db1")).Single(t => t.Name == "customers");

        var columns = (await _env.Repository.FindAsync<ColumnInfo>("TableId", table.Id)).OrderBy(c => c.Position).ToList();

        Assert.Equal(new[] { "id", "email", "nickname", "created" }, columns.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, columns.Select(c => c.Position).ToArray());
    }

    [Fact]
    public async Task Reordered_source_columns_update_positions_without_recreating_them()
    {
        var path = _env.NewFile("order.db");
        TestCatalog.Execute(path, "CREATE TABLE t (a TEXT, b TEXT, c TEXT);");
        await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));
        var ids = (await _env.Repository.GetAllAsync<ColumnInfo>()).ToDictionary(c => c.Name, c => c.Id);

        File.Delete(path);
        TestCatalog.Execute(path, "CREATE TABLE t (c TEXT, a TEXT, b TEXT);");
        var result = await _env.Synchronizer.SyncAsync("db1", await _connector.ReadSchemaAsync(path));

        Assert.Equal(0, result.ColumnsAdded + result.ColumnsRemoved);
        var after = (await _env.Repository.GetAllAsync<ColumnInfo>()).OrderBy(c => c.Position).ToList();
        Assert.Equal(new[] { "c", "a", "b" }, after.Select(c => c.Name).ToArray());
        Assert.All(after, c => Assert.Equal(ids[c.Name], c.Id));
    }

    private sealed class SourceIndexComparer : IEqualityComparer<SourceIndex>
    {
        public static readonly SourceIndexComparer Instance = new();

        public bool Equals(SourceIndex? x, SourceIndex? y)
        {
            return x != null && y != null && x.Table == y.Table && x.Name == y.Name && x.IsUnique == y.IsUnique &&
                   x.Columns.SequenceEqual(y.Columns);
        }

        public int GetHashCode(SourceIndex obj)
        {
            return HashCode.Combine(obj.Table, obj.Name);
        }
    }
}
