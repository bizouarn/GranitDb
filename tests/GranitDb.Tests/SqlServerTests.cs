using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.Data.SqlClient;

namespace GranitDb.Tests;

/// <summary>Tests contre un vrai SQL Server en conteneur (Testcontainers). Ignorés si Docker est absent.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlServerTests(SqlServerFixture sql)
{
    private const string LegacyCatalogSql = """
        CREATE TABLE Databases (Id VARCHAR(36) PRIMARY KEY, Name VARCHAR(60), Type VARCHAR(50), IsPublic bit,
            Description VARCHAR(MAX), ProjectUrl VARCHAR(MAX), DatabaseUrl VARCHAR(MAX), MetaInfo VARCHAR(MAX));
        CREATE TABLE Columns (Id VARCHAR(36) PRIMARY KEY, DatabaseId VARCHAR(36), TableId VARCHAR(36),
            Name VARCHAR(60), DataType VARCHAR(50), IsNullable Bit, Description VARCHAR(MAX), MetaInfo VARCHAR(MAX));
        CREATE TABLE Relations (Id VARCHAR(36) PRIMARY KEY, FirstId VARCHAR(36), SecondId VARCHAR(36),
            RelationType VARCHAR(50), Description VARCHAR(MAX), MetaInfo VARCHAR(MAX));
        CREATE TABLE Tables (Id VARCHAR(36) PRIMARY KEY, Name VARCHAR(255) NOT NULL, DatabaseId VARCHAR(36) NOT NULL,
            Description VARCHAR(MAX), MetaInfo VARCHAR(MAX));
        """;

    private async Task<(CatalogRepository Repository, string ConnectionString)> NewCatalogAsync()
    {
        var options = sql.NewCatalogOptions();
        var repository = SqlServerFixture.CreateRepository(options);
        await repository.InitializeAsync();
        return (repository, options.ConnectionString!);
    }

    [SqlServerFact]
    public async Task Missing_database_is_created_on_initialization()
    {
        var options = sql.NewCatalogOptions();
        var name = new SqlConnectionStringBuilder(options.ConnectionString).InitialCatalog;

        await SqlServerFixture.CreateRepository(options).InitializeAsync();

        var master = new SqlConnectionStringBuilder(options.ConnectionString) { InitialCatalog = "master" };
        Assert.Equal(1, await SqlServerFixture.ScalarAsync<int>(master.ConnectionString,
            $"SELECT COUNT(*) FROM sys.databases WHERE name = '{name}'"));
    }

    [SqlServerFact]
    public async Task Fresh_catalog_reaches_the_latest_version_with_foreign_keys_and_unicode_columns()
    {
        var (_, cs) = await NewCatalogAsync();

        Assert.Equal(CatalogMigrations.All.Count,
            await SqlServerFixture.ScalarAsync<int>(cs, "SELECT MAX(Version) FROM dbo.SchemaVersion"));
        Assert.Equal(12, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.foreign_keys"));
        Assert.Equal("nvarchar", await SqlServerFixture.ScalarAsync<string>(cs,
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'Databases' AND COLUMN_NAME = 'Name'"));
        Assert.Equal(1, await SqlServerFixture.ScalarAsync<int>(cs,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_Columns_TableId'"));
    }

    [SqlServerFact]
    public async Task Initializing_twice_is_harmless()
    {
        var options = sql.NewCatalogOptions();
        var repository = SqlServerFixture.CreateRepository(options);

        await repository.InitializeAsync();
        await repository.InitializeAsync();

        Assert.Equal(CatalogMigrations.All.Count, await SqlServerFixture.ScalarAsync<int>(
            options.ConnectionString!, "SELECT COUNT(*) FROM dbo.SchemaVersion"));
    }

    [SqlServerFact]
    public async Task Catalog_created_with_the_legacy_script_is_upgraded_and_orphans_removed()
    {
        var options = sql.NewCatalogOptions();
        await SqlServerBootstrap.EnsureDatabaseAsync(options.ConnectionString!);
        await SqlServerFixture.ExecuteAsync(options.ConnectionString!,
            LegacyCatalogSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(
                [
                    "INSERT INTO Databases (Id, Name, Type, IsPublic, Description) VALUES ('d1', 'Ancienne', 'SqlServer', NULL, 'gardée')",
                    "INSERT INTO Tables (Id, Name, DatabaseId) VALUES ('t1', 'clients', 'd1')",
                    "INSERT INTO Tables (Id, Name, DatabaseId) VALUES ('t-orphan', 'fantôme', 'ghost')",
                    "INSERT INTO Columns (Id, DatabaseId, TableId, Name, DataType) VALUES ('c1', 'd1', 't1', 'id', 'int')",
                    "INSERT INTO Columns (Id, DatabaseId, TableId, Name, DataType) VALUES ('c-orphan', 'ghost', 't-orphan', 'x', 'int')",
                    "INSERT INTO Relations (Id, FirstId, SecondId) VALUES ('r1', 'c1', 'c-orphan')"
                ]).ToArray());

        var repository = SqlServerFixture.CreateRepository(options);
        await repository.InitializeAsync();

        var database = await repository.GetAsync<DatabaseInfo>("d1");
        Assert.NotNull(database);
        Assert.Equal("gardée", database.Description);
        Assert.False(database.IsPublic);
        Assert.Single(await repository.GetAllAsync<TableInfo>());
        var column = Assert.Single(await repository.GetAllAsync<ColumnInfo>());
        Assert.True(column.IsNullable);
        Assert.False(column.IsPrimaryKey);
        Assert.Empty(await repository.GetAllAsync<RelationInfo>());
        Assert.Equal(12, await SqlServerFixture.ScalarAsync<int>(options.ConnectionString!,
            "SELECT COUNT(*) FROM sys.foreign_keys"));
    }

    [SqlServerFact]
    public async Task Repository_roundtrips_entities_including_unicode_and_booleans()
    {
        var (repository, _) = await NewCatalogAsync();
        var text = "Clés étrangères — 日本語 ✓";
        var db = await repository.CreateAsync(new DatabaseInfo
            { Name = "Boutique", Type = "SqlServer", IsPublic = true, Description = text });
        var table = await repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });
        var column = await repository.CreateAsync(new ColumnInfo
        {
            Name = new string('n', 200), DataType = "int", IsNullable = false, IsPrimaryKey = true,
            DefaultValue = "((0))", TableId = table.Id, DatabaseId = db.Id
        });

        var readDb = await repository.GetAsync<DatabaseInfo>(db.Id);
        Assert.True(readDb!.IsPublic);
        Assert.Equal(text, readDb.Description);
        var readColumn = await repository.GetAsync<ColumnInfo>(column.Id);
        Assert.False(readColumn!.IsNullable);
        Assert.True(readColumn.IsPrimaryKey);
        Assert.Equal(200, readColumn.Name.Length); // l'ancienne largeur de 60 caractères n'existe plus
        Assert.Single(await repository.FindAsync<TableInfo>("DatabaseId", db.Id));

        readColumn.Description = "mise à jour";
        Assert.True(await repository.UpdateAsync(column.Id, readColumn));
        Assert.Equal("mise à jour", (await repository.GetAsync<ColumnInfo>(column.Id))!.Description);
    }

    [SqlServerFact]
    public async Task Foreign_keys_are_enforced()
    {
        var (repository, _) = await NewCatalogAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            repository.CreateAsync(new TableInfo { Name = "orphan", DatabaseId = "no-such-db" }));

        Assert.Equal(547, ex.Number);
    }

    [SqlServerFact]
    public async Task Deleting_a_database_cascades_in_foreign_key_order()
    {
        var (repository, _) = await NewCatalogAsync();
        var db = await repository.CreateAsync(new DatabaseInfo { Name = "d", Type = "SqlServer" });
        var table = await repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });
        var a = await repository.CreateAsync(new ColumnInfo { Name = "a", TableId = table.Id, DatabaseId = db.Id });
        var b = await repository.CreateAsync(new ColumnInfo { Name = "b", TableId = table.Id, DatabaseId = db.Id });
        await repository.CreateAsync(new RelationInfo { FirstId = a.Id, SecondId = b.Id });

        Assert.True(await repository.DeleteAsync<DatabaseInfo>(db.Id));

        Assert.Empty(await repository.GetAllAsync<TableInfo>());
        Assert.Empty(await repository.GetAllAsync<ColumnInfo>());
        Assert.Empty(await repository.GetAllAsync<RelationInfo>());
    }

    [SqlServerFact]
    public async Task Sqlite_file_can_be_documented_into_a_sql_server_catalog()
    {
        var (repository, _) = await NewCatalogAsync();
        var synchronizer = new SchemaSynchronizer(repository);
        using var env = new TestCatalog();
        var path = env.CreateSampleDb();
        var schema = await new SqliteSchemaConnector().ReadSchemaAsync(path);

        var first = await synchronizer.SyncAsync("db1", schema, "Boutique");
        Assert.Equal(5, first.TablesAdded);
        Assert.Equal(2, first.RelationsCreated);

        var second = await synchronizer.SyncAsync("db1", schema);
        Assert.Equal(0, second.TablesAdded + second.ColumnsAdded + second.ColumnsRemoved);
        Assert.Equal(2, (await repository.GetAllAsync<RelationInfo>()).Count);

        TestCatalog.Execute(path, "DROP TABLE order_lines; DROP TABLE tags;");
        var third = await synchronizer.SyncAsync("db1", await new SqliteSchemaConnector().ReadSchemaAsync(path));
        Assert.Equal(2, third.TablesRemoved);
        Assert.Equal(3, (await repository.FindAsync<TableInfo>("DatabaseId", "db1")).Count);
        Assert.Single(await repository.GetAllAsync<RelationInfo>());
    }

    [SqlServerFact]
    public async Task Tables_with_the_same_name_in_different_schemas_stay_distinct()
    {
        var source = sql.NewDatabaseConnectionString("schemas");
        await SqlServerBootstrap.EnsureDatabaseAsync(source);
        await SqlServerFixture.ExecuteAsync(source,
            "CREATE SCHEMA sales",
            "CREATE SCHEMA hr",
            "CREATE TABLE sales.Orders (Id INT NOT NULL PRIMARY KEY, Total INT)",
            "CREATE TABLE hr.Orders (Id INT NOT NULL PRIMARY KEY, Label NVARCHAR(20))",
            "CREATE TABLE dbo.Lines (Id INT NOT NULL PRIMARY KEY, SalesOrderId INT NOT NULL REFERENCES sales.Orders(Id))");

        var schema = await new SqlServerSchemaConnector().ReadSchemaAsync(source);

        Assert.Equal(new[] { "hr.Orders", "Lines", "sales.Orders" }, schema.Tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal("Label", Assert.Single(schema.Tables.Single(t => t.Name == "hr.Orders").Columns, c => c.Name != "Id").Name);
        Assert.Equal("Total", Assert.Single(schema.Tables.Single(t => t.Name == "sales.Orders").Columns, c => c.Name != "Id").Name);
        Assert.Contains(new SourceForeignKey("Lines", "SalesOrderId", "sales.Orders", "Id"), schema.ForeignKeys);

        // Et la synchronisation les distingue aussi dans le catalogue.
        var (repository, _) = await NewCatalogAsync();
        await new SchemaSynchronizer(repository).SyncAsync("src", schema);
        Assert.Equal(3, (await repository.FindAsync<TableInfo>("DatabaseId", "src")).Count);
        Assert.Single(await repository.GetRelationsOfDatabaseAsync("src"));
    }

    [SqlServerFact]
    public async Task Sql_server_connector_reads_indexes_and_zero_based_column_positions()
    {
        var source = sql.NewDatabaseConnectionString("indexes");
        await SqlServerBootstrap.EnsureDatabaseAsync(source);
        await SqlServerFixture.ExecuteAsync(source,
            "CREATE TABLE dbo.Accounts (Id INT NOT NULL PRIMARY KEY, Email NVARCHAR(100) NOT NULL, Country CHAR(2), City NVARCHAR(50), Note NVARCHAR(MAX))",
            "CREATE UNIQUE INDEX UX_Accounts_Email ON dbo.Accounts(Email)",
            "CREATE INDEX IX_Accounts_Country_City ON dbo.Accounts(Country, City) INCLUDE (Note)");

        var schema = await new SqlServerSchemaConnector().ReadSchemaAsync(source);

        var unique = schema.Indexes.Single(i => i.Name == "UX_Accounts_Email");
        Assert.True(unique.IsUnique);
        Assert.Equal(new[] { "Email" }, unique.Columns);
        var composite = schema.Indexes.Single(i => i.Name == "IX_Accounts_Country_City");
        Assert.False(composite.IsUnique);
        Assert.Equal(new[] { "Country", "City" }, composite.Columns); // la colonne INCLUDE n'en fait pas partie
        Assert.DoesNotContain(schema.Indexes, i => i.Name.StartsWith("PK_")); // clé primaire exclue
        var columns = schema.Tables.Single(t => t.Name == "Accounts").Columns;
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, columns.Select(c => c.Ordinal).ToArray());

        var (repository, _) = await NewCatalogAsync();
        var result = await new SchemaSynchronizer(repository).SyncAsync("src", schema);
        Assert.Equal(2, result.IndexesAdded);
        Assert.Equal(2, (await repository.GetAllAsync<IndexInfo>()).Count);
    }

    [SqlServerFact]
    public async Task Global_search_works_on_sql_server_with_unicode_escaping_and_limits()
    {
        var (repository, _) = await NewCatalogAsync();
        var db = await repository.CreateAsync(new DatabaseInfo { Name = "Écoles", Type = "SqlServer", Description = "Base des étudiants" });
        var table = await repository.CreateAsync(new TableInfo { Name = "étudiants", DatabaseId = db.Id });
        foreach (var i in Enumerable.Range(0, 5))
            await repository.CreateAsync(new ColumnInfo { Name = $"note_{i}", TableId = table.Id, DatabaseId = db.Id, Description = "100% sûr" });
        await repository.CreateAsync(new IndexInfo { DatabaseId = db.Id, TableId = table.Id, Name = "ix_note", ColumnNames = "note_0,note_1" });

        var byName = await repository.SearchAsync("ÉTUDIANT", 3);
        Assert.Equal("étudiants", Assert.Single(byName, h => h.Kind == "table").Name);
        Assert.Equal("Écoles", byName.Single(h => h.Kind == "table").DatabaseName);

        Assert.Equal(3, (await repository.SearchAsync("note", 3)).Count(h => h.Kind == "column")); // limite par type
        Assert.Equal(5, (await repository.SearchAsync("100%", 25)).Count(h => h.Kind == "column")); // % littéral, via la description
        Assert.Empty(await repository.SearchAsync("n_te", 25));                                      // _ littéral
        Assert.Contains(await repository.SearchAsync("note_1", 25), h => h.Kind == "index");
        Assert.Equal("Écoles", Assert.Single(await repository.SearchAsync("étudiants", 25), h => h.Kind == "database").Name);
    }

    [SqlServerFact]
    public async Task Pages_are_sorted_and_counted_on_sql_server()
    {
        var (repository, _) = await NewCatalogAsync();
        foreach (var i in Enumerable.Range(0, 12).Reverse())
            await repository.CreateAsync(new DatabaseInfo { Name = $"base_{i:00}", Type = "SqlServer" });

        var page = await repository.GetPageAsync<DatabaseInfo>(5, 4);

        Assert.Equal(new[] { "base_05", "base_06", "base_07", "base_08" }, page.Select(d => d.Name).ToArray());
        Assert.Equal(12, await repository.CountAsync<DatabaseInfo>());
    }

    [SqlServerFact]
    public async Task Filters_and_filtered_pages_work_on_sql_server()
    {
        var (repository, _) = await NewCatalogAsync();
        var database = await repository.CreateAsync(new DatabaseInfo { Name = "Écoles", Type = "SqlServer" });
        foreach (var name in new[] { "élèves", "Élèves_archive", "profs", "100%_notes" })
            await repository.CreateAsync(new TableInfo { Name = name, DatabaseId = database.Id });
        await repository.CreateAsync(new ColumnInfo
            { DatabaseId = database.Id, TableId = (await repository.FindAsync<TableInfo>("Name", "profs"))[0].Id, Name = "id", DataType = "int", IsPrimaryKey = true });

        var contains = await repository.GetPageAsync<TableInfo>(0, 10, [new CatalogFilter("Name", "élèves")]);
        var literal = await repository.GetAllAsync<TableInfo>([new CatalogFilter("Name", "100%", FilterMode.Contains)]);
        var page = await repository.GetPageAsync<TableInfo>(1, 2, [new CatalogFilter("DatabaseId", database.Id)]);
        var keys = await repository.GetAllAsync<ColumnInfo>([new CatalogFilter("IsPrimaryKey", "true", FilterMode.Bool)]);

        Assert.Equal("élèves", Assert.Single(contains).Name); // égalité (« Élèves_archive » exclue)
        Assert.Equal("100%_notes", Assert.Single(literal).Name);
        // Tri de la collation SQL Server (sans casse ni accents) : 100%_notes, élèves, Élèves_archive, profs.
        Assert.Equal(["élèves", "Élèves_archive"], page.Select(t => t.Name).ToArray());
        Assert.Equal(4, await repository.CountAsync<TableInfo>([new CatalogFilter("DatabaseId", database.Id)]));
        Assert.Equal("id", Assert.Single(keys).Name);
    }

    [SqlServerFact]
    public async Task Description_history_is_recorded_atomically_on_sql_server()
    {
        var (repository, _) = await NewCatalogAsync();
        var database = await repository.CreateAsync(new DatabaseInfo { Name = "Écoles", Type = "SqlServer" });

        await repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "Premiers élèves « ✓ »");
        await repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "Deuxième");

        var history = await repository.GetHistoryAsync(database.Id, 0, 10);
        Assert.Equal(2, await repository.CountHistoryAsync(database.Id));
        Assert.Equal(["Deuxième", "Premiers élèves « ✓ »"], history.Select(h => h.NewValue).ToArray());
        Assert.Equal("Premiers élèves « ✓ »", history[0].OldValue);
        Assert.Null(history[1].OldValue);
        Assert.All(history, h => Assert.Equal("database", h.EntityType));
        Assert.True(history[0].ChangedAt >= history[1].ChangedAt);
        Assert.True((DateTime.UtcNow - history[0].ChangedAt).Duration() < TimeSpan.FromMinutes(5));
    }

    [SqlServerFact]
    public async Task Governance_tables_domains_tags_and_sensitive_columns_work_on_sql_server()
    {
        var (repository, cs) = await NewCatalogAsync();
        var database = await repository.CreateAsync(new DatabaseInfo { Name = "Écoles", Type = "SqlServer" });
        var table = await repository.CreateAsync(new TableInfo { Name = "élèves", DatabaseId = database.Id });
        var other = await repository.CreateAsync(new TableInfo { Name = "profs", DatabaseId = database.Id });
        var column = await repository.CreateAsync(new ColumnInfo
            { Name = "nom", DataType = "nvarchar", TableId = table.Id, DatabaseId = database.Id });
        var domain = await repository.CreateAsync(new DomainInfo { DatabaseId = database.Id, Name = "Scolarité", Color = "#112233" });
        await repository.CreateAsync(new DomainTableInfo { DomainId = domain.Id, TableId = table.Id, X = 12.5, Y = -3 });
        var tag = await repository.CreateAsync(new TagInfo { Name = "RGPD" });
        await repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = "column", EntityId = column.Id });
        await repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = "table", EntityId = other.Id });

        // Unicité : même nom de domaine (sans tenir compte de la casse), même table dans un domaine, même étiquette.
        await Assert.ThrowsAsync<SqlException>(() => repository.CreateAsync(new DomainInfo { DatabaseId = database.Id, Name = "SCOLARITÉ" }));
        await Assert.ThrowsAsync<SqlException>(() => repository.CreateAsync(new DomainTableInfo { DomainId = domain.Id, TableId = table.Id }));
        await Assert.ThrowsAsync<SqlException>(() => repository.CreateAsync(new TagInfo { Name = "rgpd" }));
        await Assert.ThrowsAsync<SqlException>(() => repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = "column", EntityId = column.Id }));

        var link = (await repository.FindAsync<DomainTableInfo>("DomainId", domain.Id)).Single();
        Assert.Equal((12.5, -3.0), (link.X, link.Y));
        var modified = await repository.ModifyAsync<ColumnInfo>(column.Id, c => c.IsSensitive = true);
        Assert.True(modified!.IsSensitive);
        var change = Assert.Single(await repository.GetHistoryAsync(column.Id, 0, 10));
        Assert.Equal(("IsSensitive", "false", "true"), (change.Field, change.OldValue, change.NewValue));
        Assert.Equal(2, (await repository.GetAllAsync<TagLinkInfo>([CatalogFilter.In("EntityId", [column.Id, other.Id])])).Count);
        Assert.Empty(await repository.GetAllAsync<TagLinkInfo>([CatalogFilter.In("EntityId", [])]));

        // Cascades dans l'ordre des clés étrangères.
        Assert.True(await repository.DeleteAsync<TableInfo>(table.Id));
        Assert.Equal(0, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.DomainTables"));
        Assert.Equal(1, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.TagLinks"));
        Assert.True(await repository.DeleteAsync<DatabaseInfo>(database.Id));
        Assert.Equal(0, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.TagLinks"));
        Assert.Equal(0, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Domains"));
        Assert.Equal(1, await SqlServerFixture.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Tags"));
        Assert.True(await repository.DeleteAsync<TagInfo>(tag.Id));
    }

    [SqlServerFact]
    public async Task Deleting_a_table_removes_its_indexes_before_the_table_on_sql_server()
    {
        var (repository, _) = await NewCatalogAsync();
        var db = await repository.CreateAsync(new DatabaseInfo { Name = "d", Type = "SqlServer" });
        var table = await repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });
        await repository.CreateAsync(new IndexInfo
            { DatabaseId = db.Id, TableId = table.Id, Name = "ix", ColumnNames = "a,b" });

        Assert.True(await repository.DeleteAsync<TableInfo>(table.Id));

        Assert.Empty(await repository.GetAllAsync<IndexInfo>());
    }

    [SqlServerFact]
    public async Task Sql_server_connector_reads_tables_keys_nullability_defaults_and_foreign_keys()
    {
        var source = sql.NewDatabaseConnectionString("source");
        await SqlServerBootstrap.EnsureDatabaseAsync(source);
        await SqlServerFixture.ExecuteAsync(source,
            """
            CREATE TABLE dbo.Customers (
                Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Email NVARCHAR(100) NOT NULL,
                Nickname NVARCHAR(50) NULL,
                Created DATETIME2 NOT NULL DEFAULT (SYSUTCDATETIME()))
            """,
            """
            CREATE TABLE dbo.Orders (
                Id INT NOT NULL PRIMARY KEY,
                CustomerId INT NOT NULL CONSTRAINT FK_Orders_Customers FOREIGN KEY REFERENCES dbo.Customers(Id))
            """,
            "CREATE VIEW dbo.CustomerEmails AS SELECT Id, Email FROM dbo.Customers");

        var schema = await new SqlServerSchemaConnector().ReadSchemaAsync(source);

        Assert.Equal("SqlServer", schema.Type);
        Assert.Equal(new[] { "CustomerEmails", "Customers", "Orders" }, schema.Tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal("View", schema.Tables.Single(t => t.Name == "CustomerEmails").TableType);
        var customers = schema.Tables.Single(t => t.Name == "Customers").Columns;
        Assert.True(customers.Single(c => c.Name == "Id").IsPrimaryKey);
        Assert.False(customers.Single(c => c.Name == "Email").IsNullable);
        Assert.True(customers.Single(c => c.Name == "Nickname").IsNullable);
        Assert.NotNull(customers.Single(c => c.Name == "Created").DefaultValue);
        Assert.Contains(new SourceForeignKey("Orders", "CustomerId", "Customers", "Id"), schema.ForeignKeys);
    }
}

/// <summary>Ces tests n'ont pas besoin de Docker : ils vérifient les erreurs de connexion.</summary>
public class SqlServerConnectorErrorTests
{
    [Fact]
    public async Task Empty_connection_string_is_rejected()
    {
        await Assert.ThrowsAsync<InvalidSourceException>(() => new SqlServerSchemaConnector().ReadSchemaAsync(""));
    }

    [Fact]
    public async Task Unreachable_server_is_reported_as_an_invalid_source()
    {
        await Assert.ThrowsAsync<InvalidSourceException>(() => new SqlServerSchemaConnector()
            .ReadSchemaAsync("Server=127.0.0.1,1;Database=x;User Id=sa;Password=x;Connect Timeout=2;Encrypt=False"));
    }
}
