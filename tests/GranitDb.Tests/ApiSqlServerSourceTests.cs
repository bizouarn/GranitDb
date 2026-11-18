using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace GranitDb.Tests;

/// <summary>
///     Documenter une vraie base SQL Server (conteneur) depuis l'API : test de connexion, import, schémas choisis,
///     resynchronisation avec aperçu, changement de connexion, secrets chiffrés.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ApiSqlServerSourceTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiSqlServerSourceTests(SqlServerFixture sql)
    {
        _sql = sql;
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data"),
            ["Catalog:AllowSqlServerSources"] = "true"
        });
        _client = _factory.CreateKestrelClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private sealed record Source(string ConnectionString, string Host, int Port, string Database, string User, string Password);

    /// <summary>Crée une base source : un client, des commandes (schéma sales) qui le référencent, une vue.</summary>
    private async Task<Source> NewSourceAsync(params string[] more)
    {
        var connection = _sql.NewDatabaseConnectionString("src");
        await SqlServerBootstrap.EnsureDatabaseAsync(connection);
        await SqlServerFixture.ExecuteAsync(connection,
            "CREATE SCHEMA sales",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY, Email NVARCHAR(100) NOT NULL, Nickname NVARCHAR(50) NULL)",
            "CREATE TABLE sales.Orders (Id INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL REFERENCES dbo.Customers(Id), Total INT)",
            "CREATE INDEX IX_Orders_Total ON sales.Orders(Total)",
            "CREATE VIEW dbo.CustomerEmails AS SELECT Id, Email FROM dbo.Customers");
        if (more.Length > 0) await SqlServerFixture.ExecuteAsync(connection, more);
        var builder = new SqlConnectionStringBuilder(connection);
        var parts = builder.DataSource.Replace("tcp:", "").Split(',');
        return new Source(connection, parts[0], int.Parse(parts[1]), builder.InitialCatalog, builder.UserID, builder.Password);
    }

    private static Dictionary<string, object?> Body(Source source, Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["server"] = source.Host, ["port"] = source.Port, ["database"] = source.Database, ["authMode"] = "Sql",
            ["user"] = source.User, ["password"] = source.Password, ["encrypt"] = true, ["trustServerCertificate"] = true,
            ["timeoutSeconds"] = 5
        };
        change?.Invoke(body);
        return body;
    }

    private async Task<JsonElement> PostAsync(string url, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> ImportAsync(Source source, Action<Dictionary<string, object?>>? change = null)
    {
        var result = await PostAsync("/api/v1/import/sqlserver", Body(source, change), HttpStatusCode.Created);
        return result.GetProperty("database").GetProperty("id").GetString()!;
    }

    private string StoredSecret(string databaseId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_env.Directory, "api-data", "granitdb.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceSecret FROM Databases WHERE Id = $id";
        command.Parameters.AddWithValue("$id", databaseId);
        return (string)command.ExecuteScalar()!;
    }

    private async Task<string[]> TableNamesAsync(string databaseId) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo?databaseId={databaseId}")).EnumerateArray()
        .Select(t => t.GetProperty("name").GetString()!).Order().ToArray();

    // --- Test de connexion ---

    [SqlServerFact]
    public async Task The_test_says_what_would_be_documented_without_saving_anything()
    {
        var source = await NewSourceAsync();

        var result = await PostAsync("/api/v1/import/sqlserver/test", Body(source));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(2, result.GetProperty("tables").GetInt32());
        Assert.Equal(1, result.GetProperty("views").GetInt32());
        Assert.Contains("Connexion réussie", result.GetProperty("message").GetString());
        var schemas = result.GetProperty("schemas").EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("tables").GetInt32());
        Assert.Equal(2, schemas["dbo"]); // Customers et la vue
        Assert.Equal(1, schemas["sales"]);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/DatabaseInfo")).EnumerateArray());
    }

    [SqlServerFact]
    public async Task A_wrong_password_a_missing_database_and_a_closed_port_each_get_their_own_instruction()
    {
        var source = await NewSourceAsync();

        var login = await PostAsync("/api/v1/import/sqlserver/test", Body(source, b => b["password"] = "Mauvais-mot-de-passe-1"));
        var missing = await PostAsync("/api/v1/import/sqlserver/test", Body(source, b => b["database"] = "base_qui_n_existe_pas"));
        var closed = await PostAsync("/api/v1/import/sqlserver/test", Body(source, b => { b["port"] = 1; b["timeoutSeconds"] = 2; }));

        Assert.False(login.GetProperty("ok").GetBoolean());
        Assert.Contains("Identifiants refusés", login.GetProperty("message").GetString());
        Assert.False(missing.GetProperty("ok").GetBoolean());
        Assert.Contains("Base introuvable", missing.GetProperty("message").GetString());
        Assert.False(closed.GetProperty("ok").GetBoolean());
        Assert.Contains("injoignable", closed.GetProperty("message").GetString());
        Assert.DoesNotContain("Mauvais-mot-de-passe-1", login.ToString());
    }

    [SqlServerFact]
    public async Task A_read_only_account_that_can_only_see_metadata_is_enough()
    {
        var source = await NewSourceAsync();
        var master = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var login = $"lecteur_{Guid.NewGuid():N}"[..20];
        await SqlServerFixture.ExecuteAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = 'Lecteur_Passw0rd!x', CHECK_POLICY = OFF");
        await SqlServerFixture.ExecuteAsync(source.ConnectionString, $"CREATE USER [{login}] FOR LOGIN [{login}]", $"GRANT VIEW DEFINITION TO [{login}]");

        var result = await PostAsync("/api/v1/import/sqlserver/test", Body(source, b => { b["user"] = login; b["password"] = "Lecteur_Passw0rd!x"; }));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(2, result.GetProperty("tables").GetInt32());
    }

    [SqlServerFact]
    public async Task A_connected_account_that_sees_no_table_is_told_to_check_its_rights()
    {
        var source = await NewSourceAsync();
        var master = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        var login = $"aveugle_{Guid.NewGuid():N}"[..20];
        await SqlServerFixture.ExecuteAsync(master, $"CREATE LOGIN [{login}] WITH PASSWORD = 'Aveugle_Passw0rd!x', CHECK_POLICY = OFF");
        await SqlServerFixture.ExecuteAsync(source.ConnectionString, $"CREATE USER [{login}] FOR LOGIN [{login}]");

        var result = await PostAsync("/api/v1/import/sqlserver/test", Body(source, b => { b["user"] = login; b["password"] = "Aveugle_Passw0rd!x"; }));
        var import = await _client.PostAsJsonAsync("/api/v1/import/sqlserver", Body(source, b => { b["user"] = login; b["password"] = "Aveugle_Passw0rd!x"; }));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(0, result.GetProperty("tables").GetInt32());
        Assert.Contains("VIEW DEFINITION", result.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, import.StatusCode); // rien à documenter : pas de base vide créée
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/DatabaseInfo")).EnumerateArray());
    }

    // --- Import ---

    [SqlServerFact]
    public async Task Importing_documents_the_schema_and_never_returns_the_connection()
    {
        var source = await NewSourceAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/import/sqlserver", Body(source, b => { b["name"] = "Distante"; b["description"] = "Base du serveur"; }));
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = JsonDocument.Parse(text).RootElement;
        var database = json.GetProperty("database");
        Assert.Equal("Distante", database.GetProperty("name").GetString());
        Assert.Equal("SqlServer", database.GetProperty("type").GetString());
        Assert.Equal("Base du serveur", database.GetProperty("description").GetString());
        Assert.True(database.GetProperty("canResync").GetBoolean());
        Assert.Equal($"{source.Host}:{source.Port} / {source.Database}", database.GetProperty("sourceLabel").GetString());
        Assert.Equal(3, json.GetProperty("sync").GetProperty("tablesAdded").GetInt32());
        var id = database.GetProperty("id").GetString()!;
        Assert.Equal(["CustomerEmails", "Customers", "sales.Orders"], await TableNamesAsync(id));
        Assert.Single((await _client.GetFromJsonAsync<JsonElement>($"/api/v1/RelationInfo")).EnumerateArray()); // sales.Orders → Customers
        foreach (var body in new[] { text, await _client.GetStringAsync($"/api/v1/DatabaseInfo/{id}"), await _client.GetStringAsync("/api/v1/DatabaseInfo") })
        {
            Assert.DoesNotContain(source.Password, body);
            Assert.DoesNotContain(source.User, body);
            Assert.DoesNotContain("sourceSecret", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionString", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SqlServerFact]
    public async Task The_connection_is_stored_encrypted_in_the_catalog()
    {
        var source = await NewSourceAsync();

        var id = await ImportAsync(source);

        var stored = StoredSecret(id);
        Assert.False(string.IsNullOrWhiteSpace(stored));
        Assert.DoesNotContain(source.Password, stored);
        Assert.DoesNotContain(source.User, stored);
        Assert.DoesNotContain(source.Host, stored);
        Assert.DoesNotContain(source.Database, stored);
    }

    [SqlServerFact]
    public async Task Only_the_chosen_schemas_are_documented_and_the_choice_is_kept_for_resynchronizations()
    {
        var source = await NewSourceAsync();

        var id = await ImportAsync(source, b => b["schemas"] = new[] { "sales" });

        Assert.Equal(["sales.Orders"], await TableNamesAsync(id));
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/RelationInfo")).EnumerateArray()); // la clé vers dbo.Customers sort du périmètre
        await SqlServerFixture.ExecuteAsync(source.ConnectionString, "CREATE TABLE dbo.Later (Id INT NOT NULL PRIMARY KEY)", "CREATE TABLE sales.Lines (Id INT NOT NULL PRIMARY KEY)");
        var preview = await PostAsync($"/api/v1/DatabaseInfo/{id}/sync/preview", new { });
        var changes = preview.GetProperty("changes").EnumerateArray().Select(c => (c.GetProperty("kind").GetString(), c.GetProperty("table").GetString())).ToList();
        Assert.Contains(("table", "sales.Lines"), changes);
        Assert.DoesNotContain(("table", "Later"), changes); // hors des schémas choisis
    }

    [SqlServerFact]
    public async Task Importing_a_source_with_nothing_in_the_chosen_schemas_creates_nothing()
    {
        var source = await NewSourceAsync();

        var response = await _client.PostAsJsonAsync("/api/v1/import/sqlserver", Body(source, b => b["schemas"] = new[] { "inexistant" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>("/api/v1/DatabaseInfo")).EnumerateArray());
    }

    // --- Resynchronisation ---

    [SqlServerFact]
    public async Task A_changed_source_is_previewed_then_resynchronized_keeping_the_documentation()
    {
        var source = await NewSourceAsync();
        var id = await ImportAsync(source);
        var customers = (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo?databaseId={id}&name=Customers")).EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Customers").GetProperty("id").GetString();
        await _client.PutAsJsonAsync($"/api/v1/TableInfo/{customers}/documentation", new { description = "Les clients" });
        var nickname = (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/ColumnInfo?tableId={customers}&name=Nickname"))[0].GetProperty("id").GetString();
        await _client.PutAsJsonAsync($"/api/v1/ColumnInfo/{nickname}/documentation", new { description = "Pseudo" });
        await SqlServerFixture.ExecuteAsync(source.ConnectionString,
            "ALTER TABLE dbo.Customers ADD Phone NVARCHAR(20) NULL", "DROP VIEW dbo.CustomerEmails", "ALTER TABLE dbo.Customers DROP COLUMN Nickname");

        var preview = await PostAsync($"/api/v1/DatabaseInfo/{id}/sync/preview", new { });

        var changes = preview.GetProperty("changes").EnumerateArray().Select(c =>
            (c.GetProperty("kind").GetString(), c.GetProperty("action").GetString(), c.GetProperty("name").GetString(), c.GetProperty("losesDocumentation").GetBoolean())).ToList();
        Assert.Contains(("column", "added", "Phone", false), changes);
        Assert.Contains(("column", "removed", "Nickname", true), changes); // décrite : perte annoncée
        Assert.Equal(2, preview.GetProperty("changes").GetArrayLength() - 1); // colonne ajoutée, colonne retirée, vue retirée
        var sync = await PostAsync($"/api/v1/DatabaseInfo/{id}/sync", new { });
        Assert.Equal(1, sync.GetProperty("columnsAdded").GetInt32());
        Assert.Equal(1, sync.GetProperty("tablesRemoved").GetInt32());
        Assert.False(sync.TryGetProperty("database", out _));
        Assert.Equal("Les clients", (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/TableInfo/{customers}")).GetProperty("description").GetString());
        Assert.False((await PostAsync($"/api/v1/DatabaseInfo/{id}/sync/preview", new { })).GetProperty("hasChanges").GetBoolean());
    }

    [SqlServerFact]
    public async Task A_source_that_became_unreachable_is_reported_when_resynchronizing()
    {
        var source = await NewSourceAsync();
        var id = await ImportAsync(source);
        var master = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await SqlServerFixture.ExecuteAsync(master, $"ALTER DATABASE [{source.Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE", $"ALTER DATABASE [{source.Database}] SET OFFLINE");

        var sync = await _client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null);

        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await sync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()));
        Assert.Equal(3, (await TableNamesAsync(id)).Length); // le catalogue reste tel quel
    }

    // --- Changer la connexion ---

    [SqlServerFact]
    public async Task The_connection_can_be_replaced_with_a_new_password_and_the_database_is_resynchronized()
    {
        var source = await NewSourceAsync();
        var id = await ImportAsync(source);
        var before = StoredSecret(id);
        await SqlServerFixture.ExecuteAsync(source.ConnectionString, "CREATE TABLE dbo.Added (Id INT NOT NULL PRIMARY KEY)");

        var wrong = await _client.PutAsJsonAsync($"/api/v1/import/sqlserver/{id}", Body(source, b => b["password"] = "Mauvais-1"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(before, StoredSecret(id)); // un essai raté ne remplace rien
        var good = await _client.PutAsJsonAsync($"/api/v1/import/sqlserver/{id}", Body(source));

        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var result = await good.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("sync").GetProperty("tablesAdded").GetInt32());
        Assert.Equal(id, result.GetProperty("database").GetProperty("id").GetString());
        Assert.NotEqual(before, StoredSecret(id)); // rechiffré (chiffrement non déterministe)
        Assert.Contains("Added", await TableNamesAsync(id));
    }

    [SqlServerFact]
    public async Task Replacing_the_connection_keeps_the_name_and_description_of_the_database()
    {
        var source = await NewSourceAsync();
        var id = await ImportAsync(source, b => { b["name"] = "Nom choisi"; b["description"] = "Description saisie"; });

        await _client.PutAsJsonAsync($"/api/v1/import/sqlserver/{id}", Body(source, b => b["schemas"] = new[] { "dbo" }));

        var database = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/DatabaseInfo/{id}");
        Assert.Equal("Nom choisi", database.GetProperty("name").GetString());
        Assert.Equal("Description saisie", database.GetProperty("description").GetString());
        Assert.Equal(["CustomerEmails", "Customers"], await TableNamesAsync(id)); // le nouveau choix de schémas s'applique
    }

    // --- Une base supprimée du catalogue emporte sa connexion ---

    [SqlServerFact]
    public async Task Deleting_the_database_removes_the_stored_connection()
    {
        var source = await NewSourceAsync();
        var id = await ImportAsync(source);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/v1/DatabaseInfo/{id}")).StatusCode);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_env.Directory, "api-data", "granitdb.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Databases";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }
}
