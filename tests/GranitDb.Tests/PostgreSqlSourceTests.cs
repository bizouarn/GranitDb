using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Npgsql;

namespace GranitDb.Tests;

/// <summary>Documenter une base PostgreSQL : schéma, import, synchronisation, profilage (contre un vrai serveur en conteneur).</summary>
[Collection(PostgreSqlCollection.Name)]
public class PostgreSqlSourceTests(PostgreSqlFixture pg)
{
    private const string Setup = """
        CREATE SCHEMA sales;
        CREATE TABLE clients (id serial PRIMARY KEY, nom varchar(50) NOT NULL, pays text DEFAULT 'FR', secret text);
        CREATE UNIQUE INDEX ux_clients_nom ON clients (nom);
        CREATE INDEX ix_clients_expr ON clients (lower(pays));
        CREATE TABLE sales.commandes (id int PRIMARY KEY, client_id int REFERENCES clients(id), total numeric(10,2), note text);
        CREATE TABLE lignes (commande int NOT NULL, ligne int NOT NULL, produit text, PRIMARY KEY (commande, ligne));
        CREATE TABLE avis (commande int, ligne int, texte text, FOREIGN KEY (commande, ligne) REFERENCES lignes (commande, ligne));
        CREATE VIEW v_clients AS SELECT id, nom FROM clients;
        ALTER TABLE clients ADD COLUMN temporaire int;
        ALTER TABLE clients DROP COLUMN temporaire;
        ALTER TABLE clients ADD COLUMN actif boolean;
        INSERT INTO clients (nom, pays, secret) VALUES ('Ada', 'FR', 'MOT-DE-PASSE-1'), ('Bob', 'FR', NULL), ('Cy', 'BE', NULL);
        INSERT INTO sales.commandes VALUES (1, 1, 10.5, NULL), (2, 1, 10.5, 'x'), (3, 2, NULL, NULL);
        """;

    private async Task<(string Database, string Connection)> NewAsync()
    {
        var database = await pg.NewDatabaseAsync(Setup);
        return (database, pg.Input(database).BuildConnectionString());
    }

    [SqlServerFact]
    public async Task The_schema_is_read_with_types_defaults_keys_relations_and_indexes()
    {
        var (_, connection) = await NewAsync();

        var schema = await new PostgreSqlSchemaConnector().ReadSchemaAsync(connection);

        Assert.Equal("PostgreSql", schema.Type);
        Assert.Equal(["avis", "clients", "lignes", "sales.commandes", "v_clients"], schema.Tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal("View", schema.Tables.Single(t => t.Name == "v_clients").TableType);
        var clients = schema.Tables.Single(t => t.Name == "clients");
        // La colonne supprimée a laissé un trou dans attnum : les positions restent consécutives à partir de 0.
        Assert.Equal(["id", "nom", "pays", "secret", "actif"], clients.Columns.Select(c => c.Name).ToArray());
        Assert.Equal([0, 1, 2, 3, 4], clients.Columns.Select(c => c.Ordinal).ToArray());
        var id = clients.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsNullable);
        Assert.Equal("integer", id.DataType);
        Assert.Contains("nextval", id.DefaultValue);
        Assert.Equal("character varying(50)", clients.Columns[1].DataType);
        Assert.False(clients.Columns[1].IsNullable);
        Assert.Contains("'FR'", clients.Columns[2].DefaultValue);
        Assert.Equal("numeric(10,2)", schema.Tables.Single(t => t.Name == "sales.commandes").Columns[2].DataType);

        Assert.Contains(new SourceForeignKey("sales.commandes", "client_id", "clients", "id"), schema.ForeignKeys);
        var composite = schema.ForeignKeys.Where(fk => fk.Table == "avis").ToList();
        Assert.Equal(["commande", "ligne"], composite.Select(fk => fk.Column).ToArray());
        Assert.NotNull(composite[0].Constraint);
        Assert.Equal(composite[0].Constraint, composite[1].Constraint);

        var unique = schema.Indexes.Single(i => i.Name == "ux_clients_nom");
        Assert.True(unique.IsUnique);
        Assert.Equal(["nom"], unique.Columns.ToArray());
        Assert.Equal(["<expression>"], schema.Indexes.Single(i => i.Name == "ix_clients_expr").Columns.ToArray());
        Assert.DoesNotContain(schema.Indexes, i => i.Name.EndsWith("_pkey")); // clés primaires exclues
    }

    [SqlServerFact]
    public async Task The_connection_is_read_only_whatever_the_account_may_do()
    {
        var (database, _) = await NewAsync();
        var connection = pg.Input(database).BuildConnectionString();

        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("CREATE TABLE ne_doit_pas_exister (id int)", db);

        var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("25006", error.SqlState); // read_only_sql_transaction
    }

    [SqlServerFact]
    public async Task A_wrong_password_or_an_unknown_database_gives_an_instruction_not_a_stack_trace()
    {
        var (database, _) = await NewAsync();

        var password = await Assert.ThrowsAsync<InvalidSourceException>(() =>
            new PostgreSqlSchemaConnector().ReadSchemaAsync(pg.Input(database, "mauvais").BuildConnectionString()));
        var missing = await Assert.ThrowsAsync<InvalidSourceException>(() =>
            new PostgreSqlSchemaConnector().ReadSchemaAsync(pg.Input("absente_" + Guid.NewGuid().ToString("N")).BuildConnectionString()));

        Assert.Contains("Identifiants refusés", password.Message);
        Assert.Contains("Base introuvable", missing.Message);
    }

    private TestCatalog Env(Action<GranitDB.API.Options.CatalogOptions>? configure = null) => new(o =>
    {
        o.AllowSqlServerSources = true;
        o.AllowProfiling = true;
        configure?.Invoke(o);
    });

    [SqlServerFact]
    public async Task A_postgresql_database_is_imported_resynchronised_and_filtered_by_schema()
    {
        var (database, _) = await NewAsync();
        using var env = Env();
        var input = pg.Input(database);
        input.Name = "Boutique PG";

        var test = await env.Importer.TestServerAsync(input);
        var imported = await env.Importer.ImportServerAsync(input);

        Assert.True(test.Ok);
        Assert.Equal(["public", "sales"], test.Schemas.Select(s => s.Name).Order().ToArray());
        Assert.Equal("PostgreSql", imported.Database.Type);
        Assert.Equal("Boutique PG", imported.Database.Name);
        Assert.True(imported.Sync.TablesAdded >= 4);
        var stored = await env.Repository.GetAsync<DatabaseInfo>(imported.Database.Id);
        Assert.Null(stored!.SourcePath);
        Assert.False(string.IsNullOrEmpty(stored.SourceSecret));
        Assert.DoesNotContain(pg.Password, stored.SourceSecret); // chiffrée

        await pg.ExecuteAsync(database, "ALTER TABLE lignes ADD COLUMN prix numeric");
        var resync = (await env.Importer.ResyncAsync(imported.Database.Id))!;
        Assert.Equal(1, resync.Sync.ColumnsAdded);

        var onlySales = pg.Input(database);
        onlySales.Schemas = ["sales"];
        var filtered = await env.Importer.ImportServerAsync(onlySales);
        Assert.Equal(1, filtered.Sync.TablesAdded); // sales.commandes, sa clé vers « clients » est écartée
        Assert.Empty(await env.Repository.GetRelationsOfDatabaseAsync(filtered.Database.Id));
    }

    [SqlServerFact]
    public async Task The_wrong_engine_for_an_existing_database_is_refused_and_disabled_sources_stay_closed()
    {
        var (database, _) = await NewAsync();
        using var env = Env();
        var imported = await env.Importer.ImportServerAsync(pg.Input(database));
        var asMySql = pg.Input(database);
        asMySql.Engine = "MySql";

        var wrong = await Assert.ThrowsAsync<InvalidSourceException>(() => env.Importer.ReplaceServerConnectionAsync(imported.Database.Id, asMySql));
        using var closed = new TestCatalog();
        var disabled = await Assert.ThrowsAsync<InvalidSourceException>(() => closed.Importer.TestServerAsync(pg.Input(database)));

        Assert.Contains("PostgreSql", wrong.Message);
        Assert.Contains("PostgreSQL", disabled.Message);
        Assert.Contains("désactivées", disabled.Message);
    }

    [SqlServerFact]
    public async Task Columns_are_profiled_with_schema_qualified_names_and_types_without_distinct()
    {
        var (database, _) = await NewAsync();
        await pg.ExecuteAsync(database, "CREATE TABLE docs (id int PRIMARY KEY, j json, jb jsonb, t text); INSERT INTO docs VALUES (1, '{}', '{\"a\":1}', 'x'), (2, NULL, '{\"a\":1}', NULL)");
        using var env = Env();
        var imported = await env.Importer.ImportServerAsync(pg.Input(database));
        var service = new ColumnProfilingService(env.Repository, env.Importer, [new PostgreSqlColumnProfiler()],
            Microsoft.Extensions.Options.Options.Create(env.Options));
        var tables = (await env.Repository.FindAsync<TableInfo>("DatabaseId", imported.Database.Id)).ToDictionary(t => t.Name);

        var commandes = (await service.ProfileTableAsync(tables["sales.commandes"].Id))!;
        var docs = (await service.ProfileTableAsync(tables["docs"].Id))!;

        Assert.Equal(3, commandes.RowCount);
        var total = commandes.Columns.Single(c => c.Column == "total");
        Assert.Equal((1L, 1L), (total.NullCount, total.DistinctCount!.Value));
        var by = docs.Columns.ToDictionary(c => c.Column);
        Assert.Null(by["j"].DistinctCount); // json n'a pas d'égalité
        Assert.Equal(1, by["j"].NullCount);
        Assert.Equal(1L, by["jb"].DistinctCount);
        var run = (await service.ProfileDatabaseAsync(imported.Database.Id))!;
        Assert.Equal(0, run.TablesFailed);
    }

    [Fact]
    public void Quoting_splits_the_schema_and_doubles_quotes()
    {
        Assert.Equal("\"public\".\"orders\"", PostgreSqlColumnProfiler.TableReference("orders"));
        Assert.Equal("\"sales\".\"Orders\"", PostgreSqlColumnProfiler.TableReference("sales.Orders"));
        Assert.Equal("\"public\".\"we\"\"ird\"", PostgreSqlColumnProfiler.TableReference("we\"ird"));
    }

    [Fact]
    public void The_connection_string_is_built_by_the_driver_and_asks_for_read_only_transactions()
    {
        var input = new GranitDB.API.Models.Inputs.ServerConnectionInput
        {
            Engine = "PostgreSql", Server = "db.exemple.fr", Port = 6543, Database = "app", User = "lecteur", Password = "p;Host=evil", Encrypt = true
        };

        var built = new NpgsqlConnectionStringBuilder(input.BuildConnectionString());

        Assert.Equal("db.exemple.fr", built.Host);
        Assert.Equal(6543, built.Port);
        Assert.Equal("p;Host=evil", built.Password); // aucune injection d'option
        Assert.Equal(SslMode.Require, built.SslMode);
        Assert.Contains("default_transaction_read_only=on", built.Options);
        Assert.Equal("db.exemple.fr:6543 / app", input.Label);
    }
}
