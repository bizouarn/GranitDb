using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Jalon 5 côté API : documentation, colonnes sensibles, relations manuelles, domaines, étiquettes, complétude, aperçu de synchronisation.</summary>
public sealed class ApiGovernanceTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiGovernanceTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
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

    private const string V1 = "/api/v1";

    private async Task<string> ImportAsync(string name = "Boutique")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb($"{Guid.NewGuid():N}.db"))), "file", "sample.db");
        form.Add(new StringContent(name), "name");
        var response = await _client.PostAsync($"{V1}/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        return await _client.GetFromJsonAsync<JsonElement>(url);
    }

    private async Task<string> TableIdAsync(string databaseId, string name)
    {
        var tables = await GetJsonAsync($"{V1}/TableInfo?databaseId={databaseId}&name={name}");
        return tables.EnumerateArray().First(t => t.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    private async Task<string> ColumnIdAsync(string tableId, string name)
    {
        var columns = await GetJsonAsync($"{V1}/TableInfo/{tableId}/columns");
        return columns.EnumerateArray().First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> PutAsync(string url, object body)
    {
        return await _client.PutAsJsonAsync(url, body);
    }

    private async Task<JsonElement> HistoryAsync(string entityId)
    {
        return await GetJsonAsync($"{V1}/History?entityId={entityId}");
    }

    private string SourceFileOf(string databaseId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_env.Directory, "api-data", "granitdb.db")};Mode=ReadOnly");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SourcePath FROM Databases WHERE Id = $id";
        command.Parameters.AddWithValue("$id", databaseId);
        return (string)command.ExecuteScalar()!;
    }

    private void RunOnSource(string databaseId, string sql)
    {
        var path = SourceFileOf(databaseId);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    // --- Documentation ---

    [Fact]
    public async Task Documentation_is_edited_without_touching_the_other_fields_and_is_historised()
    {
        var db = await ImportAsync();
        var orders = await TableIdAsync(db, "orders");

        var put = await PutAsync($"{V1}/TableInfo/{orders}/documentation", new { description = "  Les commandes  ", metaInfo = "owner: ventes" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var table = await GetJsonAsync($"{V1}/TableInfo/{orders}");
        Assert.Equal("Les commandes", table.GetProperty("description").GetString());
        Assert.Equal("owner: ventes", table.GetProperty("metaInfo").GetString());
        Assert.Equal("orders", table.GetProperty("name").GetString());
        Assert.Equal(3, table.GetProperty("columns").GetArrayLength());
        var history = await HistoryAsync(orders);
        Assert.Equal(["Description", "MetaInfo"], history.EnumerateArray().Select(h => h.GetProperty("field").GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task A_blank_description_clears_it_and_too_long_or_unknown_is_refused()
    {
        var db = await ImportAsync();
        var orders = await TableIdAsync(db, "orders");
        await PutAsync($"{V1}/TableInfo/{orders}/documentation", new { description = "x" });

        await PutAsync($"{V1}/TableInfo/{orders}/documentation", new { description = "   " });

        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync($"{V1}/TableInfo/{orders}")).GetProperty("description").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PutAsync($"{V1}/TableInfo/{orders}/documentation", new { description = new string('a', 4001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync($"{V1}/TableInfo/nope/documentation", new { description = "x" })).StatusCode);
    }

    [Fact]
    public async Task Database_and_column_documentation_work_too_and_survive_a_synchronization()
    {
        var db = await ImportAsync();
        var email = await ColumnIdAsync(await TableIdAsync(db, "customers"), "email");
        await PutAsync($"{V1}/DatabaseInfo/{db}/documentation", new { description = "La boutique" });
        await PutAsync($"{V1}/ColumnInfo/{email}/documentation", new { description = "Adresse de contact" });

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null)).StatusCode);

        Assert.Equal("La boutique", (await GetJsonAsync($"{V1}/DatabaseInfo/{db}")).GetProperty("description").GetString());
        Assert.Equal("Adresse de contact", (await GetJsonAsync($"{V1}/ColumnInfo/{email}")).GetProperty("description").GetString());
    }

    // --- Colonnes sensibles ---

    [Fact]
    public async Task A_column_can_be_marked_sensitive_filtered_historised_and_kept_by_the_synchronization()
    {
        var db = await ImportAsync();
        var email = await ColumnIdAsync(await TableIdAsync(db, "customers"), "email");

        var put = await PutAsync($"{V1}/ColumnInfo/{email}/sensitivity", new { isSensitive = true });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isSensitive").GetBoolean());
        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);
        var sensitive = await GetJsonAsync($"{V1}/ColumnInfo?databaseId={db}&isSensitive=true");
        Assert.Equal("email", Assert.Single(sensitive.EnumerateArray()).GetProperty("name").GetString());
        var change = Assert.Single((await HistoryAsync(email)).EnumerateArray());
        Assert.Equal("IsSensitive", change.GetProperty("field").GetString());
        Assert.Equal("false", change.GetProperty("oldValue").GetString());
        Assert.Equal("true", change.GetProperty("newValue").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync($"{V1}/ColumnInfo/nope/sensitivity", new { isSensitive = true })).StatusCode);
    }

    [Fact]
    public async Task A_column_put_does_not_reset_the_sensitive_mark()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        var email = await ColumnIdAsync(customers, "email");
        await PutAsync($"{V1}/ColumnInfo/{email}/sensitivity", new { isSensitive = true });

        var put = await PutAsync($"{V1}/ColumnInfo/{email}",
            new { databaseId = db, tableId = customers, name = "email", dataType = "TEXT", description = "x" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await GetJsonAsync($"{V1}/ColumnInfo/{email}")).GetProperty("isSensitive").GetBoolean());
    }

    // --- Relations manuelles ---

    [Fact]
    public async Task A_manual_relation_is_created_validated_and_survives_the_synchronization()
    {
        var db = await ImportAsync();
        var tags = await TableIdAsync(db, "tags");
        var customers = await TableIdAsync(db, "customers");
        var tagId = await ColumnIdAsync(tags, "id");
        var customerId = await ColumnIdAsync(customers, "id");

        var created = await _client.PostAsJsonAsync($"{V1}/RelationInfo",
            new { firstId = customerId, secondId = tagId, relationType = "Many-to-Many", description = "Étiquettes de clients" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);
        var relations = await GetJsonAsync($"{V1}/RelationInfo?firstId={customerId}");
        var manual = Assert.Single(relations.EnumerateArray(), r => r.GetProperty("secondId").GetString() == tagId);
        Assert.Equal("Many-to-Many", manual.GetProperty("relationType").GetString());
        Assert.Equal(JsonValueKind.Null, manual.GetProperty("metaInfo").ValueKind);
    }

    [Fact]
    public async Task Relation_rules_unknown_column_other_database_duplicate_and_same_column()
    {
        var db = await ImportAsync();
        var other = await ImportAsync("Autre");
        var customerId = await ColumnIdAsync(await TableIdAsync(db, "customers"), "id");
        var orderCustomer = await ColumnIdAsync(await TableIdAsync(db, "orders"), "customer_id");
        var otherId = await ColumnIdAsync(await TableIdAsync(other, "customers"), "id");

        async Task<HttpStatusCode> Post(object body) => (await _client.PostAsJsonAsync($"{V1}/RelationInfo", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { firstId = customerId, secondId = "inconnue", relationType = "One-to-One" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { firstId = customerId, secondId = otherId, relationType = "One-to-One" }));
        // La clé étrangère détectée existe déjà : ni la même relation ni son inverse.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { firstId = customerId, secondId = orderCustomer, relationType = "One-to-Many" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { firstId = orderCustomer, secondId = customerId, relationType = "One-to-Many" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { firstId = customerId, secondId = customerId, relationType = "One-to-One" }));
    }

    [Fact]
    public async Task The_auto_marker_cannot_be_set_or_cleared_by_a_caller()
    {
        var db = await ImportAsync();
        var customerId = await ColumnIdAsync(await TableIdAsync(db, "customers"), "id");
        var tagId = await ColumnIdAsync(await TableIdAsync(db, "tags"), "id");
        var orderCustomer = await ColumnIdAsync(await TableIdAsync(db, "orders"), "customer_id");
        var auto = (await GetJsonAsync($"{V1}/RelationInfo?secondId={orderCustomer}")).EnumerateArray().Single();
        var autoId = auto.GetProperty("id").GetString()!;
        Assert.Equal("auto:fk", auto.GetProperty("metaInfo").GetString());

        var forged = await _client.PostAsJsonAsync($"{V1}/RelationInfo",
            new { firstId = customerId, secondId = tagId, relationType = "One-to-One", metaInfo = "auto:fk" });
        var cleared = await PutAsync($"{V1}/RelationInfo/{autoId}", new { firstId = customerId, secondId = orderCustomer, relationType = "One-to-One" });
        var viaDoc = await PutAsync($"{V1}/RelationInfo/{autoId}/documentation", new { description = "Un client a plusieurs commandes", metaInfo = "" });
        var forgedDoc = await PutAsync($"{V1}/RelationInfo/{(await _client.PostAsJsonAsync($"{V1}/RelationInfo", new { firstId = customerId, secondId = tagId, relationType = "One-to-One" })).Content.ReadFromJsonAsync<JsonElement>().Result.GetProperty("id").GetString()}/documentation",
            new { metaInfo = "auto:fk" });

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaDoc.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forgedDoc.StatusCode);
        var after = await GetJsonAsync($"{V1}/RelationInfo/{autoId}");
        Assert.Equal("auto:fk", after.GetProperty("metaInfo").GetString());
        Assert.Equal("One-to-One", after.GetProperty("relationType").GetString());
    }

    [Fact]
    public async Task A_detected_relation_keeps_its_corrected_type_and_description_across_synchronizations()
    {
        var db = await ImportAsync();
        var customerId = await ColumnIdAsync(await TableIdAsync(db, "customers"), "id");
        var orderCustomer = await ColumnIdAsync(await TableIdAsync(db, "orders"), "customer_id");
        var auto = (await GetJsonAsync($"{V1}/RelationInfo?secondId={orderCustomer}")).EnumerateArray().Single();
        var autoId = auto.GetProperty("id").GetString()!;
        await PutAsync($"{V1}/RelationInfo/{autoId}", new { firstId = customerId, secondId = orderCustomer, relationType = "One-to-One", description = "Un seul client" });

        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);
        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);

        var after = (await GetJsonAsync($"{V1}/RelationInfo?secondId={orderCustomer}")).EnumerateArray().Single();
        Assert.Equal(autoId, after.GetProperty("id").GetString());
        Assert.Equal("One-to-One", after.GetProperty("relationType").GetString());
        Assert.Equal("Un seul client", after.GetProperty("description").GetString());
        Assert.Equal("auto:fk", after.GetProperty("metaInfo").GetString());
    }

    // --- Domaines ---

    private async Task<string> CreateDomainAsync(string db, string name, string? color = null)
    {
        var response = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId = db, name, color });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> DomainsAsync(string db)
    {
        return await GetJsonAsync($"{V1}/DomainInfo/database/{db}");
    }

    [Fact]
    public async Task Domains_are_created_renamed_validated_and_unique_per_database_ignoring_case()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes", "#1976d2");

        var duplicate = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId = db, name = "ventes" });
        var badColor = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId = db, name = "X", color = "rouge" });
        var noBase = await _client.PostAsJsonAsync($"{V1}/DomainInfo", new { databaseId = "nope", name = "X" });
        var rename = await PutAsync($"{V1}/DomainInfo/{sales}", new { databaseId = db, name = "Commerce", color = "#00ff00" });
        var move = await PutAsync($"{V1}/DomainInfo/{sales}", new { databaseId = await ImportAsync("Autre"), name = "Commerce" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badColor.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, noBase.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        var domain = Assert.Single((await DomainsAsync(db)).EnumerateArray());
        Assert.Equal("Commerce", domain.GetProperty("name").GetString());
        Assert.Equal("#00ff00", domain.GetProperty("color").GetString());
    }

    [Fact]
    public async Task Tables_join_domains_a_table_may_be_in_several_and_the_places_are_remembered()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var billing = await CreateDomainAsync(db, "Facturation");
        var orders = await TableIdAsync(db, "orders");
        var customers = await TableIdAsync(db, "customers");

        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { x = 120.5, y = 40 });
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{customers}", new { });
        await PutAsync($"{V1}/DomainInfo/{billing}/tables/{orders}", new { });
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { }); // sans effet : la place est gardée

        var domains = (await DomainsAsync(db)).EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
        var inSales = domains["Ventes"].GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("tableId").GetString()!);
        Assert.Equal(2, inSales.Count);
        Assert.Equal(120.5, inSales[orders].GetProperty("x").GetDouble());
        Assert.Equal(JsonValueKind.Null, inSales[customers].GetProperty("x").ValueKind);
        Assert.Single(domains["Facturation"].GetProperty("tables").EnumerateArray());
        Assert.Equal(["Facturation", "Ventes"], domains.Keys.Order().ToArray());
    }

    [Fact]
    public async Task Moving_a_table_to_another_domain_removes_it_from_the_first_atomically()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var billing = await CreateDomainAsync(db, "Facturation");
        var orders = await TableIdAsync(db, "orders");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { });

        var move = await PutAsync($"{V1}/DomainInfo/{billing}/tables/{orders}?moveFrom={sales}", new { x = 10, y = 20 });

        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        var domains = (await DomainsAsync(db)).EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
        Assert.Empty(domains["Ventes"].GetProperty("tables").EnumerateArray());
        Assert.Equal(orders, Assert.Single(domains["Facturation"].GetProperty("tables").EnumerateArray()).GetProperty("tableId").GetString());
    }

    [Fact]
    public async Task A_table_of_another_database_or_an_unknown_one_cannot_join_a_domain()
    {
        var db = await ImportAsync();
        var other = await ImportAsync("Autre");
        var sales = await CreateDomainAsync(db, "Ventes");
        var foreign = await TableIdAsync(other, "orders");

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync($"{V1}/DomainInfo/{sales}/tables/{foreign}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync($"{V1}/DomainInfo/{sales}/tables/nope", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync($"{V1}/DomainInfo/nope/tables/{foreign}", new { })).StatusCode);
        Assert.Empty((await DomainsAsync(db)).EnumerateArray().Single().GetProperty("tables").EnumerateArray());
    }

    [Fact]
    public async Task A_table_leaves_a_domain_without_being_deleted()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var orders = await TableIdAsync(db, "orders");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DomainInfo/{sales}/tables/{orders}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"{V1}/DomainInfo/{sales}/tables/{orders}")).StatusCode);

        Assert.Empty((await DomainsAsync(db)).EnumerateArray().Single().GetProperty("tables").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{V1}/TableInfo/{orders}")).StatusCode);
    }

    [Fact]
    public async Task The_layout_of_a_domain_is_saved_all_or_nothing()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var orders = await TableIdAsync(db, "orders");
        var customers = await TableIdAsync(db, "customers");
        var tags = await TableIdAsync(db, "tags");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { });
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{customers}", new { });

        var ok = await PutAsync($"{V1}/DomainInfo/{sales}/layout",
            new[] { new { tableId = orders, x = 300.0, y = 50.0 }, new { tableId = customers, x = 24.0, y = 24.0 } });
        var partial = await PutAsync($"{V1}/DomainInfo/{sales}/layout",
            new[] { new { tableId = orders, x = 999.0, y = 999.0 }, new { tableId = tags, x = 1.0, y = 1.0 } });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        var places = (await DomainsAsync(db)).EnumerateArray().Single().GetProperty("tables").EnumerateArray()
            .ToDictionary(t => t.GetProperty("tableId").GetString()!, t => t.GetProperty("x").GetDouble());
        Assert.Equal(300.0, places[orders]); // la sauvegarde partielle n'a rien changé
        Assert.Equal(24.0, places[customers]);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync($"{V1}/DomainInfo/nope/layout", Array.Empty<object>())).StatusCode);
    }

    [Fact]
    public async Task Tables_can_be_filtered_by_domain()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        await CreateDomainAsync(db, "Vide");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{await TableIdAsync(db, "orders")}", new { });
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{await TableIdAsync(db, "customers")}", new { });
        var empty = (await DomainsAsync(db)).EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Vide").GetProperty("id").GetString();

        var inSales = await GetJsonAsync($"{V1}/TableInfo?domainId={sales}");
        var inSalesByDatabase = await GetJsonAsync($"{V1}/TableInfo/database/{db}?domainId={sales}");
        var inEmpty = await GetJsonAsync($"{V1}/TableInfo?domainId={empty}");

        Assert.Equal(["customers", "orders"], inSales.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order().ToArray());
        Assert.Equal(2, inSalesByDatabase.GetArrayLength());
        Assert.Empty(inEmpty.EnumerateArray());
    }

    [Fact]
    public async Task Deleting_a_domain_a_table_or_a_database_removes_the_links_but_not_the_tables()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var orders = await TableIdAsync(db, "orders");
        var customers = await TableIdAsync(db, "customers");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{orders}", new { });
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{customers}", new { });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/TableInfo/{customers}")).StatusCode);
        Assert.Equal(orders, Assert.Single((await DomainsAsync(db)).EnumerateArray().Single().GetProperty("tables").EnumerateArray()).GetProperty("tableId").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DomainInfo/{sales}")).StatusCode);
        Assert.Empty((await DomainsAsync(db)).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{V1}/TableInfo/{orders}")).StatusCode);

        var again = await CreateDomainAsync(db, "Ventes");
        await PutAsync($"{V1}/DomainInfo/{again}/tables/{orders}", new { });
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DatabaseInfo/{db}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/DomainInfo/database/{db}")).StatusCode);
        Assert.Empty((await GetJsonAsync($"{V1}/DomainInfo")).EnumerateArray());
    }

    [Fact]
    public async Task A_table_removed_by_a_synchronization_leaves_its_domains()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{await TableIdAsync(db, "tags")}", new { });
        RunOnSource(db, "DROP TABLE tags;");

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null)).StatusCode);

        Assert.Empty((await DomainsAsync(db)).EnumerateArray().Single().GetProperty("tables").EnumerateArray());
    }

    // --- Étiquettes ---

    private async Task<HttpResponseMessage> AttachAsync(string name, string type, string id)
    {
        return await _client.PostAsJsonAsync($"{V1}/TagInfo/attach", new { name, entityType = type, entityId = id });
    }

    [Fact]
    public async Task Attaching_creates_the_tag_once_ignoring_case_and_is_idempotent()
    {
        var db = await ImportAsync();
        var orders = await TableIdAsync(db, "orders");
        var customers = await TableIdAsync(db, "customers");

        var first = await AttachAsync("RGPD", "table", customers);
        await AttachAsync("rgpd", "table", orders);
        await AttachAsync("RGPD", "table", customers);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tags = await GetJsonAsync($"{V1}/TagInfo");
        Assert.Equal("RGPD", Assert.Single(tags.EnumerateArray()).GetProperty("name").GetString());
        var of = await GetJsonAsync($"{V1}/TagInfo/of/table/{customers}");
        Assert.Equal("RGPD", Assert.Single(of.EnumerateArray()).GetProperty("tagName").GetString());
        Assert.Equal(2, (await GetJsonAsync($"{V1}/TagInfo/database/{db}/links")).GetArrayLength());
    }

    [Fact]
    public async Task Attach_validates_the_type_the_name_and_the_target()
    {
        var db = await ImportAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await AttachAsync("x", "relation", db)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AttachAsync("", "database", db)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AttachAsync(new string('a', 51), "database", db)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AttachAsync("x", "table", "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AttachAsync("x", "table", db)).StatusCode); // une base n'est pas une table
        Assert.Empty((await GetJsonAsync($"{V1}/TagInfo")).EnumerateArray()); // rien n'a été créé en route
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/TagInfo/database/nope/links")).StatusCode);
    }

    [Fact]
    public async Task Tables_and_columns_can_be_filtered_by_tag_and_an_unknown_tag_matches_nothing()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        var email = await ColumnIdAsync(customers, "email");
        await AttachAsync("RGPD", "table", customers);
        await AttachAsync("rgpd", "column", email);
        await AttachAsync("RGPD", "database", db);

        var tables = await GetJsonAsync($"{V1}/TableInfo?tag=rgpd");
        var columns = await GetJsonAsync($"{V1}/ColumnInfo?tag=RGPD");

        Assert.Equal("customers", Assert.Single(tables.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("email", Assert.Single(columns.EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty((await GetJsonAsync($"{V1}/TableInfo?tag=inconnue")).EnumerateArray());
    }

    [Fact]
    public async Task Detaching_keeps_the_tag_and_deleting_a_tag_removes_it_everywhere()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        var tagId = (await (await AttachAsync("RGPD", "table", customers)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await AttachAsync("RGPD", "database", db);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/TagInfo/{tagId}/links/table/{customers}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"{V1}/TagInfo/{tagId}/links/table/{customers}")).StatusCode);
        Assert.Single((await GetJsonAsync($"{V1}/TagInfo")).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/TagInfo/{tagId}")).StatusCode);
        Assert.Empty((await GetJsonAsync($"{V1}/TagInfo/database/{db}/links")).EnumerateArray());
    }

    [Fact]
    public async Task Deleting_a_table_or_a_database_removes_its_tag_links_and_a_synchronization_too()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        var tagsTable = await TableIdAsync(db, "tags");
        await AttachAsync("A", "column", await ColumnIdAsync(customers, "email"));
        await AttachAsync("B", "table", tagsTable);
        await AttachAsync("C", "database", db);

        RunOnSource(db, "DROP TABLE tags;");
        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);
        Assert.Equal(["A", "C"], (await GetJsonAsync($"{V1}/TagInfo/database/{db}/links")).EnumerateArray().Select(l => l.GetProperty("tagName").GetString()!).Order().ToArray());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/TableInfo/{customers}")).StatusCode);
        Assert.Equal(["C"], (await GetJsonAsync($"{V1}/TagInfo/database/{db}/links")).EnumerateArray().Select(l => l.GetProperty("tagName").GetString()!).ToArray());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DatabaseInfo/{db}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/TagInfo/{(await GetJsonAsync($"{V1}/TagInfo")).EnumerateArray().First().GetProperty("id").GetString()}")).StatusCode);
    }

    // --- Complétude ---

    [Fact]
    public async Task Completeness_counts_described_tables_and_columns_and_flags_undescribed_sensitive_columns()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers"); // 4 colonnes
        var email = await ColumnIdAsync(customers, "email");
        var nickname = await ColumnIdAsync(customers, "nickname");
        await PutAsync($"{V1}/TableInfo/{customers}/documentation", new { description = "Clients" });
        await PutAsync($"{V1}/ColumnInfo/{email}/documentation", new { description = "Adresse" });
        await PutAsync($"{V1}/ColumnInfo/{nickname}/documentation", new { description = "   " }); // espaces : ne compte pas
        await PutAsync($"{V1}/ColumnInfo/{email}/sensitivity", new { isSensitive = true });
        await PutAsync($"{V1}/ColumnInfo/{nickname}/sensitivity", new { isSensitive = true });

        var report = await GetJsonAsync($"{V1}/Completeness/database/{db}");

        var row = report.GetProperty("perTable").EnumerateArray().Single(t => t.GetProperty("tableName").GetString() == "customers");
        Assert.Equal(4, row.GetProperty("columns").GetInt32());
        Assert.Equal(1, row.GetProperty("columnsDescribed").GetInt32());
        Assert.True(row.GetProperty("tableDescribed").GetBoolean());
        Assert.Equal(2, row.GetProperty("sensitiveColumns").GetInt32());
        Assert.Equal(1, row.GetProperty("sensitiveUndescribed").GetInt32());
        Assert.Equal(40, row.GetProperty("percent").GetInt32()); // 2 / 5
        Assert.Equal(5, report.GetProperty("tables").GetInt32());
        Assert.Equal(1, report.GetProperty("tablesDescribed").GetInt32());
        Assert.Equal(1, report.GetProperty("columnsDescribed").GetInt32());
        Assert.Equal(1, report.GetProperty("sensitiveUndescribed").GetInt32());
        // 2 éléments décrits sur 5 tables + 12 colonnes (customers 4, orders 3, order_lines 4, tags 2, customer_emails 2 = 15) = 20
        Assert.Equal(15, report.GetProperty("columns").GetInt32());
        Assert.Equal(10, report.GetProperty("percent").GetInt32());
    }

    [Fact]
    public async Task Completeness_can_be_limited_to_a_domain_and_an_unknown_database_is_404()
    {
        var db = await ImportAsync();
        var sales = await CreateDomainAsync(db, "Ventes");
        var tags = await TableIdAsync(db, "tags");
        await PutAsync($"{V1}/DomainInfo/{sales}/tables/{tags}", new { });
        await PutAsync($"{V1}/TableInfo/{tags}/documentation", new { description = "Étiquettes" });
        await PutAsync($"{V1}/ColumnInfo/{await ColumnIdAsync(tags, "id")}/documentation", new { description = "Clé" });
        await PutAsync($"{V1}/ColumnInfo/{await ColumnIdAsync(tags, "label")}/documentation", new { description = "Libellé" });

        var report = await GetJsonAsync($"{V1}/Completeness/database/{db}?domainId={sales}");

        Assert.Equal(1, report.GetProperty("tables").GetInt32());
        Assert.Equal(100, report.GetProperty("percent").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/Completeness/database/nope")).StatusCode);
        var emptyDomain = await CreateDomainAsync(db, "Vide");
        Assert.Equal(0, (await GetJsonAsync($"{V1}/Completeness/database/{db}?domainId={emptyDomain}")).GetProperty("percent").GetInt32());
    }

    // --- Aperçu de synchronisation ---

    private async Task<JsonElement> PreviewAsync(string db)
    {
        var response = await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync/preview", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(string Kind, string Action, string Table, string? Name, bool Loses)> Changes(JsonElement preview)
    {
        return preview.GetProperty("changes").EnumerateArray().Select(c => (
            c.GetProperty("kind").GetString()!, c.GetProperty("action").GetString()!, c.GetProperty("table").GetString()!,
            c.GetProperty("name").GetString(), c.GetProperty("losesDocumentation").GetBoolean())).ToList();
    }

    [Fact]
    public async Task A_source_that_did_not_change_has_an_empty_preview()
    {
        var db = await ImportAsync();

        var preview = await PreviewAsync(db);

        Assert.False(preview.GetProperty("hasChanges").GetBoolean());
        Assert.Empty(preview.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task The_preview_lists_additions_changes_and_removals_without_modifying_the_catalog()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        await PutAsync($"{V1}/ColumnInfo/{await ColumnIdAsync(customers, "nickname")}/documentation", new { description = "Pseudo" });
        await PutAsync($"{V1}/TableInfo/{await TableIdAsync(db, "tags")}/documentation", new { description = "Étiquettes" });
        RunOnSource(db, """
            ALTER TABLE customers ADD COLUMN phone TEXT;
            ALTER TABLE customers DROP COLUMN nickname;
            DROP VIEW customer_emails;
            DROP TABLE tags;
            CREATE TABLE invoices (id INTEGER PRIMARY KEY, order_id INTEGER REFERENCES orders(id));
            CREATE INDEX ix_orders_note ON orders(note);
            """);
        var before = (await GetJsonAsync($"{V1}/ColumnInfo?databaseId={db}")).GetArrayLength();

        var preview = await PreviewAsync(db);

        var changes = Changes(preview);
        Assert.Contains(("table", "added", "invoices", null, false), changes);
        Assert.Contains(("column", "added", "invoices", "order_id", false), changes);
        Assert.Contains(("column", "added", "customers", "phone", false), changes);
        Assert.Contains(("column", "removed", "customers", "nickname", true), changes); // décrite : perte de documentation
        Assert.Contains(("table", "removed", "tags", null, true), changes);
        Assert.Contains(("table", "removed", "customer_emails", null, false), changes); // vue sans documentation
        Assert.Contains(("index", "added", "orders", "ix_orders_note", false), changes);
        Assert.Contains(changes, c => c.Kind == "relation" && c.Action == "added" && c.Table == "invoices");
        Assert.Equal(2, preview.GetProperty("losesDocumentation").GetInt32());
        // Rien n'a été appliqué.
        Assert.Equal(before, (await GetJsonAsync($"{V1}/ColumnInfo?databaseId={db}")).GetArrayLength());
        Assert.Equal(5, (await GetJsonAsync($"{V1}/TableInfo?databaseId={db}")).GetArrayLength());
        // Appliquer fait exactement ce qui était annoncé.
        await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null);
        Assert.Empty(Changes(await PreviewAsync(db)));
    }

    [Fact]
    public async Task The_preview_details_column_changes_and_warns_about_tagged_sensitive_and_manual_relation_losses()
    {
        var db = await ImportAsync();
        var customers = await TableIdAsync(db, "customers");
        var email = await ColumnIdAsync(customers, "email");
        await PutAsync($"{V1}/ColumnInfo/{email}/sensitivity", new { isSensitive = true });
        await AttachAsync("RGPD", "column", await ColumnIdAsync(customers, "created"));
        var tagColumn = await ColumnIdAsync(await TableIdAsync(db, "tags"), "label");
        var customerId = await ColumnIdAsync(customers, "id");
        await _client.PostAsJsonAsync($"{V1}/RelationInfo", new { firstId = customerId, secondId = tagColumn, relationType = "One-to-Many" });
        RunOnSource(db, """
            ALTER TABLE customers DROP COLUMN created;
            ALTER TABLE tags DROP COLUMN label;
            ALTER TABLE orders ADD COLUMN extra TEXT;
            """);

        var changes = Changes(await PreviewAsync(db));

        Assert.Contains(("column", "removed", "customers", "created", true), changes); // étiquette perdue
        Assert.Contains(("column", "removed", "tags", "label", false), changes);
        Assert.Contains(changes, c => c.Kind == "relation" && c.Action == "removed" && c.Loses); // relation manuelle perdue
        Assert.DoesNotContain(changes, c => c.Name == "email");
    }

    [Fact]
    public async Task The_preview_reports_a_changed_type_and_a_vanished_foreign_key()
    {
        var db = await ImportAsync();
        RunOnSource(db, """
            ALTER TABLE tags RENAME COLUMN label TO title;
            CREATE TABLE orders2 (id INTEGER PRIMARY KEY, customer_id INTEGER, note TEXT);
            INSERT INTO orders2 SELECT * FROM orders;
            DROP TABLE order_lines;
            DROP TABLE orders;
            ALTER TABLE orders2 RENAME TO orders;
            """);

        var preview = await PreviewAsync(db);

        var changes = Changes(preview);
        Assert.Contains(("column", "added", "tags", "title", false), changes);
        Assert.Contains(("column", "removed", "tags", "label", false), changes);
        Assert.Contains(changes, c => c.Kind == "relation" && c.Action == "removed" && c.Name!.StartsWith("orders.customer_id"));
        Assert.Contains(("table", "removed", "order_lines", null, false), changes);
        var detail = preview.GetProperty("changes").EnumerateArray().FirstOrDefault(c => c.GetProperty("kind").GetString() == "column" && c.GetProperty("action").GetString() == "changed");
        Assert.True(detail.ValueKind == JsonValueKind.Undefined || detail.GetProperty("detail").GetString()!.Length > 0);
    }

    [Fact]
    public async Task The_preview_of_an_unknown_database_is_404_and_without_source_it_is_refused()
    {
        var created = await _client.PostAsJsonAsync($"{V1}/DatabaseInfo", new { name = "Manuelle", type = "Sqlite" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"{V1}/DatabaseInfo/nope/sync/preview", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"{V1}/DatabaseInfo/{id}/sync/preview", null)).StatusCode);
    }
}
