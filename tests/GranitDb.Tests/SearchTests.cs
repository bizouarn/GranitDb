using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Connectors;
using GranitDB.API.Models.Descriptions;

namespace GranitDb.Tests;

public class SearchRepositoryTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task SeedAsync()
    {
        var path = _env.CreateSampleDb("boutique.db");
        TestCatalog.Execute(path, "CREATE UNIQUE INDEX ux_customers_email ON customers(email);");
        await _env.Synchronizer.SyncAsync("shop", await new SqliteSchemaConnector().ReadSchemaAsync(path), "Boutique en ligne");
        var customers = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "shop")).Single(t => t.Name == "customers");
        customers.Description = "Les clients inscrits";
        await _env.Repository.UpdateAsync(customers.Id, customers);
        var email = (await _env.Repository.FindAsync<ColumnInfo>("TableId", customers.Id)).Single(c => c.Name == "email");
        email.Description = "Adresse de connexion";
        await _env.Repository.UpdateAsync(email.Id, email);
    }

    [Fact]
    public async Task Finds_a_domain_and_the_tables_filed_in_it()
    {
        await SeedAsync();
        var customers = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "shop")).Single(t => t.Name == "customers");
        var domain = await _env.Repository.CreateAsync(new DomainInfo { Name = "Ventes", DatabaseId = "shop" });
        await _env.Repository.CreateAsync(new DomainTableInfo { DomainId = domain.Id, TableId = customers.Id });

        var hits = await _env.Repository.SearchAsync("vent", 25);

        var found = Assert.Single(hits, h => h.Kind == "domain");
        Assert.Equal("Ventes", found.Name);
        Assert.Equal("shop", found.DatabaseId);
        var table = Assert.Single(hits, h => h.Kind == "table");
        Assert.Equal("customers", table.Name);
        Assert.Equal("domaine « Ventes »", table.Via);
    }

    [Fact]
    public async Task Finds_the_elements_carrying_a_tag_with_the_tag_as_the_reason()
    {
        await SeedAsync();
        var customers = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "shop")).Single(t => t.Name == "customers");
        var email = (await _env.Repository.FindAsync<ColumnInfo>("TableId", customers.Id)).Single(c => c.Name == "email");
        var tag = await _env.Repository.CreateAsync(new TagInfo { Name = "RGPD" });
        await _env.Repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = TagLinkInfo.Table, EntityId = customers.Id });
        await _env.Repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = TagLinkInfo.Column, EntityId = email.Id });
        await _env.Repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = TagLinkInfo.Database, EntityId = "shop" });

        var hits = await _env.Repository.SearchAsync("rgpd", 25);

        Assert.All(hits, h => Assert.Equal("étiquette « RGPD »", h.Via));
        Assert.Equal(["column", "database", "table"], hits.Select(h => h.Kind).Order().ToArray());
        Assert.Equal("email", hits.Single(h => h.Kind == "column").Name);
    }

    [Fact]
    public async Task A_match_by_name_is_not_repeated_by_its_tag_and_keeps_no_reason()
    {
        await SeedAsync();
        var customers = (await _env.Repository.FindAsync<TableInfo>("DatabaseId", "shop")).Single(t => t.Name == "customers");
        var tag = await _env.Repository.CreateAsync(new TagInfo { Name = "customers-legacy" });
        await _env.Repository.CreateAsync(new TagLinkInfo { TagId = tag.Id, EntityType = TagLinkInfo.Table, EntityId = customers.Id });

        var hits = await _env.Repository.SearchAsync("customers", 25);

        var table = Assert.Single(hits, h => h.Kind == "table" && h.Name == "customers");
        Assert.Null(table.Via);
    }

    [Fact]
    public async Task Finds_each_kind_by_name_with_its_context()
    {
        await SeedAsync();

        var hits = await _env.Repository.SearchAsync("custom", 25);

        var table = Assert.Single(hits, h => h.Kind == "table" && h.Name == "customers");
        Assert.Equal("shop", table.DatabaseId);
        Assert.Equal("Boutique en ligne", table.DatabaseName);
        Assert.Equal(table.Id, table.TableId);
        var column = Assert.Single(hits, h => h.Kind == "column" && h.Name == "customer_id");
        Assert.Equal("orders", column.TableName);
        Assert.Equal("shop", column.DatabaseId);
        Assert.Contains(hits, h => h.Kind == "index" && h.Name == "ux_customers_email");
        Assert.Contains(hits, h => h.Kind == "table" && h.Name == "customer_emails"); // la vue
    }

    [Fact]
    public async Task Finds_databases_by_name_and_by_description()
    {
        await SeedAsync();
        var database = (await _env.Repository.GetAsync<DatabaseInfo>("shop"))!;
        database.Description = "Catalogue des produits vendus";
        await _env.Repository.UpdateAsync("shop", database);

        Assert.Equal("database", Assert.Single(await _env.Repository.SearchAsync("boutique", 5), h => h.Kind == "database").Kind);
        var byDescription = await _env.Repository.SearchAsync("produits vendus", 5);
        Assert.Equal("Boutique en ligne", Assert.Single(byDescription).Name);
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_looks_into_descriptions()
    {
        await SeedAsync();

        var hits = await _env.Repository.SearchAsync("CLIENTS INSCRITS", 25);
        Assert.Equal("customers", Assert.Single(hits).Name);

        var column = await _env.Repository.SearchAsync("connexion", 25);
        Assert.Equal("email", Assert.Single(column).Name);
        Assert.Equal("column", column[0].Kind);
    }

    [Fact]
    public async Task Index_is_found_by_the_columns_it_covers()
    {
        await SeedAsync();

        var hits = await _env.Repository.SearchAsync("email", 25);

        var index = Assert.Single(hits, h => h.Kind == "index");
        Assert.Equal("ux_customers_email", index.Name);
        Assert.Equal("customers", index.TableName);
    }

    [Fact]
    public async Task Percent_underscore_and_backslash_are_literal()
    {
        await SeedAsync();

        Assert.Empty(await _env.Repository.SearchAsync("%%", 25));
        Assert.Empty(await _env.Repository.SearchAsync("c_st", 25)); // « _ » ne vaut pas « n'importe quel caractère »
        Assert.NotEmpty(await _env.Repository.SearchAsync("customer_", 25));
        Assert.Empty(await _env.Repository.SearchAsync("\\", 25));
    }

    [Fact]
    public async Task Injection_attempts_are_treated_as_plain_text()
    {
        await SeedAsync();

        Assert.Empty(await _env.Repository.SearchAsync("'; DROP TABLE \"Databases\"; --", 25));
        Assert.Single(await _env.Repository.GetAllAsync<DatabaseInfo>());
    }

    [Fact]
    public async Task Limit_applies_per_kind_and_results_are_sorted_by_name()
    {
        var tables = Enumerable.Range(0, 30).Select(i => new SourceTable($"item_{i:00}", "Table",
            [new SourceColumn("item_col", "TEXT", true, false, null, 0)])).ToList();
        await _env.Synchronizer.SyncAsync("big", new SourceSchema("big", "Sqlite", tables, []));

        var hits = await _env.Repository.SearchAsync("item", 10);

        var tableHits = hits.Where(h => h.Kind == "table").ToList();
        Assert.Equal(10, tableHits.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"item_{i:00}"), tableHits.Select(h => h.Name));
        Assert.Equal(10, hits.Count(h => h.Kind == "column")); // la limite vaut aussi pour les colonnes
    }

    [Fact]
    public async Task Nothing_matches_an_unknown_term()
    {
        await SeedAsync();
        Assert.Empty(await _env.Repository.SearchAsync("zzzz-introuvable", 25));
    }

    [Fact]
    public async Task Search_works_inside_a_transaction()
    {
        await SeedAsync();

        var count = await _env.Repository.InTransactionAsync(async repo => (await repo.SearchAsync("orders", 25)).Count);

        Assert.True(count >= 1);
    }
}

public sealed class SearchApiTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public SearchApiTests()
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
        _env.Dispose();
    }

    private async Task ImportAsync()
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(File.ReadAllBytes(_env.CreateSampleDb())), "file", "boutique.db" },
            { new StringContent("Boutique"), "name" }
        };
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsync("/import/sqlite", form)).StatusCode);
    }

    [Fact]
    public async Task Search_returns_hits_with_enough_context_to_navigate()
    {
        await ImportAsync();

        var hits = await _client.GetFromJsonAsync<JsonElement>("/Search?q=orders");

        var table = hits.EnumerateArray().Single(h => h.GetProperty("kind").GetString() == "table");
        Assert.Equal("orders", table.GetProperty("name").GetString());
        Assert.Equal("Boutique", table.GetProperty("databaseName").GetString());
        Assert.False(string.IsNullOrEmpty(table.GetProperty("databaseId").GetString()));
        Assert.False(string.IsNullOrEmpty(table.GetProperty("tableId").GetString()));
    }

    [Fact]
    public async Task Search_on_an_empty_catalog_returns_an_empty_list()
    {
        Assert.Equal("[]", (await _client.GetStringAsync("/Search?q=anything")).Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("%20%20")]
    public async Task Too_short_terms_are_rejected(string q)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/Search?q={q}")).StatusCode);
    }

    [Fact]
    public async Task Missing_term_too_long_term_and_bad_limit_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/Search")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/Search?q={new string('x', 101)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/Search?q=ab&limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/Search?q=ab&limit=101")).StatusCode);
    }

    [Fact]
    public async Task Search_terms_with_special_characters_do_not_break_the_query()
    {
        await ImportAsync();

        var response = await _client.GetAsync("/Search?q=" + Uri.EscapeDataString("100% 'quoted' _x\\"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", (await response.Content.ReadAsStringAsync()).Trim());
    }
}
