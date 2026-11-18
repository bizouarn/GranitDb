using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Models.Descriptions;

namespace GranitDb.Tests;

public class PaginationRepositoryTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private async Task SeedAsync(int count)
    {
        // Créées dans le désordre pour vérifier le tri par nom.
        foreach (var i in Enumerable.Range(0, count).OrderBy(_ => Guid.NewGuid()))
            await _env.Repository.CreateAsync(new DatabaseInfo { Name = $"base_{i:000}", Type = "Sqlite" });
    }

    [Fact]
    public async Task Pages_are_sorted_by_name_and_do_not_overlap()
    {
        await SeedAsync(25);

        var first = await _env.Repository.GetPageAsync<DatabaseInfo>(0, 10);
        var second = await _env.Repository.GetPageAsync<DatabaseInfo>(10, 10);
        var third = await _env.Repository.GetPageAsync<DatabaseInfo>(20, 10);

        Assert.Equal(10, first.Count);
        Assert.Equal(10, second.Count);
        Assert.Equal(5, third.Count);
        var all = first.Concat(second).Concat(third).Select(d => d.Name).ToList();
        Assert.Equal(Enumerable.Range(0, 25).Select(i => $"base_{i:000}"), all);
    }

    [Fact]
    public async Task Page_beyond_the_end_is_empty_and_count_is_the_total()
    {
        await SeedAsync(3);

        Assert.Empty(await _env.Repository.GetPageAsync<DatabaseInfo>(50, 10));
        Assert.Equal(3, await _env.Repository.CountAsync<DatabaseInfo>());
        Assert.Equal(0, await _env.Repository.CountAsync<TableInfo>());
    }

    [Fact]
    public async Task Entities_without_a_name_are_paged_by_id()
    {
        var db = await _env.Repository.CreateAsync(new DatabaseInfo { Name = "d", Type = "Sqlite" });
        var t = await _env.Repository.CreateAsync(new TableInfo { Name = "t", DatabaseId = db.Id });
        var a = await _env.Repository.CreateAsync(new ColumnInfo { Name = "a", TableId = t.Id, DatabaseId = db.Id });
        var b = await _env.Repository.CreateAsync(new ColumnInfo { Name = "b", TableId = t.Id, DatabaseId = db.Id });
        await _env.Repository.CreateAsync(new RelationInfo { FirstId = a.Id, SecondId = b.Id });
        await _env.Repository.CreateAsync(new RelationInfo { FirstId = b.Id, SecondId = a.Id });

        var page = await _env.Repository.GetPageAsync<RelationInfo>(1, 5);

        Assert.Single(page);
        Assert.Equal(2, await _env.Repository.CountAsync<RelationInfo>());
    }
}

public sealed class PaginationApiTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public PaginationApiTests()
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

    private async Task SeedAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await _client.PostAsJsonAsync("/DatabaseInfo",
                new { name = $"base_{i:000}", type = "Sqlite" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Without_parameters_everything_is_returned_and_there_is_no_total_header()
    {
        await SeedAsync(4);

        var response = await _client.GetAsync("/DatabaseInfo");

        Assert.Equal(4, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.False(response.Headers.Contains("X-Total-Count"));
    }

    [Fact]
    public async Task With_skip_and_take_a_page_and_the_total_are_returned()
    {
        await SeedAsync(7);

        var response = await _client.GetAsync("/DatabaseInfo?skip=2&take=3");

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "base_002", "base_003", "base_004" },
            page.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray());
        Assert.Equal("7", response.Headers.GetValues("X-Total-Count").Single());
    }

    [Fact]
    public async Task Take_alone_defaults_skip_to_zero_and_skip_alone_defaults_take_to_fifty()
    {
        await SeedAsync(3);

        var takeOnly = await _client.GetFromJsonAsync<JsonElement>("/DatabaseInfo?take=2");
        var skipOnly = await _client.GetFromJsonAsync<JsonElement>("/DatabaseInfo?skip=1");

        Assert.Equal(2, takeOnly.GetArrayLength());
        Assert.Equal(2, skipOnly.GetArrayLength());
    }

    [Theory]
    [InlineData("skip=-1")]
    [InlineData("take=0")]
    [InlineData("take=1001")]
    public async Task Invalid_paging_parameters_return_400(string query)
    {
        var response = await _client.GetAsync($"/DatabaseInfo?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cors_exposes_the_total_count_header()
    {
        await SeedAsync(1);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/DatabaseInfo?take=1");
        request.Headers.Add("Origin", "http://localhost:8081");

        var response = await _client.SendAsync(request);

        Assert.Contains("X-Total-Count", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }
}
