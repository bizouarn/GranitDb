using System.Net;
using GranitDb.Client.api;
using ApiClient = GranitDb.Client.api.Client;

namespace GranitDb.Tests;

/// <summary>
///     Les identifiants des appels de l'interface : joints seulement quand l'API demande une session. Sans
///     <c>Cors:AllowedOrigins</c> l'API n'autorise pas les identifiants et le navigateur bloquerait chaque appel qui les joint.
/// </summary>
public class ClientCredentialsTests
{
    /// <summary>L'API vue du navigateur : note le mode d'identification de chaque appel.</summary>
    private sealed class Recorder(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Credentials)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var options = request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var o)
                ? o
                : null;
            var credentials = options != null && options.TryGetValue("credentials", out var c) ? c.ToString()! : "";
            Calls.Add((request.RequestUri!.AbsolutePath, credentials));
            return Task.FromResult(respond(request, credentials));
        }
    }

    private static ApiClient NewClient(Recorder api)
    {
        var session = new SessionHandler(api);
        return new ApiClient(new HttpClient(session) { BaseAddress = new Uri("http://api.test/") }, session);
    }

    private static HttpResponseMessage Json(string body) => FakeApi.Json(body);

    [Fact]
    public async Task Without_authentication_no_call_asks_the_browser_to_include_credentials()
    {
        var api = new Recorder((r, _) => r.RequestUri!.AbsolutePath == "/api/v1/Auth/me"
            ? Json("""{"enabled":false,"authenticated":false,"providers":[]}""")
            : Json("[]"));
        var client = NewClient(api);

        var session = await client.GetSessionAsync();
        await client.GetAllDatabaseAsync();

        Assert.False(session.Enabled);
        Assert.Equal([("/api/v1/Auth/me", "same-origin"), ("/api/v1/DatabaseInfo", "same-origin")], api.Calls);
    }

    [Fact]
    public async Task With_authentication_the_session_is_read_again_with_the_cookie_and_later_calls_send_it()
    {
        // Sans le cookie l'API ne connaît pas l'appelant ; avec lui, elle reconnaît la session.
        var api = new Recorder((r, credentials) => r.RequestUri!.AbsolutePath != "/api/v1/Auth/me" ? Json("[]")
            : credentials == "include" ? Json("""{"enabled":true,"authenticated":true,"login":"alice","role":"Editor","providers":[]}""")
            : Json("""{"enabled":true,"authenticated":false,"providers":[]}"""));
        var client = NewClient(api);

        var session = await client.GetSessionAsync();
        await client.GetAllDatabaseAsync();
        await client.GetSessionAsync();

        Assert.True(session.Authenticated);
        Assert.Equal("alice", session.Login);
        Assert.Equal(["same-origin", "include", "include", "include"], api.Calls.Select(c => c.Credentials).ToArray());
    }

    [Fact]
    public async Task With_authentication_but_an_undeclared_origin_the_interface_still_knows_a_session_is_required()
    {
        // Le navigateur bloque l'appel avec identifiants quand l'origine n'est pas déclarée.
        var api = new Recorder((_, credentials) => credentials == "include"
            ? throw new HttpRequestException("Failed to fetch")
            : Json("""{"enabled":true,"authenticated":false,"providers":[{"id":"github","displayName":"GitHub"}]}"""));

        var session = await NewClient(api).GetSessionAsync();

        Assert.True(session.Enabled);
        Assert.False(session.Authenticated);
        Assert.Single(session.Providers);
    }
}
