using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GranitDB.API.Exceptions;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GranitDb.Tests;

/// <summary>Faux serveur compatible OpenAI : répond avec les contenus donnés, dans l'ordre, et garde les corps reçus.</summary>
internal sealed class FakeAiServer(params string[] contents) : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string[] Models { get; set; } = ["qwen3:4b-instruct"];
    public TimeSpan Delay { get; set; }
    public bool Fail { get; set; }
    private int _next;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("connexion refusée");
        Requests.Add(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (request.Method == HttpMethod.Get)
            return Json($$"""{"data":[{{string.Join(",", Models.Select(m => $"{{\"id\":\"{m}\"}}"))}}]}""");
        Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
        if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
        var content = contents[Math.Min(_next++, contents.Length - 1)];
        return Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = 12, completion_tokens = 5 }
        }));
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class AiProviderTests
{
    private const string Schema = """{"type":"object","properties":{"description":{"type":"string"}},"required":["description"],"additionalProperties":false}""";

    private static OpenAiCompatibleProvider Provider(FakeAiServer server, Action<CatalogOptions>? catalog = null, Action<AiOptions>? ai = null)
    {
        var c = new CatalogOptions { AllowAi = true };
        var a = new AiOptions();
        catalog?.Invoke(c);
        ai?.Invoke(a);
        return new OpenAiCompatibleProvider(Microsoft.Extensions.Options.Options.Create(c), Microsoft.Extensions.Options.Options.Create(a),
            NullLogger<OpenAiCompatibleProvider>.Instance, server);
    }

    private static readonly AiRequest Ask = new("Tu documentes des tables.", "Table clients : id, nom.", Schema,
        e => e.TryGetProperty("description", out _) ? null : "le champ description manque");

    [Fact]
    public async Task A_valid_json_answer_is_returned_parsed_with_its_cost()
    {
        var server = new FakeAiServer("""{"description":"Les clients."}""");
        using var provider = Provider(server);

        var response = await provider.CompleteAsync(Ask);

        Assert.Equal("Les clients.", response.Json!.Value.GetProperty("description").GetString());
        Assert.Equal((12, 5, "qwen3:4b-instruct"), (response.PromptTokens, response.CompletionTokens, response.Model));
        var body = JsonDocument.Parse(server.Bodies.Single()).RootElement;
        Assert.Equal("qwen3:4b-instruct", body.GetProperty("model").GetString());
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("none", body.GetProperty("reasoning_effort").GetString()); // pas de bloc de réflexion : bien plus rapide sur CPU
        Assert.Contains("français", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Invalid_json_is_retried_once_telling_the_model_what_was_wrong()
    {
        var server = new FakeAiServer("Voici : pas de JSON", """{"description":"ok"}""");
        using var provider = Provider(server);

        var response = await provider.CompleteAsync(Ask);

        Assert.Equal("ok", response.Json!.Value.GetProperty("description").GetString());
        Assert.Equal(2, server.Bodies.Count);
        Assert.Contains("pas du JSON valide", server.Bodies[1]);
        Assert.Equal(24, response.PromptTokens); // le coût des deux appels
    }

    [Fact]
    public async Task Two_invalid_answers_end_with_a_clean_error_and_never_a_third_call()
    {
        var server = new FakeAiServer("""{"autre":1}""");
        using var provider = Provider(server);

        var error = await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(Ask));

        Assert.Contains("format attendu", error.Message);
        Assert.Contains("description manque", error.Message);
        Assert.Equal(2, server.Bodies.Count);
    }

    [Fact]
    public async Task Thinking_blocks_are_removed_from_the_answer()
    {
        var server = new FakeAiServer("<think>je réfléchis\nlonguement</think>\n{\"description\":\"net\"}");
        using var provider = Provider(server);

        var response = await provider.CompleteAsync(Ask);

        Assert.Equal("""{"description":"net"}""", response.Content);
    }

    [Fact]
    public async Task Without_a_schema_the_text_comes_back_as_is()
    {
        using var provider = Provider(new FakeAiServer("Bonjour"));

        var response = await provider.CompleteAsync(new AiRequest("s", "u"));

        Assert.Equal("Bonjour", response.Content);
        Assert.Null(response.Json);
    }

    [Fact]
    public async Task The_assistant_is_disabled_by_default_and_calls_nothing()
    {
        var server = new FakeAiServer("x");
        using var provider = Provider(server, c => c.AllowAi = false);

        var error = await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(Ask));

        Assert.Contains("Catalog:AllowAi", error.Message);
        Assert.Empty(server.Requests);
        Assert.False(new CatalogOptions().AllowAi);
    }

    [Theory]
    [InlineData("http://example.com/v1", false)]
    [InlineData("http://192.168.1.20:11434/v1", false)]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("http://ollama:11434/v1", false)]
    public void A_remote_endpoint_must_be_https_unless_explicitly_allowed(string endpoint, bool accepted)
    {
        using var provider = Provider(new FakeAiServer("x"), ai: a => a.Endpoint = endpoint);
        using var allowed = Provider(new FakeAiServer("x"), ai: a => { a.Endpoint = endpoint; a.AllowRemote = true; });

        if (accepted) Assert.NotNull(provider.Endpoint());
        else Assert.Contains("https", Assert.Throws<GranitExceptions>(() => provider.Endpoint()).Message);
        Assert.NotNull(allowed.Endpoint());
    }

    [Theory]
    [InlineData("ftp://localhost/v1")]
    [InlineData("pas une adresse")]
    [InlineData("http://user:pass@localhost/v1")]
    [InlineData("http://localhost/v1?key=secret")]
    public void A_malformed_endpoint_or_one_carrying_credentials_is_refused(string endpoint)
    {
        using var provider = Provider(new FakeAiServer("x"), ai: a => a.Endpoint = endpoint);

        Assert.Throws<GranitExceptions>(() => provider.Endpoint());
    }

    [Fact]
    public async Task An_oversized_request_is_refused_before_any_call()
    {
        var server = new FakeAiServer("x");
        using var provider = Provider(server, ai: a => a.MaxRequestChars = 100);

        var error = await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(new AiRequest("s", new string('x', 200))));

        Assert.Contains("Ai:MaxRequestChars", error.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_api_key_is_sent_as_a_bearer_token_and_can_come_from_the_environment()
    {
        Environment.SetEnvironmentVariable("GRANITDB_TEST_AI_KEY", "cle-secrete");
        try
        {
            var server = new FakeAiServer("ok");
            using var provider = Provider(server, ai: a => a.ApiKey = "env:GRANITDB_TEST_AI_KEY");

            await provider.CompleteAsync(new AiRequest("s", "u"));

            Assert.Equal("Bearer", server.Requests.Single().Headers.Authorization!.Scheme);
            Assert.Equal("cle-secrete", server.Requests.Single().Headers.Authorization!.Parameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRANITDB_TEST_AI_KEY", null);
        }
    }

    [Fact]
    public async Task A_missing_model_an_error_status_and_an_unreachable_server_give_readable_errors()
    {
        var missing = new FakeAiServer("x") { Status = HttpStatusCode.NotFound };
        using (var provider = Provider(missing))
            Assert.Contains("téléchargement", (await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(new AiRequest("s", "u")))).Message);

        var broken = new FakeAiServer("x") { Status = HttpStatusCode.InternalServerError };
        using (var provider = Provider(broken))
            Assert.Contains("500", (await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(new AiRequest("s", "u")))).Message);

        var down = new FakeAiServer("x") { Fail = true };
        using (var provider = Provider(down))
            Assert.Contains("injoignable", (await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(new AiRequest("s", "u")))).Message);
    }

    [Fact]
    public async Task A_slow_model_ends_on_the_timeout_and_the_callers_cancellation_is_not_swallowed()
    {
        var slow = new FakeAiServer("x") { Delay = TimeSpan.FromSeconds(5) };
        using var provider = Provider(slow, ai: a => a.TimeoutSeconds = 1);

        var error = await Assert.ThrowsAsync<GranitExceptions>(() => provider.CompleteAsync(new AiRequest("s", "u")));
        Assert.Contains("Ai:TimeoutSeconds", error.Message);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(new AiRequest("s", "u"), cts.Token));
    }

    [Fact]
    public async Task Calls_wait_for_their_turn_when_the_model_allows_one_at_a_time()
    {
        var server = new FakeAiServer("ok") { Delay = TimeSpan.FromMilliseconds(300) };
        using var provider = Provider(server, ai: a => a.MaxConcurrent = 1);
        var started = DateTime.UtcNow;

        await Task.WhenAll(provider.CompleteAsync(new AiRequest("s", "u")), provider.CompleteAsync(new AiRequest("s", "u")));

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(550), "les deux appels ne devraient pas se chevaucher");
    }

    [Fact]
    public async Task Status_tells_whether_the_server_answers_and_the_model_is_installed()
    {
        using (var ready = Provider(new FakeAiServer("x")))
        {
            var status = await ready.StatusAsync();
            Assert.Equal((true, true, true, null), (status.Enabled, status.Reachable, status.ModelAvailable, status.Message));
            Assert.Equal("http://localhost:11434", status.Endpoint);
        }

        using (var pulling = Provider(new FakeAiServer("x") { Models = ["autre:1b"] }))
        {
            var status = await pulling.StatusAsync();
            Assert.True(status.Reachable);
            Assert.False(status.ModelAvailable);
            Assert.Contains("ollama pull qwen3:4b-instruct", status.Message);
        }

        using (var down = Provider(new FakeAiServer("x") { Fail = true }))
            Assert.False((await down.StatusAsync()).Reachable);
        using (var off = Provider(new FakeAiServer("x"), c => c.AllowAi = false))
            Assert.False((await off.StatusAsync()).Enabled);
    }

    [Fact]
    public async Task Status_never_shows_the_key_or_the_path_of_the_endpoint()
    {
        using var provider = Provider(new FakeAiServer("x"), ai: a => { a.ApiKey = "super-secret"; a.Endpoint = "https://api.example.com/private/v1"; });

        var status = await provider.StatusAsync();

        Assert.DoesNotContain("super-secret", JsonSerializer.Serialize(status));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(status));
    }
}

/// <summary>Routes de l'IA : désactivée par défaut, capacité annoncée.</summary>
public sealed class AiApiTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    private KestrelApiFactory Start(bool allow) => new(new Dictionary<string, string>
    {
        ["Catalog:Provider"] = "Sqlite",
        ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-" + Guid.NewGuid().ToString("N")),
        ["Catalog:AllowAi"] = allow ? "true" : "false",
        ["Ai:Endpoint"] = "http://127.0.0.1:9/v1", // personne n'écoute
        ["Ai:TimeoutSeconds"] = "2"
    });

    [Fact]
    public async Task The_capability_is_off_by_default_and_the_test_route_answers_400_with_the_reason()
    {
        using var factory = Start(false);
        using var http = factory.CreateKestrelClient();

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v1/Capabilities");
        var test = await http.PostAsync("/api/v1/Ai/test", null);
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/Ai/status");

        Assert.False(capabilities.GetProperty("allowAi").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        Assert.Contains("AllowAi", await test.Content.ReadAsStringAsync());
        Assert.False(status.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task When_enabled_the_capability_names_the_model_and_an_unreachable_server_is_reported()
    {
        using var factory = Start(true);
        using var http = factory.CreateKestrelClient();

        var capabilities = await http.GetFromJsonAsync<JsonElement>("/api/v1/Capabilities");
        var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/Ai/status");
        var test = await http.PostAsync("/api/v1/Ai/test", null);

        Assert.True(capabilities.GetProperty("allowAi").GetBoolean());
        Assert.Equal("qwen3:4b-instruct", capabilities.GetProperty("aiModel").GetString());
        Assert.False(status.GetProperty("reachable").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        var reason = await test.Content.ReadAsStringAsync();
        Assert.True(reason.Contains("injoignable") || reason.Contains("Ai:TimeoutSeconds"), reason);
    }
}
