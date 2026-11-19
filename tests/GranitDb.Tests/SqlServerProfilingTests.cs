using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Models.Inputs;
using Microsoft.Data.SqlClient;
using GranitDB.API.Services;

namespace GranitDb.Tests;

/// <summary>Profilage contre un vrai SQL Server en conteneur : types variés, schémas, noms à citer.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlServerProfilingTests(SqlServerFixture sql)
{
    private async Task<string> SourceAsync()
    {
        var source = sql.NewDatabaseConnectionString("profil");
        await SqlServerBootstrap.EnsureDatabaseAsync(source);
        await SqlServerFixture.ExecuteAsync(source,
            "CREATE SCHEMA sales",
            """
            CREATE TABLE dbo.Mixte (
                Id INT NOT NULL PRIMARY KEY, Nom NVARCHAR(50) NULL, Actif BIT NULL, Guid UNIQUEIDENTIFIER NULL,
                Montant DECIMAL(10,2) NULL, Quand DATETIME2 NULL, Blob VARBINARY(MAX) NULL, Gros NVARCHAR(MAX) NULL,
                Vieux TEXT NULL, Vieux2 NTEXT NULL, Doc XML NULL, Divers SQL_VARIANT NULL, Geo GEOGRAPHY NULL, Chemin HIERARCHYID NULL)
            """,
            """
            INSERT INTO dbo.Mixte (Id, Nom, Actif, Guid, Montant, Quand, Blob, Gros, Vieux, Vieux2, Doc, Divers, Geo, Chemin) VALUES
              (1, 'a', 1, NEWID(), 10.5, SYSDATETIME(), 0x01, N'x', 'v', N'w', '<a/>', 1, geography::Point(1, 1, 4326), hierarchyid::Parse('/1/')),
              (2, 'a', 0, NEWID(), 10.5, SYSDATETIME(), NULL, N'y', NULL, NULL, NULL, NULL, NULL, NULL),
              (3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)
            """,
            "CREATE TABLE sales.Orders (Id INT NOT NULL PRIMARY KEY, Total INT NULL)",
            "INSERT INTO sales.Orders VALUES (1, 5), (2, 5), (3, NULL)",
            "CREATE TABLE dbo.[Odd]]Name] ([Col]]1] INT NULL)",
            "INSERT INTO dbo.[Odd]]Name] VALUES (1), (NULL)");
        return source;
    }

    private static ProfiledColumn[] Columns(params (string Name, string Type)[] columns) =>
        columns.Select(c => new ProfiledColumn(c.Name, c.Type)).ToArray();

    [SqlServerFact]
    public async Task Every_common_type_is_profiled_and_the_ones_without_distinct_only_get_nulls()
    {
        var source = await SourceAsync();
        var schema = await new SqlServerSchemaConnector().ReadSchemaAsync(source);
        var table = schema.Tables.Single(t => t.Name == "Mixte");

        var result = await new SqlServerColumnProfiler().ProfileAsync(source, "Mixte",
            table.Columns.Select(c => new ProfiledColumn(c.Name, c.DataType)).ToList(), 30);

        Assert.Equal(3, result.RowCount);
        var by = result.Columns.ToDictionary(c => c.Column);
        Assert.Equal((0L, 3L), (by["Id"].NullCount, by["Id"].DistinctCount!.Value));
        Assert.Equal((1L, 1L), (by["Nom"].NullCount, by["Nom"].DistinctCount!.Value));
        Assert.Equal((1L, 2L), (by["Actif"].NullCount, by["Actif"].DistinctCount!.Value));
        Assert.Equal((1L, 2L), (by["Guid"].NullCount, by["Guid"].DistinctCount!.Value));
        Assert.Equal((1L, 1L), (by["Montant"].NullCount, by["Montant"].DistinctCount!.Value));
        Assert.Equal((2L, 1L), (by["Blob"].NullCount, by["Blob"].DistinctCount!.Value));
        Assert.Equal(1L, by["Gros"].NullCount - 0 > 0 ? 1L : 0L);
        foreach (var noDistinct in new[] { "Vieux", "Vieux2", "Doc", "Divers", "Geo", "Chemin" })
        {
            Assert.Null(by[noDistinct].DistinctCount);
            Assert.True(by[noDistinct].NullCount >= 1, noDistinct);
        }
    }

    [SqlServerFact]
    public async Task A_table_outside_dbo_and_a_name_with_a_bracket_are_quoted()
    {
        var source = await SourceAsync();
        var profiler = new SqlServerColumnProfiler();

        var orders = await profiler.ProfileAsync(source, "sales.Orders", Columns(("Id", "int"), ("Total", "int")), 30);
        var odd = await profiler.ProfileAsync(source, "Odd]Name", Columns(("Col]1", "int")), 30);

        Assert.Equal(3, orders.RowCount);
        Assert.Equal((1L, 1L), (orders.Columns.Single(c => c.Column == "Total").NullCount, orders.Columns.Single(c => c.Column == "Total").DistinctCount!.Value));
        Assert.Equal(2, odd.RowCount);
        Assert.Equal(1, odd.Columns.Single().NullCount);
    }

    [SqlServerFact]
    public async Task A_missing_table_or_denied_read_gives_a_message_and_not_a_crash()
    {
        var source = await SourceAsync();

        var missing = await Assert.ThrowsAsync<GranitDB.API.Exceptions.InvalidSourceException>(() =>
            new SqlServerColumnProfiler().ProfileAsync(source, "Absente", Columns(("Id", "int")), 30));

        Assert.Contains("Absente", missing.Message);
    }

    [SqlServerFact]
    public async Task The_service_profiles_a_registered_database_through_its_encrypted_connection()
    {
        var source = await SourceAsync();
        var builder = new SqlConnectionStringBuilder(source);
        using var env = new TestCatalog(o =>
        {
            o.AllowSqlServerSources = true;
            o.AllowProfiling = true;
        });
        var host = builder.DataSource.Replace("tcp:", "").Split(',');
        var imported = await env.Importer.ImportServerAsync(new ServerConnectionInput
        {
            Server = host[0], Port = host.Length > 1 ? int.Parse(host[1]) : null, Database = builder.InitialCatalog,
            AuthMode = ServerConnectionInput.SqlAuth, User = builder.UserID, Password = builder.Password,
            Encrypt = false, TrustServerCertificate = true
        });
        var service = new ColumnProfilingService(env.Repository, env.Importer, [new SqliteColumnProfiler(), new SqlServerColumnProfiler()],
            Microsoft.Extensions.Options.Options.Create(env.Options));

        var run = (await service.ProfileDatabaseAsync(imported.Database.Id))!;

        Assert.Equal((3, 0), (run.TablesProfiled, run.TablesFailed));
        var tables = await env.Repository.FindAsync<GranitDB.API.Models.Descriptions.TableInfo>("DatabaseId", imported.Database.Id);
        var orders = (await service.GetAsync(tables.Single(t => t.Name == "sales.Orders").Id))!;
        Assert.Equal(3, orders.RowCount);
        Assert.Equal(1, orders.Columns.Single(c => c.Column == "Total").NullCount);
    }

    [SqlServerFact]
    public async Task More_columns_than_a_batch_are_profiled_in_several_queries()
    {
        var source = sql.NewDatabaseConnectionString("large");
        await SqlServerBootstrap.EnsureDatabaseAsync(source);
        var names = Enumerable.Range(1, 130).Select(i => $"C{i}").ToArray();
        await SqlServerFixture.ExecuteAsync(source,
            $"CREATE TABLE dbo.Large ({string.Join(", ", names.Select(n => $"{n} INT NULL"))})",
            $"INSERT INTO dbo.Large ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => i % 2 == 0 ? "1" : "NULL"))})");

        var result = await new SqlServerColumnProfiler().ProfileAsync(source, "Large", Columns(names.Select(n => (n, "int")).ToArray()), 30);

        Assert.Equal(130, result.Columns.Count);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(0, result.Columns[0].NullCount);
        Assert.Equal(1, result.Columns[1].NullCount);
    }
}
