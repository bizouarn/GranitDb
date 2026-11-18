using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Jalon 3 : entrées validées, filtres, sous-ressources, historique, analyse d'impact, version /api/v1.</summary>
public sealed class ApiCatalogTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiCatalogTests()
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

    private async Task<string> ImportAsync()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb())), "file", "sample.db");
        form.Add(new StringContent("Boutique"), "name");
        var response = await _client.PostAsync("/api/v1/import/sqlite", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("database").GetProperty("id").GetString()!;
    }

    /// <summary>Le chemin du fichier source, lu dans le catalogue : l'API ne le renvoie jamais.</summary>
    private string? SourcePathOf(string databaseId)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_env.Directory, "api-data", "granitdb.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SourcePath FROM Databases WHERE Id = $id";
        command.Parameters.AddWithValue("$id", databaseId);
        return command.ExecuteScalar() as string;
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        return await _client.GetFromJsonAsync<JsonElement>(url);
    }

    private async Task<string> TableIdAsync(string databaseId, string name)
    {
        var tables = await GetJsonAsync($"/api/v1/TableInfo/database/{databaseId}");
        return tables.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name)
            .GetProperty("id").GetString()!;
    }

    // --- /api/v1 ---

    [Fact]
    public async Task Routes_are_available_under_api_v1_and_still_under_the_legacy_path()
    {
        var id = await ImportAsync();

        foreach (var prefix in new[] { "/api/v1", "" })
        {
            Assert.Single((await GetJsonAsync($"{prefix}/DatabaseInfo")).EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{prefix}/DatabaseInfo/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{prefix}/Search?q=customers")).StatusCode);
        }
    }

    // --- Validation des entrées ---

    [Fact]
    public async Task Invalid_input_is_refused_with_400_and_the_faulty_fields()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/DatabaseInfo",
            new { name = "", type = "Sqlite", projectUrl = "pas une url" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Name", body);
        Assert.Contains("ProjectUrl", body);
        Assert.Empty((await GetJsonAsync("/api/v1/DatabaseInfo")).EnumerateArray());
    }

    [Fact]
    public async Task Table_and_relation_values_are_checked()
    {
        var id = await ImportAsync();

        var badType = await _client.PostAsJsonAsync("/api/v1/TableInfo",
            new { name = "t", databaseId = id, tableType = "Bizarre" });
        var badRelation = await _client.PostAsJsonAsync("/api/v1/RelationInfo",
            new { firstId = "a", secondId = "b", relationType = "Plusieurs" });

        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRelation.StatusCode);
    }

    [Fact]
    public async Task Create_then_update_a_database_without_touching_the_server_managed_source_path()
    {
        var id = await ImportAsync();
        var before = await GetJsonAsync($"/api/v1/DatabaseInfo/{id}");
        Assert.True(before.GetProperty("canResync").GetBoolean());
        var sourcePath = SourcePathOf(id);
        Assert.False(string.IsNullOrEmpty(sourcePath));

        // Un appelant ne peut pas rediriger la resynchronisation vers un autre fichier.
        var put = await _client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}",
            new { name = "Boutique 2", type = "Sqlite", description = "Ma boutique", sourcePath = "C:/secret.db" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await GetJsonAsync($"/api/v1/DatabaseInfo/{id}");
        Assert.Equal("Boutique 2", after.GetProperty("name").GetString());
        Assert.Equal("Ma boutique", after.GetProperty("description").GetString());
        Assert.Equal(sourcePath, SourcePathOf(id));
        Assert.True(after.GetProperty("canResync").GetBoolean());

        var created = await _client.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Vide", type = "Sqlite" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Update_of_an_unknown_entity_is_404_and_a_wrong_body_id_is_400()
    {
        var id = await ImportAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync("/api/v1/DatabaseInfo/nope",
            new { name = "x", type = "Sqlite" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}",
            new { id = "autre", name = "x", type = "Sqlite" })).StatusCode);
    }

    // --- Filtres et pagination ---

    [Fact]
    public async Task Tables_can_be_filtered_by_name_and_type()
    {
        var id = await ImportAsync();

        var byName = await GetJsonAsync($"/api/v1/TableInfo?databaseId={id}&name=ORDER");
        var views = await GetJsonAsync($"/api/v1/TableInfo?databaseId={id}&tableType=View");

        Assert.Equal(["order_lines", "orders"],
            byName.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("customer_emails", Assert.Single(views.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Columns_can_be_filtered_by_table_type_and_primary_key()
    {
        var id = await ImportAsync();
        var orders = await TableIdAsync(id, "orders");

        var ofTable = await GetJsonAsync($"/api/v1/ColumnInfo?tableId={orders}");
        var keys = await GetJsonAsync($"/api/v1/ColumnInfo?databaseId={id}&isPrimaryKey=true");
        var integers = await GetJsonAsync($"/api/v1/ColumnInfo?tableId={orders}&dataType=INTEGER");

        Assert.Equal(3, ofTable.GetArrayLength());
        Assert.All(keys.EnumerateArray(), c => Assert.True(c.GetProperty("isPrimaryKey").GetBoolean()));
        Assert.Equal(5, // customers.id, orders.id, order_lines (2), tags.id
            keys.GetArrayLength());
        Assert.Equal(2, integers.GetArrayLength());
    }

    [Fact]
    public async Task A_filter_with_a_wildcard_character_is_matched_literally()
    {
        await ImportAsync();

        Assert.Empty((await GetJsonAsync("/api/v1/TableInfo?name=%25")).EnumerateArray());
        // « _ » est littéral : il ne correspond qu'aux noms qui contiennent un souligné, pas à n'importe quel caractère.
        Assert.Equal(["customer_emails", "order_lines"], (await GetJsonAsync("/api/v1/TableInfo?name=_")).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task An_invalid_boolean_filter_is_a_400()
    {
        await ImportAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/ColumnInfo?isPrimaryKey=peut-etre")).StatusCode);
    }

    [Fact]
    public async Task Filtered_pages_report_the_filtered_total()
    {
        var id = await ImportAsync();

        var response = await _client.GetAsync($"/api/v1/TableInfo?databaseId={id}&name=order&skip=0&take=1");

        Assert.Equal("2", response.Headers.GetValues("X-Total-Count").Single());
        Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Tables_of_a_database_are_paged_and_filtered()
    {
        var id = await ImportAsync();

        var page = await _client.GetAsync($"/api/v1/TableInfo/database/{id}?skip=1&take=2");
        var filtered = await GetJsonAsync($"/api/v1/TableInfo/database/{id}?name=customer");
        var beyond = await _client.GetAsync($"/api/v1/TableInfo/database/{id}?skip=50&take=10");

        Assert.Equal("5", page.Headers.GetValues("X-Total-Count").Single());
        var pageJson = await page.Content.ReadFromJsonAsync<JsonElement>();
        var names = pageJson.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["customers", "order_lines"], names); // tri par nom : customer_emails, customers, order_lines, ...
        Assert.Equal(2, filtered.GetArrayLength());
        // Les colonnes et relations sont jointes à chaque table de la page.
        Assert.True(pageJson.EnumerateArray().First()
            .GetProperty("columns").GetArrayLength() > 0);
        Assert.Equal(HttpStatusCode.OK, beyond.StatusCode);
        Assert.Equal(0, (await beyond.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [Fact]
    public async Task A_database_without_tables_is_still_404_and_bad_paging_is_400()
    {
        var created = await _client.PostAsJsonAsync("/api/v1/DatabaseInfo", new { name = "Vide", type = "Sqlite" });
        var empty = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/TableInfo/database/{empty}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync($"/api/v1/TableInfo/database/{empty}?take=0")).StatusCode);
    }

    // --- Sous-ressources d'une table ---

    [Fact]
    public async Task A_table_exposes_its_columns_indexes_and_relations()
    {
        var id = await ImportAsync();
        var orders = await TableIdAsync(id, "orders");

        var columns = await GetJsonAsync($"/api/v1/TableInfo/{orders}/columns");
        var relations = await GetJsonAsync($"/api/v1/TableInfo/{orders}/relations");
        var indexes = await GetJsonAsync($"/api/v1/TableInfo/{orders}/indexes");

        Assert.Equal(["id", "customer_id", "note"],
            columns.EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(2, relations.GetArrayLength()); // orders -> customers et order_lines -> orders
        Assert.Equal(JsonValueKind.Array, indexes.ValueKind);
        foreach (var sub in new[] { "columns", "relations", "indexes" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/v1/TableInfo/nope/{sub}")).StatusCode);
    }

    // --- Historique ---

    [Fact]
    public async Task Description_changes_are_recorded_with_old_and_new_value()
    {
        var id = await ImportAsync();

        await _client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}",
            new { name = "Boutique", type = "Sqlite", description = "v1" });
        await _client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}",
            new { name = "Boutique", type = "Sqlite", description = "v2", metaInfo = "m" });

        var response = await _client.GetAsync($"/api/v1/History?entityId={id}");
        var history = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Equal("3", response.Headers.GetValues("X-Total-Count").Single());
        Assert.Equal(3, history.Count);
        var latest = history.Where(h => h.GetProperty("field").GetString() == "Description").First();
        Assert.Equal("v1", latest.GetProperty("oldValue").GetString());
        Assert.Equal("v2", latest.GetProperty("newValue").GetString());
        Assert.Equal("database", latest.GetProperty("entityType").GetString());
        Assert.Contains(history, h => h.GetProperty("field").GetString() == "MetaInfo" &&
                                      h.GetProperty("oldValue").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task An_update_that_changes_no_description_leaves_no_history_and_the_history_pages()
    {
        var id = await ImportAsync();

        await _client.PutAsJsonAsync($"/api/v1/DatabaseInfo/{id}", new { name = "Renommée", type = "Sqlite" });

        Assert.Empty((await GetJsonAsync($"/api/v1/History?entityId={id}")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/History?take=0")).StatusCode);
    }

    [Fact]
    public async Task Synchronization_does_not_write_history()
    {
        var id = await ImportAsync();

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/v1/DatabaseInfo/{id}/sync", null)).StatusCode);

        Assert.Empty((await GetJsonAsync("/api/v1/History")).EnumerateArray());
    }

    // --- Analyse d'impact ---

    [Fact]
    public async Task Impact_of_a_table_follows_the_chain_of_references()
    {
        var id = await ImportAsync();
        var customers = await TableIdAsync(id, "customers");
        var orders = await TableIdAsync(id, "orders");

        var ofCustomers = await GetJsonAsync($"/api/v1/Impact/table/{customers}");
        var ofOrders = await GetJsonAsync($"/api/v1/Impact/table/{orders}");

        var dependents = ofCustomers.GetProperty("dependents").EnumerateArray()
            .Select(n => (n.GetProperty("tableName").GetString(), n.GetProperty("depth").GetInt32())).ToArray();
        Assert.Equal([("orders", 1), ("order_lines", 2)], dependents);
        Assert.Empty(ofCustomers.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("orders", ofOrders.GetProperty("root").GetProperty("tableName").GetString());
        Assert.Equal(["order_lines"], ofOrders.GetProperty("dependents").EnumerateArray()
            .Select(n => n.GetProperty("tableName").GetString()!).ToArray());
        Assert.Equal(["customers"], ofOrders.GetProperty("dependencies").EnumerateArray()
            .Select(n => n.GetProperty("tableName").GetString()!).ToArray());
    }

    [Fact]
    public async Task Impact_of_a_column_lists_the_columns_that_reference_it()
    {
        var id = await ImportAsync();
        var customers = await GetJsonAsync($"/api/v1/TableInfo/{await TableIdAsync(id, "customers")}/columns");
        var customerId = customers.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "id")
            .GetProperty("id").GetString();

        var impact = await GetJsonAsync($"/api/v1/Impact/column/{customerId}");

        var dependent = Assert.Single(impact.GetProperty("dependents").EnumerateArray());
        Assert.Equal("orders", dependent.GetProperty("tableName").GetString());
        Assert.Equal("customer_id", dependent.GetProperty("columnName").GetString());
        Assert.Equal("customers", impact.GetProperty("root").GetProperty("tableName").GetString());
    }

    [Fact]
    public async Task Impact_of_an_unknown_element_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/Impact/table/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/Impact/column/nope")).StatusCode);
    }
}

public class ImpactAnalyzerTests
{
    private static readonly (string, string)[] Chain = [("a", "b"), ("b", "c"), ("c", "a"), ("a", "d")];

    [Fact]
    public void Dependents_are_found_breadth_first_with_their_depth()
    {
        var result = ImpactAnalyzer.Dependents("a", [("a", "b"), ("a", "c"), ("b", "d")]);

        Assert.Equal([new ImpactStep("b", 1), new ImpactStep("c", 1), new ImpactStep("d", 2)], result);
    }

    [Fact]
    public void Dependencies_walk_the_references_backwards()
    {
        var result = ImpactAnalyzer.Dependencies("d", [("a", "b"), ("b", "d")]);

        Assert.Equal([new ImpactStep("b", 1), new ImpactStep("a", 2)], result);
    }

    [Fact]
    public void A_cycle_terminates_and_never_lists_the_start_again()
    {
        var result = ImpactAnalyzer.Dependents("a", Chain);

        Assert.DoesNotContain(result, s => s.Key == "a");
        Assert.Equal(["b", "c", "d"], result.Select(s => s.Key).Order().ToArray());
    }

    [Fact]
    public void A_node_with_no_edge_has_no_impact()
    {
        Assert.Empty(ImpactAnalyzer.Dependents("z", Chain));
        Assert.Empty(ImpactAnalyzer.Dependencies("z", Chain));
    }
}

public sealed class CatalogFilterRepositoryTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    [Fact]
    public async Task Unknown_filter_property_is_rejected_instead_of_reaching_the_sql()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _env.Repository.GetAllAsync<DatabaseInfo>(
            [new CatalogFilter("Name\" OR 1=1 --", "x")]));
    }

    [Fact]
    public async Task Modify_returns_null_for_an_unknown_entity_and_keeps_the_id()
    {
        Assert.Null(await _env.Repository.ModifyAsync<DatabaseInfo>("nope", d => d.Name = "x"));

        var created = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "A", Type = "Sqlite" });
        var modified = await _env.Repository.ModifyAsync<DatabaseInfo>(created.Id, d =>
        {
            d.Id = "tentative";
            d.Description = "d";
        });
        Assert.Equal(created.Id, modified!.Id);
        Assert.Equal(1, await _env.Repository.CountHistoryAsync(created.Id));
    }

    [Fact]
    public async Task A_failing_modification_leaves_neither_the_update_nor_its_history()
    {
        var created = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "A", Type = "Sqlite" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Repository.ModifyAsync<DatabaseInfo>(created.Id,
            d =>
            {
                d.Description = "perdue";
                throw new InvalidOperationException();
            }));

        Assert.Null((await _env.Repository.GetAsync<DatabaseInfo>(created.Id))!.Description);
        Assert.Equal(0, await _env.Repository.CountHistoryAsync(null));
    }
}
