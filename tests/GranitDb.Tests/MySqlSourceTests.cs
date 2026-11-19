using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using MySqlConnector;

namespace GranitDb.Tests;

/// <summary>Documenter une base MySQL : schéma, import, synchronisation, profilage (contre un vrai serveur en conteneur).</summary>
[Collection(MySqlCollection.Name)]
public class MySqlSourceTests(MySqlFixture my)
{
    private static readonly string[] Setup =
    [
        """
        CREATE TABLE clients (id INT AUTO_INCREMENT PRIMARY KEY, nom VARCHAR(50) NOT NULL, pays VARCHAR(2) DEFAULT 'FR',
            secret TEXT, actif TINYINT(1) NULL, UNIQUE KEY ux_clients_nom (nom), KEY ix_clients_pays (pays, nom))
        """,
        "CREATE TABLE commandes (id INT PRIMARY KEY, client_id INT, total DECIMAL(10,2), note TEXT, CONSTRAINT fk_cmd_client FOREIGN KEY (client_id) REFERENCES clients(id))",
        "CREATE TABLE lignes (commande INT NOT NULL, ligne INT NOT NULL, produit VARCHAR(20), PRIMARY KEY (commande, ligne))",
        "CREATE TABLE avis (commande INT, ligne INT, texte TEXT, CONSTRAINT fk_avis FOREIGN KEY (commande, ligne) REFERENCES lignes (commande, ligne))",
        "CREATE VIEW v_clients AS SELECT id, nom FROM clients",
        "INSERT INTO clients (nom, pays, secret) VALUES ('Ada', 'FR', 'MOT-DE-PASSE-1'), ('Bob', 'FR', NULL), ('Cy', 'BE', NULL)",
        "INSERT INTO commandes VALUES (1, 1, 10.5, NULL), (2, 1, 10.5, 'x'), (3, 2, NULL, NULL)"
    ];

    private async Task<string> NewAsync() => await my.NewDatabaseAsync(Setup);

    [SqlServerFact]
    public async Task The_schema_is_read_with_types_defaults_keys_relations_and_indexes()
    {
        var database = await NewAsync();

        var schema = await new MySqlSchemaConnector().ReadSchemaAsync(my.Input(database).BuildConnectionString());

        Assert.Equal("MySql", schema.Type);
        Assert.Equal(database, schema.DatabaseName);
        Assert.Equal(["avis", "clients", "commandes", "lignes", "v_clients"], schema.Tables.Select(t => t.Name).Order().ToArray());
        Assert.Equal("View", schema.Tables.Single(t => t.Name == "v_clients").TableType);
        var clients = schema.Tables.Single(t => t.Name == "clients");
        Assert.Equal(["id", "nom", "pays", "secret", "actif"], clients.Columns.Select(c => c.Name).ToArray());
        Assert.Equal([0, 1, 2, 3, 4], clients.Columns.Select(c => c.Ordinal).ToArray());
        Assert.True(clients.Columns[0].IsPrimaryKey);
        Assert.False(clients.Columns[0].IsNullable);
        Assert.Equal("int", clients.Columns[0].DataType);
        Assert.Equal("varchar(50)", clients.Columns[1].DataType);
        Assert.False(clients.Columns[1].IsNullable);
        Assert.Equal("FR", clients.Columns[2].DefaultValue);
        Assert.True(clients.Columns[4].IsNullable);
        Assert.Equal(["commande", "ligne"], schema.Tables.Single(t => t.Name == "lignes").Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToArray());

        Assert.Contains(new SourceForeignKey("commandes", "client_id", "clients", "id"), schema.ForeignKeys);
        var composite = schema.ForeignKeys.Where(fk => fk.Table == "avis").ToList();
        Assert.Equal(["commande", "ligne"], composite.Select(fk => fk.Column).ToArray());
        Assert.Equal(composite[0].Constraint, composite[1].Constraint);
        Assert.NotNull(composite[0].Constraint);

        var unique = schema.Indexes.Single(i => i.Name == "ux_clients_nom");
        Assert.True(unique.IsUnique);
        Assert.Equal(["pays", "nom"], schema.Indexes.Single(i => i.Name == "ix_clients_pays").Columns.ToArray());
        Assert.DoesNotContain(schema.Indexes, i => i.Name == "PRIMARY");
    }

    [SqlServerFact]
    public async Task A_wrong_password_or_an_unknown_database_gives_an_instruction_not_a_stack_trace()
    {
        var database = await NewAsync();

        var password = await Assert.ThrowsAsync<InvalidSourceException>(() =>
            new MySqlSchemaConnector().ReadSchemaAsync(my.Input(database, "mauvais").BuildConnectionString()));
        var missing = await Assert.ThrowsAsync<InvalidSourceException>(() =>
            new MySqlSchemaConnector().ReadSchemaAsync(my.Input("absente_" + Guid.NewGuid().ToString("N")).BuildConnectionString()));

        Assert.Contains("Identifiants refusés", password.Message);
        Assert.Contains("Base introuvable", missing.Message);
    }

    private TestCatalog Env() => new(o =>
    {
        o.AllowSqlServerSources = true;
        o.AllowProfiling = true;
    });

    [SqlServerFact]
    public async Task A_mysql_database_is_imported_and_resynchronised()
    {
        var database = await NewAsync();
        using var env = Env();
        var input = my.Input(database);
        input.Name = "Boutique MySQL";
        input.Schemas = ["ignore"]; // sans effet : la base est l'unité

        var test = await env.Importer.TestServerAsync(input);
        var imported = await env.Importer.ImportServerAsync(input);

        Assert.True(test.Ok);
        Assert.Equal(1, test.Schemas.Count);
        Assert.Equal("MySql", imported.Database.Type);
        Assert.Equal(5, imported.Sync.TablesAdded);
        var stored = await env.Repository.GetAsync<DatabaseInfo>(imported.Database.Id);
        Assert.False(string.IsNullOrEmpty(stored!.SourceSecret));
        Assert.DoesNotContain(my.Password, stored.SourceSecret);

        await my.ExecuteAsync(database, "ALTER TABLE lignes ADD COLUMN prix DECIMAL(8,2)");
        var resync = (await env.Importer.ResyncAsync(imported.Database.Id))!;
        Assert.Equal(1, resync.Sync.ColumnsAdded);
        Assert.Equal(2, (await env.Repository.GetRelationsOfDatabaseAsync(imported.Database.Id)).Count);
    }

    [SqlServerFact]
    public async Task Columns_are_profiled_and_a_table_missing_from_the_source_is_reported()
    {
        var database = await NewAsync();
        using var env = Env();
        var imported = await env.Importer.ImportServerAsync(my.Input(database));
        var service = new ColumnProfilingService(env.Repository, env.Importer, [new MySqlColumnProfiler()],
            Microsoft.Extensions.Options.Options.Create(env.Options));
        var tables = (await env.Repository.FindAsync<TableInfo>("DatabaseId", imported.Database.Id)).ToDictionary(t => t.Name);

        var commandes = (await service.ProfileTableAsync(tables["commandes"].Id))!;

        Assert.Equal(3, commandes.RowCount);
        var total = commandes.Columns.Single(c => c.Column == "total");
        Assert.Equal((1L, 1L), (total.NullCount, total.DistinctCount!.Value));
        Assert.Equal("unique", commandes.Columns.Single(c => c.Column == "id").Kind);

        await my.ExecuteAsync(database, "DROP TABLE avis");
        var run = (await service.ProfileDatabaseAsync(imported.Database.Id))!;
        Assert.Equal(1, run.TablesFailed);
        Assert.Contains(run.Errors, e => e.StartsWith("avis :"));
    }

    [Fact]
    public void The_connection_string_is_built_by_the_driver_without_option_injection()
    {
        var input = new GranitDB.API.Models.Inputs.ServerConnectionInput
        {
            Engine = "MySql", Server = "db.exemple.fr", Port = 3307, Database = "app", User = "lecteur", Password = "p;Server=evil", Encrypt = true
        };

        var built = new MySqlConnectionStringBuilder(input.BuildConnectionString());

        Assert.Equal("db.exemple.fr", built.Server);
        Assert.Equal(3307u, built.Port);
        Assert.Equal("p;Server=evil", built.Password);
        Assert.Equal(MySqlSslMode.VerifyCA, built.SslMode);
        input.TrustServerCertificate = true;
        Assert.Equal(MySqlSslMode.Required, new MySqlConnectionStringBuilder(input.BuildConnectionString()).SslMode);
        input.Encrypt = false;
        Assert.Equal(MySqlSslMode.None, new MySqlConnectionStringBuilder(input.BuildConnectionString()).SslMode);
    }

    [Fact]
    public void Integrated_authentication_is_refused_outside_SQL_Server_and_the_engine_must_be_known()
    {
        var input = new GranitDB.API.Models.Inputs.ServerConnectionInput
        { Engine = "PostgreSql", Server = "x", Database = "d", AuthMode = "Integrated" };

        var errors = input.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(input)).ToList();

        Assert.Contains(errors, e => e.ErrorMessage!.Contains("PostgreSQL"));
        Assert.Equal("PostgreSql", GranitDB.API.Connectors.ServerEngines.Of("postgresql").Type);
        Assert.Null(GranitDB.API.Connectors.ServerEngines.Find("Oracle"));
        Assert.Equal("sales", GranitDB.API.Connectors.ServerEngines.PostgreSql.SchemaOf("sales.orders"));
        Assert.Equal("public", GranitDB.API.Connectors.ServerEngines.PostgreSql.SchemaOf("orders"));
    }
}
