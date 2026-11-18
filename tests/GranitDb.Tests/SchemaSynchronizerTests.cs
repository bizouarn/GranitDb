using GranitDB.API.Connectors;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;

namespace GranitDb.Tests;

public class SchemaSynchronizerTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly SqliteSchemaConnector _connector = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task<SyncResult> SyncAsync(string path, string id = "db1", string? name = null)
    {
        var schema = await _connector.ReadSchemaAsync(path);
        return await _env.Synchronizer.SyncAsync(id, schema, name);
    }

    private async Task<TableInfo> TableAsync(string name, string id = "db1")
    {
        return (await _env.Repository.FindAsync<TableInfo>("DatabaseId", id)).Single(t => t.Name == name);
    }

    private async Task<ColumnInfo> ColumnAsync(string table, string column, string id = "db1")
    {
        var t = await TableAsync(table, id);
        return (await _env.Repository.FindAsync<ColumnInfo>("TableId", t.Id)).Single(c => c.Name == column);
    }

    [Fact]
    public async Task First_sync_creates_database_tables_columns_and_relations()
    {
        var result = await SyncAsync(_env.CreateSampleDb(), name: "Boutique");

        Assert.Equal(5, result.TablesAdded);
        Assert.Equal(0, result.TablesRemoved);
        Assert.Equal(4 + 3 + 4 + 2 + 2, result.ColumnsAdded);
        Assert.Equal(2, result.RelationsCreated);

        var db = await _env.Repository.GetAsync<DatabaseInfo>("db1");
        Assert.NotNull(db);
        Assert.Equal("Boutique", db.Name);
        Assert.Equal("Sqlite", db.Type);
    }

    [Fact]
    public async Task Database_name_defaults_to_the_file_name()
    {
        await SyncAsync(_env.CreateSampleDb("inventaire.db"));
        Assert.Equal("inventaire", (await _env.Repository.GetAsync<DatabaseInfo>("db1"))!.Name);
    }

    [Fact]
    public async Task Nullability_is_recorded_correctly()
    {
        await SyncAsync(_env.CreateSampleDb());

        Assert.False((await ColumnAsync("customers", "email")).IsNullable);
        Assert.True((await ColumnAsync("customers", "nickname")).IsNullable);
        Assert.False((await ColumnAsync("customers", "id")).IsNullable);
    }

    [Fact]
    public async Task Relations_link_the_referenced_column_to_the_referencing_column()
    {
        await SyncAsync(_env.CreateSampleDb());
        var customerId = await ColumnAsync("customers", "id");
        var orderCustomer = await ColumnAsync("orders", "customer_id");

        var relation = (await _env.Repository.GetAllAsync<RelationInfo>())
            .Single(r => r.FirstId == customerId.Id);

        Assert.Equal(orderCustomer.Id, relation.SecondId);
        Assert.Equal("One-to-Many", relation.RelationType);
        Assert.Equal(RelationInfo.AutoMarker, relation.MetaInfo);
    }

    [Fact]
    public async Task Second_sync_without_changes_changes_nothing()
    {
        var path = _env.CreateSampleDb();
        await SyncAsync(path);
        var idsBefore = (await _env.Repository.GetAllAsync<ColumnInfo>()).Select(c => c.Id).OrderBy(x => x).ToList();

        var result = await SyncAsync(path);

        Assert.Equal(0, result.TablesAdded + result.TablesRemoved);
        Assert.Equal(0, result.ColumnsAdded + result.ColumnsUpdated + result.ColumnsRemoved);
        var idsAfter = (await _env.Repository.GetAllAsync<ColumnInfo>()).Select(c => c.Id).OrderBy(x => x).ToList();
        Assert.Equal(idsBefore, idsAfter);
        Assert.Equal(2, (await _env.Repository.GetAllAsync<RelationInfo>()).Count); // recréées, pas dupliquées
    }

    [Fact]
    public async Task Descriptions_survive_a_resync()
    {
        var path = _env.CreateSampleDb();
        await SyncAsync(path);
        var table = await TableAsync("customers");
        table.Description = "Clients de la boutique";
        await _env.Repository.UpdateAsync(table.Id, table);
        var col = await ColumnAsync("customers", "email");
        col.Description = "Adresse de connexion";
        await _env.Repository.UpdateAsync(col.Id, col);

        TestCatalog.Execute(path, "ALTER TABLE customers ADD COLUMN phone TEXT;");
        await SyncAsync(path);

        Assert.Equal("Clients de la boutique", (await TableAsync("customers")).Description);
        Assert.Equal("Adresse de connexion", (await ColumnAsync("customers", "email")).Description);
    }

    [Fact]
    public async Task Added_column_and_added_table_are_picked_up()
    {
        var path = _env.CreateSampleDb();
        await SyncAsync(path);

        TestCatalog.Execute(path, "ALTER TABLE tags ADD COLUMN color TEXT; CREATE TABLE audit (id INTEGER PRIMARY KEY);");
        var result = await SyncAsync(path);

        Assert.Equal(1, result.TablesAdded);
        Assert.Equal(1 + 1, result.ColumnsAdded);
        Assert.NotNull(await ColumnAsync("tags", "color"));
    }

    [Fact]
    public async Task Removed_column_and_table_disappear_with_their_relations()
    {
        var path = _env.CreateSampleDb();
        await SyncAsync(path);

        TestCatalog.Execute(path, "DROP TABLE order_lines; DROP TABLE tags;");
        var result = await SyncAsync(path);

        Assert.Equal(2, result.TablesRemoved);
        var tables = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "db1")).Select(t => t.Name).ToList();
        Assert.DoesNotContain("order_lines", tables);
        Assert.DoesNotContain("tags", tables);
        // La relation order_lines -> orders est partie, celle orders -> customers reste.
        Assert.Single(await _env.Repository.GetAllAsync<RelationInfo>());
        Assert.DoesNotContain(await _env.Repository.GetAllAsync<ColumnInfo>(), c => c.Name is "sku" or "label");
    }

    [Fact]
    public async Task Changed_column_type_is_updated_in_place()
    {
        var path = _env.NewFile("a.db");
        TestCatalog.Execute(path, "CREATE TABLE t (v INTEGER);");
        await SyncAsync(path);
        var before = await ColumnAsync("t", "v");

        File.Delete(path);
        TestCatalog.Execute(path, "CREATE TABLE t (v TEXT NOT NULL);");
        var result = await SyncAsync(path);

        Assert.Equal(1, result.ColumnsUpdated);
        var after = await ColumnAsync("t", "v");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("TEXT", after.DataType);
        Assert.False(after.IsNullable);
    }

    [Fact]
    public async Task Same_column_name_in_two_tables_creates_two_distinct_columns()
    {
        await SyncAsync(_env.CreateSampleDb());

        var ids = (await _env.Repository.GetAllAsync<ColumnInfo>()).Where(c => c.Name == "id").ToList();

        Assert.Equal(4, ids.Count); // customers, orders, tags, customer_emails
        Assert.Equal(4, ids.Select(c => c.TableId).Distinct().Count());
    }

    [Fact]
    public async Task Manual_relations_are_kept_and_automatic_ones_are_not_duplicated()
    {
        var path = _env.CreateSampleDb();
        await SyncAsync(path);
        var tagId = await ColumnAsync("tags", "id");
        var noteCol = await ColumnAsync("orders", "note");
        await _env.Repository.CreateAsync(new RelationInfo
            { FirstId = tagId.Id, SecondId = noteCol.Id, RelationType = "Many-to-Many", MetaInfo = "manuel" });

        await SyncAsync(path);

        var relations = await _env.Repository.GetAllAsync<RelationInfo>();
        Assert.Equal(3, relations.Count);
        Assert.Single(relations, r => r.MetaInfo == "manuel");
    }

    [Fact]
    public async Task Two_databases_stay_isolated()
    {
        await SyncAsync(_env.CreateSampleDb("one.db"), "one");
        await SyncAsync(_env.CreateSampleDb("two.db"), "two");

        TestCatalog.Execute(_env.NewFile("two.db"), "DROP TABLE tags;");
        await SyncAsync(_env.NewFile("two.db"), "two");

        Assert.Equal(5, (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "one")).Count);
        Assert.Equal(4, (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "two")).Count);
        Assert.Equal(4, (await _env.Repository.GetAllAsync<RelationInfo>()).Count);
    }
}
