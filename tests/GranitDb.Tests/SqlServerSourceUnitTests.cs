using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GranitDB.API.Connectors;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;

namespace GranitDb.Tests;

/// <summary>Connexion SQL Server : chaîne construite sans injection, libellé sans secret, schémas choisis.</summary>
public class SqlServerConnectionInputTests
{
    private static SqlServerConnectionInput Sql(Action<SqlServerConnectionInput>? change = null)
    {
        var input = new SqlServerConnectionInput { Server = "srv-sql", Database = "Boutique", User = "lecteur", Password = "s3cret" };
        change?.Invoke(input);
        return input;
    }

    [Fact]
    public void The_connection_string_asks_for_encryption_and_read_only_intent_and_names_the_application()
    {
        var builder = new SqlConnectionStringBuilder(Sql().BuildConnectionString());

        Assert.Equal("srv-sql", builder.DataSource);
        Assert.Equal("Boutique", builder.InitialCatalog);
        Assert.Equal("lecteur", builder.UserID);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.Equal(ApplicationIntent.ReadOnly, builder.ApplicationIntent);
        Assert.Equal("GranitDb", builder.ApplicationName);
        Assert.Equal(15, builder.ConnectTimeout);
    }

    [Fact]
    public void The_port_is_appended_and_windows_authentication_carries_no_credentials()
    {
        var builder = new SqlConnectionStringBuilder(Sql(i =>
        {
            i.Port = 1444;
            i.AuthMode = "Integrated";
            i.User = null;
            i.Password = null;
            i.Encrypt = false;
            i.TrustServerCertificate = true;
            i.TimeoutSeconds = 3;
        }).BuildConnectionString());

        Assert.Equal("srv-sql,1444", builder.DataSource);
        Assert.True(builder.IntegratedSecurity);
        Assert.Equal("", builder.UserID);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
        Assert.Equal(3, builder.ConnectTimeout);
    }

    [Theory]
    [InlineData("p;ass=word")]
    [InlineData("a\"b'c")]
    [InlineData("Server=evil;Database=master")]
    [InlineData("  espaces ")]
    public void A_password_cannot_inject_other_options_into_the_connection_string(string password)
    {
        var connection = Sql(i => i.Password = password).BuildConnectionString();

        var parsed = new SqlConnectionStringBuilder(connection);
        Assert.Equal(password, parsed.Password);
        Assert.Equal("srv-sql", parsed.DataSource);
        Assert.Equal("Boutique", parsed.InitialCatalog);
    }

    [Fact]
    public void A_server_or_database_name_with_separators_stays_a_single_value()
    {
        var parsed = new SqlConnectionStringBuilder(Sql(i =>
        {
            i.Server = "srv;Database=master";
            i.Database = "b;User Id=sa";
        }).BuildConnectionString());

        Assert.Equal("srv;Database=master", parsed.DataSource);
        Assert.Equal("b;User Id=sa", parsed.InitialCatalog);
        Assert.Equal("lecteur", parsed.UserID);
    }

    [Fact]
    public void The_label_names_the_server_and_database_and_never_the_credentials()
    {
        Assert.Equal("srv-sql / Boutique", Sql().Label);
        Assert.Equal("srv-sql:1444 / Boutique", Sql(i => i.Port = 1444).Label);
        Assert.DoesNotContain("lecteur", Sql().Label);
        Assert.DoesNotContain("s3cret", Sql().Label);
    }

    [Fact]
    public void Selected_schemas_are_trimmed_deduplicated_and_null_means_all()
    {
        Assert.Null(Sql().SelectedSchemas);
        Assert.Null(Sql(i => i.Schemas = []).SelectedSchemas);
        Assert.Equal(["dbo", "sales"], Sql(i => i.Schemas = [" dbo ", "DBO", "sales"]).SelectedSchemas);
    }

    private static List<string> Errors(SqlServerConnectionInput input)
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(input);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(input, context, results, validateAllProperties: true);
        return results.SelectMany(r => r.MemberNames).ToList();
    }

    [Fact]
    public void Validation_requires_credentials_for_sql_authentication_only()
    {
        Assert.Empty(Errors(Sql()));
        Assert.Contains("User", Errors(Sql(i => i.User = " ")));
        Assert.Contains("Password", Errors(Sql(i => i.Password = "")));
        Assert.Empty(Errors(Sql(i => { i.AuthMode = "Integrated"; i.User = null; i.Password = null; })));
    }

    [Fact]
    public void Validation_checks_the_server_port_auth_mode_timeout_and_schema_names()
    {
        Assert.Contains("Server", Errors(Sql(i => i.Server = "")));
        Assert.Contains("Database", Errors(Sql(i => i.Database = "")));
        Assert.Contains("Port", Errors(Sql(i => i.Port = 70000)));
        Assert.Contains("AuthMode", Errors(Sql(i => i.AuthMode = "Kerberos")));
        Assert.Contains("TimeoutSeconds", Errors(Sql(i => i.TimeoutSeconds = 0)));
        Assert.Contains("Schemas", Errors(Sql(i => i.Schemas = ["dbo", " "])));
    }
}

/// <summary>Choix des schémas à documenter.</summary>
public class SourceSchemaFilterTests
{
    private static SourceTable Table(string name) => new(name, "Table", [new SourceColumn("id", "int", false, true, null, 0)]);

    private static SourceSchema Schema() => new("db", "SqlServer",
        [Table("Customers"), Table("sales.Orders"), Table("hr.Staff"), Table("sales.Lines")],
        [new SourceForeignKey("sales.Orders", "cid", "Customers", "id"),
         new SourceForeignKey("sales.Lines", "oid", "sales.Orders", "id"),
         new SourceForeignKey("hr.Staff", "cid", "Customers", "id")],
        [new SourceIndex("sales.Orders", "ix_o", false, ["cid"]), new SourceIndex("hr.Staff", "ix_s", false, ["cid"])]);

    [Theory]
    [InlineData("Customers", "dbo")]
    [InlineData("sales.Orders", "sales")]
    [InlineData("a.b.c", "a")]
    public void The_schema_of_a_displayed_table_name(string name, string schema)
    {
        Assert.Equal(schema, SourceSchemaFilter.SchemaOf(name));
    }

    [Fact]
    public void Without_a_choice_nothing_is_filtered()
    {
        var schema = Schema();

        Assert.Same(schema, SourceSchemaFilter.Apply(schema, null));
        Assert.Same(schema, SourceSchemaFilter.Apply(schema, []));
    }

    [Fact]
    public void Only_the_chosen_schemas_remain_with_the_keys_and_indexes_that_still_make_sense()
    {
        var result = SourceSchemaFilter.Apply(Schema(), ["SALES"]); // sans tenir compte de la casse

        Assert.Equal(["sales.Lines", "sales.Orders"], result.Tables.Select(t => t.Name).Order().ToArray());
        // La clé vers Customers (hors choix) disparaît ; celle entre deux tables gardées reste.
        Assert.Equal([new SourceForeignKey("sales.Lines", "oid", "sales.Orders", "id")], result.ForeignKeys);
        Assert.Equal(["ix_o"], result.Indexes.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Dbo_is_chosen_by_its_name_and_a_choice_matching_nothing_leaves_nothing()
    {
        Assert.Equal(["Customers"], SourceSchemaFilter.Apply(Schema(), ["dbo"]).Tables.Select(t => t.Name).ToArray());
        var none = SourceSchemaFilter.Apply(Schema(), ["autre"]);
        Assert.Empty(none.Tables);
        Assert.Empty(none.ForeignKeys);
        Assert.Empty(none.Indexes);
    }
}

/// <summary>Chiffrement des connexions enregistrées.</summary>
public class SecretProtectorTests
{
    [Fact]
    public void A_secret_round_trips_and_the_stored_text_does_not_contain_it()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        const string secret = """{"ConnectionString":"Server=srv;Password=Tr3s-S3cret"}""";

        var stored = protector.Protect(secret);

        Assert.DoesNotContain("Tr3s-S3cret", stored);
        Assert.DoesNotContain("srv", stored);
        Assert.NotEqual(stored, protector.Protect(secret)); // chiffrement non déterministe
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    [Fact]
    public void Another_key_ring_or_a_tampered_value_cannot_be_read_and_fails_with_a_cryptographic_error()
    {
        var one = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var other = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var stored = one.Protect("secret");

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(stored));
        Assert.ThrowsAny<CryptographicException>(() => one.Unprotect(stored[..^4] + "AAAA"));
        Assert.ThrowsAny<Exception>(() => one.Unprotect("pas chiffré"));
    }

    [Fact]
    public void Persistent_keys_let_a_new_instance_read_what_an_earlier_one_wrote()
    {
        var directory = Path.Combine(Path.GetTempPath(), "granitdb-tests", Guid.NewGuid().ToString("N"));
        try
        {
            IDataProtectionProvider Provider() => DataProtectionProvider.Create(new DirectoryInfo(directory),
                options => options.SetApplicationName("GranitDb"));
            var stored = new DataProtectionSecretProtector(Provider()).Protect("secret");

            Assert.Equal("secret", new DataProtectionSecretProtector(Provider()).Unprotect(stored));
            Assert.NotEmpty(Directory.GetFiles(directory, "key-*.xml"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

/// <summary>Ce que l'API dit d'une base : jamais le chemin du fichier, jamais la connexion.</summary>
public class DatabaseViewTests
{
    [Fact]
    public void A_view_keeps_the_public_fields_and_reports_whether_the_source_can_be_reread()
    {
        var withFile = DatabaseView.From(new DatabaseInfo { Id = "1", Name = "A", Type = "Sqlite", SourcePath = "/data/a.db", Description = "d", IsPublic = true });
        var withSecret = DatabaseView.From(new DatabaseInfo { Id = "2", Name = "B", Type = "SqlServer", SourceSecret = "chiffré", SourceLabel = "srv / b" });
        var none = DatabaseView.From(new DatabaseInfo { Id = "3", Name = "C", Type = "Sqlite", SourcePath = "  " });

        Assert.True(withFile.CanResync);
        Assert.Equal("d", withFile.Description);
        Assert.True(withFile.IsPublic);
        Assert.True(withSecret.CanResync);
        Assert.Equal("srv / b", withSecret.SourceLabel);
        Assert.False(none.CanResync);
    }

    [Fact]
    public void The_serialized_view_and_entity_never_contain_the_secret_or_the_path()
    {
        var entity = new DatabaseInfo { Id = "1", Name = "A", Type = "SqlServer", SourcePath = "/data/secret.db", SourceSecret = "CHIFFRE", SourceLabel = "srv / a" };

        var view = JsonSerializer.Serialize(DatabaseView.From(entity));
        var raw = JsonSerializer.Serialize(entity);

        Assert.DoesNotContain("CHIFFRE", view);
        Assert.DoesNotContain("sourceSecret", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourcePath", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/data/secret.db", view);
        Assert.DoesNotContain("CHIFFRE", raw); // l'entité elle-même ne sérialise jamais le secret
    }

    [Fact]
    public void A_sync_summary_carries_counts_and_not_the_database_entity()
    {
        var sync = new SyncResult(new DatabaseInfo { SourcePath = "/x", SourceSecret = "s" }, 1, 2, 3, 4, 5, 6, 7, 8);

        var json = JsonSerializer.Serialize(SyncSummaryView.From(sync));

        Assert.Equal("""{"TablesAdded":1,"TablesRemoved":2,"ColumnsAdded":3,"ColumnsUpdated":4,"ColumnsRemoved":5,"RelationsCreated":6,"IndexesAdded":7,"IndexesRemoved":8}""", json);
    }
}

/// <summary>L'API ne se connecte à un serveur que si on l'y autorise ; les secrets restent dans le catalogue, chiffrés.</summary>
public sealed class ApiSqlServerGateTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private HttpClient Client(bool allowSqlServer)
    {
        var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, allowSqlServer ? "open" : "closed"),
            ["Catalog:AllowSqlServerSources"] = allowSqlServer ? "true" : "false"
        });
        var client = factory.CreateKestrelClient();
        _disposables.Add(client);
        _disposables.Add(factory);
        return client;
    }

    private string CatalogFile(bool open) => Path.Combine(_env.Directory, open ? "open" : "closed", "granitdb.db");

    private static object Body(Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["server"] = "127.0.0.1", ["port"] = 1, ["database"] = "x", ["authMode"] = "Sql", ["user"] = "sa",
            ["password"] = "x", ["encrypt"] = false, ["timeoutSeconds"] = 2
        };
        change?.Invoke(body);
        return body;
    }

    private void SetCatalogColumn(bool open, string id, string column, string? value)
    {
        using var connection = new SqliteConnection($"Data Source={CatalogFile(open)};Pooling=False");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = $"UPDATE Databases SET {column} = $value WHERE Id = $id";
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private async Task<string> CreateSqlServerDatabaseAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Distante", type = "SqlServer" });
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> TitleOf(HttpResponseMessage response)
    {
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }

    // --- Sources désactivées (par défaut) ---

    [Fact]
    public async Task The_capabilities_say_what_is_allowed()
    {
        var closed = await Client(false).GetFromJsonAsync<JsonElement>("/api/v1/Capabilities");
        var open = await Client(true).GetFromJsonAsync<JsonElement>("/api/v1/Capabilities");

        Assert.False(closed.GetProperty("allowSqlServerSources").GetBoolean());
        Assert.False(closed.GetProperty("allowLocalPaths").GetBoolean());
        Assert.True(open.GetProperty("allowSqlServerSources").GetBoolean());
    }

    [Fact]
    public async Task By_default_the_api_never_connects_to_a_server_chosen_by_the_caller()
    {
        var client = Client(false);
        var id = await CreateSqlServerDatabaseAsync(client);

        var test = await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body());
        var import = await client.PostAsJsonAsync("/api/v1/import/sqlserver", Body());
        var replace = await client.PutAsJsonAsync($"/api/v1/import/sqlserver/{id}", Body());
        var legacy = await client.PostAsJsonAsync("/import/sqlserver/test", Body());

        foreach (var response in new[] { test, import, replace, legacy })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("désactivées", await TitleOf(response));
        }

        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/DatabaseInfo")).EnumerateArray()
            .Where(d => d.GetProperty("canResync").GetBoolean()));
    }

    [Fact]
    public async Task A_database_with_a_stored_connection_cannot_be_reread_while_the_sources_are_disabled()
    {
        var client = Client(false);
        var id = await CreateSqlServerDatabaseAsync(client);
        SetCatalogColumn(false, id, "SourceSecret", "peu importe");

        var sync = await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null);
        var preview = await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync/preview", null);

        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Contains("désactivées", await TitleOf(sync));
    }

    // --- Demandes mal formées ---

    [Fact]
    public async Task A_malformed_request_is_refused_with_the_faulty_fields_before_any_connection()
    {
        var client = Client(true);

        var missing = await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body(b => { b["server"] = ""; b["database"] = ""; }));
        var noCredentials = await client.PostAsJsonAsync("/api/v1/import/sqlserver", Body(b => { b["user"] = null; b["password"] = null; }));
        var badPort = await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body(b => b["port"] = 70000));
        var badMode = await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body(b => b["authMode"] = "Kerberos"));

        foreach (var response in new[] { missing, noCredentials, badPort, badMode })
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = await missing.Content.ReadAsStringAsync();
        Assert.Contains("Server", fields);
        Assert.Contains("Database", fields);
        Assert.Contains("User", await noCredentials.Content.ReadAsStringAsync());
    }

    // --- Échecs de connexion ---

    [Fact]
    public async Task An_unreachable_server_is_a_test_result_with_the_instruction_to_follow_and_imports_nothing()
    {
        var client = Client(true);

        var test = await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body());
        var import = await client.PostAsJsonAsync("/api/v1/import/sqlserver", Body());

        Assert.Equal(HttpStatusCode.OK, test.StatusCode); // un échec de connexion est un résultat, pas une requête invalide
        var result = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("injoignable", result.GetProperty("message").GetString());
        Assert.Contains("pare-feu", result.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, import.StatusCode);
        Assert.Contains("injoignable", await TitleOf(import));
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/DatabaseInfo")).EnumerateArray());
    }

    [Fact]
    public async Task The_password_never_appears_in_an_error_response()
    {
        var client = Client(true);

        var test = await (await client.PostAsJsonAsync("/api/v1/import/sqlserver/test", Body(b => b["password"] = "MotDePasse-Unique-42"))).Content.ReadAsStringAsync();
        var import = await (await client.PostAsJsonAsync("/api/v1/import/sqlserver", Body(b => b["password"] = "MotDePasse-Unique-42"))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("MotDePasse-Unique-42", test);
        Assert.DoesNotContain("MotDePasse-Unique-42", import);
    }

    [Fact]
    public async Task Replacing_the_connection_of_an_unknown_database_is_404_and_of_a_sqlite_one_is_refused()
    {
        var client = Client(true);
        var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb())), "file", "s.db" } };
        var imported = await client.PostAsync("/api/v1/import/sqlite", form);
        var sqliteId = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("database").GetProperty("id").GetString();

        var unknown = await client.PutAsJsonAsync("/api/v1/import/sqlserver/nope", Body());
        var wrongType = await client.PutAsJsonAsync($"/api/v1/import/sqlserver/{sqliteId}", Body());

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Contains("pas SqlServer", await TitleOf(wrongType));
    }

    // --- Connexion enregistrée inutilisable ---

    [Fact]
    public async Task A_stored_connection_that_cannot_be_decrypted_asks_to_enter_it_again()
    {
        var client = Client(true);
        var id = await CreateSqlServerDatabaseAsync(client);
        SetCatalogColumn(true, id, "SourceSecret", "contenu qui n'a pas été chiffré par cette installation");

        var sync = await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null);
        var preview = await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync/preview", null);

        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.Contains("n'est plus lisible", await TitleOf(sync));
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
    }

    [Fact]
    public async Task A_database_read_at_startup_has_no_stored_connection_and_says_how_to_register_it()
    {
        var client = Client(true);
        var id = await CreateSqlServerDatabaseAsync(client);

        var sync = await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null);

        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.Contains("Aucune connexion n'est enregistrée", await TitleOf(sync));
        Assert.False((await client.GetFromJsonAsync<JsonElement>($"/api/v1/DatabaseInfo/{id}")).GetProperty("canResync").GetBoolean());
    }

    // --- Sorties : plus d'entité brute ---

    [Fact]
    public async Task No_response_returns_the_source_path_the_connection_or_the_database_entity()
    {
        var client = Client(false);
        var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb())), "file", "s.db" }, { new StringContent("Boutique"), "name" } };
        var imported = await client.PostAsync("/api/v1/import/sqlite", form);
        var importedText = await imported.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(importedText).RootElement.GetProperty("database").GetProperty("id").GetString()!;

        var texts = new List<string> { importedText };
        texts.Add(await client.GetStringAsync("/api/v1/DatabaseInfo"));
        texts.Add(await client.GetStringAsync("/api/v1/DatabaseInfo?skip=0&take=10"));
        texts.Add(await client.GetStringAsync($"/api/v1/DatabaseInfo/{id}"));
        texts.Add(await (await client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null)).Content.ReadAsStringAsync());
        texts.Add(await (await client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}", new { name = "B2", type = "Sqlite" })).Content.ReadAsStringAsync());
        texts.Add(await (await client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}/documentation", new { description = "d" })).Content.ReadAsStringAsync());
        texts.Add(await (await client.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Vide", type = "Sqlite" })).Content.ReadAsStringAsync());

        foreach (var text in texts)
        {
            Assert.DoesNotContain("sourcePath", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sourceSecret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("granitdb-data", text);
            Assert.DoesNotContain(".db\"", text.Replace("\"name\":\"Boutique\"", ""), StringComparison.Ordinal);
        }

        // La resynchronisation ne renvoie plus la base, seulement ses comptes.
        var sync = JsonDocument.Parse(texts[4]).RootElement;
        Assert.False(sync.TryGetProperty("database", out _));
        Assert.True(sync.TryGetProperty("tablesAdded", out _));
        Assert.True(JsonDocument.Parse(texts[3]).RootElement.GetProperty("canResync").GetBoolean());
    }

    // --- Auteur de l'historique ---

    [Fact]
    public async Task An_identified_caller_is_recorded_as_the_author_of_a_change_and_an_anonymous_one_is_not()
    {
        var client = Client(false);
        var created = await client.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Boutique", type = "Sqlite" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/DatabaseInfo/{id}/documentation")
               { Content = JsonContent.Create(new { description = "par Alice" }) })
        {
            request.Headers.Add("X-Test-User", "alice@exemple.fr");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        await client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}/documentation", new { description = "anonyme" });
        using (var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/DatabaseInfo/{id}")
               { Content = JsonContent.Create(new { name = "Boutique", type = "Sqlite", description = "par Bob" }) })
        {
            request.Headers.Add("X-Test-User", "bob");
            await client.SendAsync(request);
        }

        var history = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/History?entityId={id}")).EnumerateArray()
            .ToDictionary(h => h.GetProperty("newValue").GetString()!, h => h.GetProperty("changedBy"));
        Assert.Equal("alice@exemple.fr", history["par Alice"].GetString());
        Assert.Equal(JsonValueKind.Null, history["anonyme"].ValueKind);
        Assert.Equal("bob", history["par Bob"].GetString());
    }

    [Fact]
    public async Task Sensitivity_changes_record_their_author_too()
    {
        var client = Client(false);
        var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb())), "file", "s.db" } };
        var imported = await client.PostAsync("/api/v1/import/sqlite", form);
        var db = (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("database").GetProperty("id").GetString();
        var columns = await client.GetFromJsonAsync<JsonElement>($"/api/v1/ColumnInfo?databaseId={db}&name=email");
        var column = columns[0].GetProperty("id").GetString();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/ColumnInfo/{column}/sensitivity")
            { Content = JsonContent.Create(new { isSensitive = true }) };
        request.Headers.Add("X-Test-User", "carole");
        await client.SendAsync(request);

        var change = Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/v1/History?entityId={column}")).EnumerateArray());
        Assert.Equal("carole", change.GetProperty("changedBy").GetString());
        Assert.Equal("IsSensitive", change.GetProperty("field").GetString());
    }
}

/// <summary>L'auteur d'une modification, au niveau du dépôt.</summary>
public class HistoryAuthorTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task The_author_is_stored_trimmed_and_capped_and_blank_means_unknown()
    {
        var database = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "A", Type = "Sqlite" });

        await _env.Repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "1", default, "  alice  ");
        await _env.Repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "2", default, "   ");
        await _env.Repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "3", default, new string('x', 400));
        await _env.Repository.ModifyAsync<DatabaseInfo>(database.Id, d => d.Description = "4");

        var byValue = (await _env.Repository.GetHistoryAsync(database.Id, 0, 10)).ToDictionary(h => h.NewValue!, h => h.ChangedBy);
        Assert.Equal("alice", byValue["1"]);
        Assert.Null(byValue["2"]);
        Assert.Equal(255, byValue["3"]!.Length);
        Assert.Null(byValue["4"]);
    }
}
