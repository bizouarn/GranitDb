using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GranitDb.Tests;

/// <summary>Catalogue SQLite jetable dans un dossier temporaire : aucun serveur n'est nécessaire.</summary>
public sealed class TestCatalog : IDisposable
{
    public TestCatalog(Action<CatalogOptions>? configure = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "granitdb-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Options = new CatalogOptions { Provider = "Sqlite", DataDirectory = Path.Combine(Directory, "data") };
        configure?.Invoke(Options);
        var wrapped = Microsoft.Extensions.Options.Options.Create(Options);
        Factory = new DbConnectionFactory(wrapped);
        Repository = new CatalogRepository(Factory, wrapped);
        Synchronizer = new SchemaSynchronizer(Repository);
        Connectors = [new SqliteSchemaConnector(), new SqlServerSchemaConnector(), new PostgreSqlSchemaConnector(), new MySqlSchemaConnector(), new H2SchemaConnector()];
        Protector = new DataProtectionSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        Webhooks = new WebhookDispatcher(new WebhookStore(Factory), Protector,
            Microsoft.Extensions.Options.Options.Create(new GranitDB.API.Options.WebhookOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebhookDispatcher>.Instance);
        Importer = new DatabaseImportService(Repository, Synchronizer, Connectors, wrapped, Protector, Webhooks);
        Repository.InitializeAsync().GetAwaiter().GetResult();
    }

    public string Directory { get; }
    public CatalogOptions Options { get; }
    public IDbConnectionFactory Factory { get; }
    public CatalogRepository Repository { get; }
    public SchemaSynchronizer Synchronizer { get; }
    public ISchemaConnector[] Connectors { get; }
    public DatabaseImportService Importer { get; }
    public ISecretProtector Protector { get; }
    public WebhookDispatcher Webhooks { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }

    public string NewFile(string name = "source.db")
    {
        return Path.Combine(Directory, name);
    }

    /// <summary>Exécute du SQL sur un fichier SQLite (le crée au besoin), sans pool de connexions.</summary>
    public static void Execute(string path, string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = new SqliteConnection(cs);
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Petite base e-commerce : FK simple, FK implicite, clé composite, vue, colonne sans type.</summary>
    public const string SampleSql = """
        CREATE TABLE customers (
            id INTEGER PRIMARY KEY,
            email TEXT NOT NULL,
            nickname TEXT,
            created VARCHAR(30) NOT NULL DEFAULT 'now'
        );
        CREATE TABLE orders (
            id INTEGER PRIMARY KEY,
            customer_id INTEGER NOT NULL REFERENCES customers(id),
            note TEXT
        );
        CREATE TABLE order_lines (
            order_id INTEGER NOT NULL,
            line_no INTEGER NOT NULL,
            sku,
            qty INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (order_id, line_no),
            FOREIGN KEY (order_id) REFERENCES orders
        );
        CREATE TABLE tags (id INTEGER PRIMARY KEY, label TEXT);
        CREATE VIEW customer_emails AS SELECT id, email FROM customers;
        """;

    public string CreateSampleDb(string name = "sample.db")
    {
        var path = NewFile(name);
        Execute(path, SampleSql);
        return path;
    }
}
