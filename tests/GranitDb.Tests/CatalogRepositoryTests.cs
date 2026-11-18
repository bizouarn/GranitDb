using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

public class CatalogRepositoryTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task<(DatabaseInfo Db, TableInfo Table, ColumnInfo Col)> SeedAsync()
    {
        var db = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "d", Type = "Sqlite" });
        var table = await _env.Repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });
        var col = await _env.Repository.CreateAsync(new ColumnInfo
            { Name = "c", DataType = "INTEGER", TableId = table.Id, DatabaseId = db.Id });
        return (db, table, col);
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_creates_the_sqlite_file()
    {
        await _env.Repository.InitializeAsync();
        await _env.Repository.InitializeAsync();
        Assert.True(File.Exists(Path.Combine(_env.Options.DataDirectory, "granitdb.db")));
    }

    [Fact]
    public async Task Create_generates_a_guid_id_and_roundtrips_all_fields()
    {
        var created = await _env.Repository.CreateAsync(new DatabaseInfo
        {
            Name = "Ventes", Type = "Sqlite", IsPublic = true, Description = "desc", MetaInfo = "meta",
            ProjectUrl = "http://p", DatabaseUrl = "http://d", SourcePath = "/x.db"
        });

        Assert.True(Guid.TryParse(created.Id, out _));
        var read = await _env.Repository.GetAsync<DatabaseInfo>(created.Id);
        Assert.NotNull(read);
        Assert.Equal("Ventes", read.Name);
        Assert.True(read.IsPublic);
        Assert.Equal("desc", read.Description);
        Assert.Equal("meta", read.MetaInfo);
        Assert.Equal("http://p", read.ProjectUrl);
        Assert.Equal("/x.db", read.SourcePath);
    }

    [Fact]
    public async Task Booleans_and_nulls_roundtrip_on_columns()
    {
        var (_, _, col) = await SeedAsync();
        col.IsNullable = true;
        col.IsPrimaryKey = true;
        col.DefaultValue = null;
        Assert.True(await _env.Repository.UpdateAsync(col.Id, col));

        var read = await _env.Repository.GetAsync<ColumnInfo>(col.Id);
        Assert.NotNull(read);
        Assert.True(read.IsNullable);
        Assert.True(read.IsPrimaryKey);
        Assert.Null(read.DefaultValue);
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        Assert.Null(await _env.Repository.GetAsync<DatabaseInfo>("nope"));
    }

    [Fact]
    public async Task GetAll_returns_every_row()
    {
        await _env.Repository.CreateAsync(new DatabaseInfo { Name = "a", Type = "Sqlite" });
        await _env.Repository.CreateAsync(new DatabaseInfo { Name = "b", Type = "Sqlite" });
        Assert.Equal(2, (await _env.Repository.GetAllAsync<DatabaseInfo>()).Count);
    }

    [Fact]
    public async Task Find_filters_in_sql_and_rejects_unknown_properties()
    {
        var (db, _, _) = await SeedAsync();
        var other = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "o", Type = "Sqlite" });
        await _env.Repository.CreateAsync(new TableInfo { Name = "x", DatabaseId = other.Id });

        var found = await _env.Repository.FindAsync<TableInfo>("DatabaseId", db.Id);

        Assert.Equal("t", Assert.Single(found).Name);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _env.Repository.FindAsync<TableInfo>("Name\" OR 1=1 --", "x"));
    }

    [Fact]
    public async Task Update_requires_matching_id_and_reports_missing_rows()
    {
        var (db, _, _) = await SeedAsync();
        await Assert.ThrowsAsync<IdMisMatchException>(() => _env.Repository.UpdateAsync("other", db));

        var ghost = new DatabaseInfo { Id = "ghost", Name = "g", Type = "Sqlite" };
        Assert.False(await _env.Repository.UpdateAsync("ghost", ghost));
    }

    [Fact]
    public async Task Delete_of_unknown_id_returns_false()
    {
        Assert.False(await _env.Repository.DeleteAsync<DatabaseInfo>("nope"));
    }

    [Fact]
    public async Task Deleting_a_database_cascades_to_tables_columns_and_relations()
    {
        var (db, table, col) = await SeedAsync();
        var col2 = await _env.Repository.CreateAsync(new ColumnInfo
            { Name = "c2", DataType = "INTEGER", TableId = table.Id, DatabaseId = db.Id });
        await _env.Repository.CreateAsync(new RelationInfo { FirstId = col.Id, SecondId = col2.Id });

        Assert.True(await _env.Repository.DeleteAsync<DatabaseInfo>(db.Id));

        Assert.Empty(await _env.Repository.GetAllAsync<TableInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<ColumnInfo>());
        Assert.Empty(await _env.Repository.GetAllAsync<RelationInfo>());
    }

    [Fact]
    public async Task Deleting_a_column_removes_its_relations_only()
    {
        var (db, table, col) = await SeedAsync();
        var col2 = await _env.Repository.CreateAsync(new ColumnInfo
            { Name = "c2", DataType = "INTEGER", TableId = table.Id, DatabaseId = db.Id });
        await _env.Repository.CreateAsync(new RelationInfo { FirstId = col.Id, SecondId = col2.Id });

        await _env.Repository.DeleteAsync<ColumnInfo>(col.Id);

        Assert.Empty(await _env.Repository.GetAllAsync<RelationInfo>());
        Assert.Single(await _env.Repository.GetAllAsync<ColumnInfo>());
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_by_the_sqlite_catalog()
    {
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            _env.Repository.CreateAsync(new TableInfo { Name = "orphan", DatabaseId = "no-such-db" }));
        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public async Task Special_characters_in_values_are_stored_verbatim()
    {
        var value = "x'; DROP TABLE \"Databases\"; --";
        var created = await _env.Repository.CreateAsync(new DatabaseInfo { Name = value, Type = "Sqlite" });
        Assert.Equal(value, (await _env.Repository.GetAsync<DatabaseInfo>(created.Id))!.Name);
    }
}
