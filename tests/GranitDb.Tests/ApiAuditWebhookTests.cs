using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Journal d'audit et webhooks de l'API.</summary>
public sealed class ApiAuditWebhookTests : IDisposable
{
    private const string V1 = "/api/v1";
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpListener _receiver = new();
    private readonly string _receiverUrl;
    private readonly List<(string Event, string Signature, string Body)> _received = [];
    private int _receiverStatus = 200;
    private readonly CancellationTokenSource _stop = new();

    public ApiAuditWebhookTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data"),
            ["Webhooks:AllowPrivateHosts"] = "true",
            ["Webhooks:MaxAttempts"] = "2",
            ["Webhooks:RetryDelaysMs:0"] = "0"
        });
        _client = _factory.CreateKestrelClient();

        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _receiverUrl = $"http://127.0.0.1:{port}/hook/";
        _receiver.Prefixes.Add(_receiverUrl);
        _receiver.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
                try
                {
                    var context = await _receiver.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (_received)
                        _received.Add((context.Request.Headers["X-GranitDb-Event"] ?? "", context.Request.Headers["X-GranitDb-Signature"] ?? "", body));
                    context.Response.StatusCode = _receiverStatus;
                    context.Response.Close();
                }
                catch
                {
                    break;
                }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _receiver.Close();
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

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

    private async Task<JsonElement> GetJsonAsync(string url) => await _client.GetFromJsonAsync<JsonElement>(url);

    private async Task<(string Id, HttpResponseMessage Response)> CreateHookAsync(object body)
    {
        var response = await _client.PostAsJsonAsync($"{V1}/Webhooks", body);
        var id = response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()! : "";
        return (id, response);
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "Condition non atteinte à temps.");
    }

    private int ReceivedCount() { lock (_received) return _received.Count; }

    // --- Journal d'audit ---

    [Fact]
    public async Task The_audit_log_notes_writes_and_exports_with_the_caller_but_not_plain_reads()
    {
        var db = await ImportAsync();
        using (var rename = new HttpRequestMessage(HttpMethod.Put, $"{V1}/DatabaseInfo/{db}")
               {
                   Content = JsonContent.Create(new { name = "Boutique 2", type = "Sqlite" })
               })
        {
            rename.Headers.Add("X-Test-User", "alice");
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(rename)).StatusCode);
        }

        await _client.GetAsync($"{V1}/DatabaseInfo/{db}");
        await _client.GetAsync($"{V1}/DatabaseInfo/{db}/export/csv");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"{V1}/DatabaseInfo/nope")).StatusCode);

        var all = await _client.GetAsync($"{V1}/Audit");
        var entries = (await all.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.Equal(entries.Count.ToString(), all.Headers.GetValues("X-Total-Count").Single());
        Assert.DoesNotContain(entries, e => e.GetProperty("method").GetString() == "GET" && !e.GetProperty("path").GetString()!.Contains("/export/"));
        Assert.Contains(entries, e => e.GetProperty("method").GetString() == "POST" && e.GetProperty("path").GetString() == "/import/sqlite" && e.GetProperty("status").GetInt32() == 201);
        var put = Assert.Single(entries, e => e.GetProperty("method").GetString() == "PUT");
        Assert.Equal("alice", put.GetProperty("actor").GetString());
        Assert.Equal("DatabaseInfo", put.GetProperty("controller").GetString());
        Assert.Equal(db, put.GetProperty("entityId").GetString());
        Assert.Equal($"/DatabaseInfo/{db}", put.GetProperty("path").GetString());
        Assert.Contains(entries, e => e.GetProperty("path").GetString()!.EndsWith("/export/csv"));
        Assert.Contains(entries, e => e.GetProperty("method").GetString() == "DELETE" && e.GetProperty("status").GetInt32() == 404);
    }

    [Fact]
    public async Task The_audit_log_can_be_filtered_and_paged_and_refuses_bad_paging()
    {
        var db = await ImportAsync();
        await _client.PutAsJsonAsync($"{V1}/DatabaseInfo/{db}", new { name = "B", type = "Sqlite" });
        await _client.PutAsJsonAsync($"{V1}/DatabaseInfo/{db}", new { name = "C", type = "Sqlite" });

        var puts = await _client.GetAsync($"{V1}/Audit?method=put&entityId={db}&take=1");
        var page = (await puts.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var future = await GetJsonAsync($"{V1}/Audit?from={Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("o"))}");

        Assert.Single(page);
        Assert.Equal("2", puts.Headers.GetValues("X-Total-Count").Single());
        Assert.Empty(future.EnumerateArray());
        Assert.Empty((await GetJsonAsync($"{V1}/Audit?actor=nobody")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"{V1}/Audit?take=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"{V1}/Audit?skip=-1")).StatusCode);
    }

    [Fact]
    public async Task The_audit_log_never_keeps_what_was_sent()
    {
        await _client.PostAsJsonAsync($"{V1}/Webhooks", new { url = _receiverUrl, secret = "TOP-SECRET-VALUE" });

        var text = await _client.GetStringAsync($"{V1}/Audit");

        Assert.Contains("/Webhooks", text);
        Assert.DoesNotContain("TOP-SECRET-VALUE", text);
    }

    // --- Webhooks ---

    [Fact]
    public async Task A_schema_change_is_sent_signed_to_the_webhooks_of_that_database_only()
    {
        var db = await ImportAsync();
        var other = await ImportAsync("Autre");
        var (hook, created) = await CreateHookAsync(new { url = _receiverUrl, secret = "s3cret", databaseId = db });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var (elsewhere, _) = await CreateHookAsync(new { url = _receiverUrl, databaseId = other, isActive = true });
        var (paused, _) = await CreateHookAsync(new { url = _receiverUrl, isActive = false });

        // Le schéma change : une table de plus.
        var newer = _env.CreateSampleDb($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(newer, "CREATE TABLE invoices (id INTEGER PRIMARY KEY, total REAL);");
        var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(newer)), "file", "next.db" } };
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsync($"{V1}/import/sqlite/{db}", form)).StatusCode);

        await WaitForAsync(() => ReceivedCount() >= 1);
        (string Event, string Signature, string Body) message;
        lock (_received) message = _received.Single();
        Assert.Equal("schema.changed", message.Event);
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(message.Body))).ToLowerInvariant();
        Assert.Equal(expected, message.Signature);
        var json = JsonDocument.Parse(message.Body).RootElement;
        Assert.Equal(db, json.GetProperty("data").GetProperty("database").GetProperty("id").GetString());
        Assert.Equal(1, json.GetProperty("data").GetProperty("changes").GetProperty("tablesAdded").GetInt32());

        await WaitForAsync(() => GetJsonAsync($"{V1}/Webhooks/{hook}/deliveries").Result.GetArrayLength() == 1);
        var delivery = (await GetJsonAsync($"{V1}/Webhooks/{hook}/deliveries")).EnumerateArray().Single();
        Assert.True(delivery.GetProperty("success").GetBoolean());
        Assert.Equal(200, delivery.GetProperty("statusCode").GetInt32());
        Assert.Equal(0, (await GetJsonAsync($"{V1}/Webhooks/{elsewhere}/deliveries")).GetArrayLength());
        Assert.Equal(0, (await GetJsonAsync($"{V1}/Webhooks/{paused}/deliveries")).GetArrayLength());
    }

    [Fact]
    public async Task Deleting_a_database_notifies_only_the_webhooks_that_subscribed_to_it()
    {
        var db = await ImportAsync();
        var (listener, _) = await CreateHookAsync(new { url = _receiverUrl, databaseId = db, events = new[] { "database.deleted" } });
        var (schemaOnly, _) = await CreateHookAsync(new { url = _receiverUrl, databaseId = db });

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/DatabaseInfo/{db}")).StatusCode);

        await WaitForAsync(() => ReceivedCount() >= 1);
        await Task.Delay(500);
        (string Event, string Signature, string Body) message;
        lock (_received) message = _received.Single();
        Assert.Equal("database.deleted", message.Event);
        Assert.Equal(db, JsonDocument.Parse(message.Body).RootElement.GetProperty("data").GetProperty("database").GetProperty("id").GetString());
        Assert.Equal(0, (await GetJsonAsync($"{V1}/Webhooks/{schemaOnly}/deliveries")).GetArrayLength());
        Assert.Equal(1, (await GetJsonAsync($"{V1}/Webhooks/{listener}/deliveries")).GetArrayLength());
    }

    [Fact]
    public async Task Importing_a_database_notifies_the_webhooks_subscribed_to_imports()
    {
        var (listener, _) = await CreateHookAsync(new { url = _receiverUrl, events = new[] { "database.imported" } });

        var db = await ImportAsync("Nouvelle");

        await WaitForAsync(() => ReceivedCount() >= 1);
        (string Event, string Signature, string Body) message;
        lock (_received) message = _received.Single();
        Assert.Equal("database.imported", message.Event);
        var data = JsonDocument.Parse(message.Body).RootElement.GetProperty("data");
        Assert.Equal(db, data.GetProperty("database").GetProperty("id").GetString());
        Assert.Equal("Nouvelle", data.GetProperty("database").GetProperty("name").GetString());
        Assert.True(data.GetProperty("tables").GetInt32() > 0);
        Assert.Equal(1, (await GetJsonAsync($"{V1}/Webhooks/{listener}/deliveries")).GetArrayLength());
    }

    [Fact]
    public async Task A_resync_that_changes_nothing_sends_nothing()
    {
        var db = await ImportAsync();
        await CreateHookAsync(new { url = _receiverUrl });

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"{V1}/DatabaseInfo/{db}/sync", null)).StatusCode);
        await Task.Delay(700);

        Assert.Equal(0, ReceivedCount());
    }

    [Fact]
    public async Task A_failing_receiver_is_retried_and_the_failure_is_recorded()
    {
        _receiverStatus = 500;
        var (hook, _) = await CreateHookAsync(new { url = _receiverUrl });

        var test = await _client.PostAsync($"{V1}/Webhooks/{hook}/test", null);

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var delivery = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(delivery.GetProperty("success").GetBoolean());
        Assert.Equal(2, delivery.GetProperty("attempts").GetInt32());
        Assert.Equal(500, delivery.GetProperty("statusCode").GetInt32());
        Assert.Equal(2, ReceivedCount());
        Assert.Equal("ping", _received[0].Event);
    }

    [Fact]
    public async Task The_secret_is_never_returned_and_a_webhook_can_be_changed_and_deleted()
    {
        var db = await ImportAsync();
        var (hook, created) = await CreateHookAsync(new { url = _receiverUrl, secret = "s3cret", description = "CI" });
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("\"hasSecret\":true", text);
        Assert.DoesNotContain("s3cret", await _client.GetStringAsync($"{V1}/Webhooks"));

        // Un champ absent laisse le secret ; une chaîne vide le retire.
        var kept = await _client.PutAsJsonAsync($"{V1}/Webhooks/{hook}", new { url = _receiverUrl + "x", databaseId = db });
        Assert.True((await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());
        var removed = await _client.PutAsJsonAsync($"{V1}/Webhooks/{hook}", new { url = _receiverUrl, secret = "" });
        Assert.False((await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasSecret").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{V1}/Webhooks/{hook}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/Webhooks/{hook}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{V1}/Webhooks/{hook}/deliveries")).StatusCode);
    }

    [Fact]
    public async Task Invalid_webhooks_are_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateHookAsync(new { url = "ftp://example.com/x" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateHookAsync(new { url = "https://user:pw@example.com/x" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateHookAsync(new { url = _receiverUrl, events = new[] { "table.dropped" } })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateHookAsync(new { url = _receiverUrl, databaseId = "nope" })).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"{V1}/Webhooks/nope/test", null)).StatusCode);
    }
}

/// <summary>Sans réglage, un webhook ne peut pas viser le réseau local ou privé du serveur.</summary>
public sealed class ApiWebhookPrivateHostTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _client;

    public ApiWebhookPrivateHostTests()
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

    [Theory]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.10/hook")]
    [InlineData("http://172.20.0.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://host.docker.internal/hook")]
    [InlineData("http://[::ffff:10.0.0.1]/hook")]
    public async Task Local_and_private_addresses_are_refused(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/Webhooks", new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("privée", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_public_address_is_accepted()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/Webhooks", new { url = "https://hooks.example.com/granitdb" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
