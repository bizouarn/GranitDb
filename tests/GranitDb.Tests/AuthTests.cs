using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GranitDB.API.Options;
using GranitDB.API.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GranitDb.Tests;

/// <summary>Règles de rôle et validation de la configuration d'authentification (sans réseau).</summary>
public class AuthConfigurationTests
{
    private static RoleRules Rules() => new()
    {
        Admins = ["octocat", "oidc-corp:boss"], Editors = ["alice"], Readers = ["bob"],
        AdminGroups = ["acme/admins"], EditorGroups = ["acme/dev"], ReaderGroups = ["acme"]
    };

    [Fact]
    public void A_login_listed_in_a_role_gets_it_case_insensitively_and_the_highest_role_wins()
    {
        Assert.Equal(Role.Admin, RoleResolver.Resolve(Rules(), "github", "OctoCat", []));
        Assert.Equal(Role.Editor, RoleResolver.Resolve(Rules(), "github", "alice", []));
        Assert.Equal(Role.Reader, RoleResolver.Resolve(Rules(), "github", "bob", []));
        Assert.Equal(Role.Admin, RoleResolver.Resolve(Rules(), "github", "carol", ["ACME", "acme/dev", "acme/admins"]));
    }

    [Fact]
    public void Groups_give_a_role_and_a_provider_prefixed_login_only_matches_its_provider()
    {
        Assert.Equal(Role.Editor, RoleResolver.Resolve(Rules(), "github", "dave", ["acme/dev"]));
        Assert.Equal(Role.Reader, RoleResolver.Resolve(Rules(), "github", "erin", ["acme"]));
        Assert.Equal(Role.Admin, RoleResolver.Resolve(Rules(), "oidc-corp", "boss", []));
        Assert.Equal(Role.None, RoleResolver.Resolve(Rules(), "github", "boss", []));
    }

    [Fact]
    public void Nobody_else_gets_in_unless_a_default_role_is_configured()
    {
        Assert.Equal(Role.None, RoleResolver.Resolve(Rules(), "github", "mallory", ["other"]));
        var open = Rules();
        open.DefaultRole = "Reader";
        Assert.Equal(Role.Reader, RoleResolver.Resolve(open, "github", "mallory", []));
        Assert.Equal(Role.Admin, RoleResolver.Resolve(open, "github", "octocat", []));
    }

    [Fact]
    public void Enabling_authentication_without_a_provider_or_an_administrator_is_refused_at_startup()
    {
        Assert.Null(Record.Exception(() => AuthSetup.Validate(new AuthOptions())));

        var none = new AuthOptions { Enabled = true, Roles = Rules() };
        Assert.Contains("aucun fournisseur", Assert.Throws<InvalidOperationException>(() => AuthSetup.Validate(none)).Message);

        var noAdmin = new AuthOptions { Enabled = true, GitHub = new GitHubAuthOptions { ClientId = "id", ClientSecret = "s" } };
        Assert.Contains("administrateur", Assert.Throws<InvalidOperationException>(() => AuthSetup.Validate(noAdmin)).Message);

        var reserved = new AuthOptions
        {
            Enabled = true, Roles = Rules(),
            Providers = { ["github"] = new OidcAuthOptions { ClientId = "i", ClientSecret = "s", Authority = "https://x" } }
        };
        Assert.Contains("réservé", Assert.Throws<InvalidOperationException>(() => AuthSetup.Validate(reserved)).Message);

        var ok = new AuthOptions
        {
            Enabled = true, Roles = Rules(),
            Providers = { ["corp"] = new OidcAuthOptions { ClientId = "i", ClientSecret = "s", Authority = "https://x", DisplayName = "Corp SSO" } }
        };
        AuthSetup.Validate(ok);
        Assert.Equal(["corp"], AuthSetup.ProvidersOf(ok).Select(p => p.Id));
        Assert.Equal("Corp SSO", AuthSetup.ProvidersOf(ok).Single().DisplayName);
    }
}

/// <summary>
///     Un faux GitHub (OAuth) et un faux fournisseur OpenID Connect (jetons signés RS256) tournent sur des ports locaux :
///     aucun appel réseau. Le « code » d'autorisation porte l'identité simulée.
/// </summary>
public sealed class FakeIdentityProviders : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public FakeIdentityProviders()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "k1" };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.AddConsole();
        _app = builder.Build();
        MapGitHub(_app);
        MapOidc(_app);
        _app.Start();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseUrl { get; }

    /// <summary>Utilisateurs de GitHub : login → (organisations, équipes « org/équipe »).</summary>
    public Dictionary<string, (string[] Orgs, string[] Teams)> GitHubUsers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["octocat"] = ([], []),
        ["alice"] = ([], []),
        ["bob"] = ([], []),
        ["mallory"] = ([], []),
        ["carol"] = (["acme"], ["acme/dev"])
    };

    public ValueTask DisposeAsync()
    {
        _rsa.Dispose();
        return _app.DisposeAsync();
    }

    private void MapGitHub(WebApplication app)
    {
        app.MapPost("/gh/token", async (HttpRequest r) =>
        {
            var form = await r.ReadFormAsync();
            return Results.Json(new { access_token = "token-" + form["code"], token_type = "bearer", scope = "read:user" });
        });
        string Login(HttpRequest r) => r.Headers.Authorization.ToString().Replace("Bearer token-", "");
        app.MapGet("/gh/api/user", (HttpRequest r) =>
            Results.Json(new { login = Login(r), name = "Nom de " + Login(r), avatar_url = "https://avatars.example/" + Login(r) }));
        app.MapGet("/gh/api/user/orgs", (HttpRequest r) =>
            Results.Json(GitHubUsers[Login(r)].Orgs.Select(o => new { login = o })));
        app.MapGet("/gh/api/user/teams", (HttpRequest r) =>
            Results.Json(GitHubUsers[Login(r)].Teams.Select(t => new { slug = t.Split('/')[1], organization = new { login = t.Split('/')[0] } })));
    }

    /// <summary>Le code porte « login|nonce|groupe,groupe » ; le jeton d'identité qui en sort est signé et porte ce nonce.</summary>
    public static string OidcCode(string login, string nonce, params string[] groups) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}|{nonce}|{string.Join(',', groups)}"));

    private void MapOidc(WebApplication app)
    {
        app.MapGet("/oidc/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = BaseUrl + "/oidc",
            authorization_endpoint = BaseUrl + "/oidc/authorize",
            token_endpoint = BaseUrl + "/oidc/token",
            jwks_uri = BaseUrl + "/oidc/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" }
        }));
        app.MapGet("/oidc/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "k1" });
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "k1", alg = "RS256", n = jwk.N, e = jwk.E } } });
        });
        app.MapPost("/oidc/token", async (HttpRequest r) =>
        {
            var form = await r.ReadFormAsync();
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(form["code"]!)).Split('|');
            var claims = new Dictionary<string, object>
            {
                ["sub"] = "sub-" + parts[0], ["preferred_username"] = parts[0], ["name"] = "Nom de " + parts[0],
                ["nonce"] = parts[1], ["groups"] = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
            };
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = BaseUrl + "/oidc", Audience = "oidc-client", Claims = claims,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)
            });
            return Results.Json(new { id_token = token, access_token = "at", token_type = "Bearer", expires_in = 300 });
        });
    }
}

/// <summary>Connexion, session, rôles et protection des routes de l'API (flux complets contre les faux fournisseurs).</summary>
public sealed class AuthFlowTests : IAsyncLifetime
{
    private const string V1 = "/api/v1";
    private const string ClientOrigin = "http://localhost:41248";
    private readonly TestCatalog _env = new();
    private readonly FakeIdentityProviders _providers = new();
    private KestrelApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data"),
            ["Auth:Enabled"] = "true",
            ["Auth:GitHub:ClientId"] = "gh-client",
            ["Auth:GitHub:ClientSecret"] = "gh-secret",
            ["Auth:GitHub:AuthorizationEndpoint"] = _providers.BaseUrl + "/gh/authorize",
            ["Auth:GitHub:TokenEndpoint"] = _providers.BaseUrl + "/gh/token",
            ["Auth:GitHub:ApiBase"] = _providers.BaseUrl + "/gh/api",
            ["Auth:Providers:corp:DisplayName"] = "SSO d'entreprise",
            ["Auth:Providers:corp:Authority"] = _providers.BaseUrl + "/oidc",
            ["Auth:Providers:corp:ClientId"] = "oidc-client",
            ["Auth:Providers:corp:ClientSecret"] = "oidc-secret",
            ["Auth:Providers:corp:RequireHttpsMetadata"] = "false",
            ["Auth:Roles:Admins:0"] = "octocat",
            ["Auth:Roles:Editors:0"] = "alice",
            ["Auth:Roles:Readers:0"] = "bob",
            ["Auth:Roles:EditorGroups:0"] = "acme/dev",
            ["Auth:Roles:AdminGroups:0"] = "corp-admins",
            ["Cors:AllowedOrigins:0"] = ClientOrigin
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _providers.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    private HttpClient NewBrowser()
    {
        // Un navigateur : garde les cookies, ne suit pas les redirections (on les lit).
        var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri(_factory.BaseUrl) };
    }

    private static HttpRequestMessage Write(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-GranitDb-Client", "1");
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }

    /// <summary>Joue la connexion GitHub : adresse de connexion → fournisseur → retour avec le « code » (le login simulé).</summary>
    private async Task<HttpResponseMessage> SignInGitHubAsync(HttpClient browser, string login, string returnUrl = "/")
    {
        var start = await browser.GetAsync($"{V1}/auth/login/github?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(_providers.BaseUrl + "/gh/authorize", authorize.AbsoluteUri);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("gh-client", query["client_id"]);
        return await browser.GetAsync($"/auth/callback/github?code={login}&state={Uri.EscapeDataString(query["state"]!)}");
    }

    private static async Task<JsonElement> MeAsync(HttpClient browser) =>
        await (await browser.GetAsync($"{V1}/auth/me")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Without_a_session_the_api_answers_401_but_the_session_route_says_how_to_sign_in()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"{V1}/TagInfo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(Write(HttpMethod.Post, $"{V1}/TagInfo", new { name = "x" }))).StatusCode);

        var me = await MeAsync(browser);
        Assert.True(me.GetProperty("enabled").GetBoolean());
        Assert.False(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal(["github", "corp"], me.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray());
        Assert.Contains(me.GetProperty("providers").EnumerateArray(), p => p.GetProperty("displayName").GetString() == "SSO d'entreprise");
    }

    [Fact]
    public async Task Signing_in_with_GitHub_opens_a_session_with_the_configured_role_and_returns_to_the_interface()
    {
        using var browser = NewBrowser();

        var back = await SignInGitHubAsync(browser, "octocat", ClientOrigin + "/databases");

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal(ClientOrigin + "/databases", back.Headers.Location!.AbsoluteUri);
        var me = await MeAsync(browser);
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal("octocat", me.GetProperty("login").GetString());
        Assert.Equal("Nom de octocat", me.GetProperty("name").GetString());
        Assert.Equal("https://avatars.example/octocat", me.GetProperty("avatarUrl").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"{V1}/DatabaseInfo")).StatusCode);
    }

    [Fact]
    public async Task A_return_address_outside_the_declared_origins_is_ignored()
    {
        using var browser = NewBrowser();

        var back = await SignInGitHubAsync(browser, "octocat", "https://evil.example/phish");

        Assert.Equal("/", back.Headers.Location!.OriginalString);
        using var other = NewBrowser();
        var protocolRelative = await SignInGitHubAsync(other, "octocat", "//evil.example");
        Assert.Equal("/", protocolRelative.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_account_without_a_role_is_sent_back_with_an_error_and_gets_no_session()
    {
        using var browser = NewBrowser();

        var back = await SignInGitHubAsync(browser, "mallory", ClientOrigin + "/");

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal(ClientOrigin + "/?auth_error=refused", back.Headers.Location!.AbsoluteUri);
        Assert.False((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"{V1}/TagInfo")).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_replayed_callback_is_refused()
    {
        using var browser = NewBrowser();

        var back = await browser.GetAsync("/auth/callback/github?code=octocat&state=forged");

        Assert.False((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());
        Assert.NotEqual(HttpStatusCode.OK, back.StatusCode);
    }

    [Fact]
    public async Task Roles_limit_what_a_session_may_do()
    {
        using var reader = NewBrowser();
        await SignInGitHubAsync(reader, "bob");
        using var editor = NewBrowser();
        await SignInGitHubAsync(editor, "alice");
        using var admin = NewBrowser();
        await SignInGitHubAsync(admin, "octocat");

        // Lecteur : lit, n'écrit pas, ne voit ni le journal ni les webhooks ni les sources SQL Server.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{V1}/DatabaseInfo")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(Write(HttpMethod.Post, $"{V1}/TagInfo", new { name = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{V1}/Audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"{V1}/Webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(Write(HttpMethod.Post, $"{V1}/import/sqlserver/test", new { }))).StatusCode);

        // Éditeur : écrit, mais pas d'administration.
        Assert.Equal(HttpStatusCode.Created, (await editor.SendAsync(Write(HttpMethod.Post, $"{V1}/TagInfo", new { name = "important" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{V1}/Audit")).StatusCode);
        // Une synchronisation planifiée connecte l'API à la source toute seule : administrateur seulement.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(Write(HttpMethod.Put, $"{V1}/DatabaseInfo/x/schedule", new { intervalMinutes = 60 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(Write(HttpMethod.Post, $"{V1}/import/server/test", new { }))).StatusCode);
        // Le profilage lit des données de la source : administrateur seulement.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(Write(HttpMethod.Post, $"{V1}/TableInfo/x/profile"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.SendAsync(Write(HttpMethod.Post, $"{V1}/DatabaseInfo/x/profile"))).StatusCode);

        // Administrateur : tout, et le journal nomme l'auteur de l'écriture de l'éditeur.
        var audit = await admin.GetAsync($"{V1}/Audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var entries = (await audit.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("actor").GetString() == "alice" && e.GetProperty("path").GetString() == "/TagInfo" &&
                                      e.GetProperty("status").GetInt32() == 201);
        Assert.Contains(entries, e => e.GetProperty("actor").GetString() == "bob" && e.GetProperty("status").GetInt32() == 403);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{V1}/Webhooks")).StatusCode);
    }

    [Fact]
    public async Task A_write_without_the_client_header_is_refused_even_with_a_valid_session()
    {
        using var browser = NewBrowser();
        await SignInGitHubAsync(browser, "alice");

        var forged = await browser.PostAsJsonAsync($"{V1}/TagInfo", new { name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Fact]
    public async Task GitHub_organisations_and_teams_give_roles_through_the_group_lists()
    {
        using var browser = NewBrowser();

        await SignInGitHubAsync(browser, "carol"); // équipe acme/dev : éditeur

        Assert.Equal("Editor", (await MeAsync(browser)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        using var browser = NewBrowser();
        await SignInGitHubAsync(browser, "bob");
        Assert.True((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());

        var logout = await browser.SendAsync(Write(HttpMethod.Post, $"{V1}/auth/logout"));

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.False((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"{V1}/TagInfo")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_provider_has_no_login_route()
    {
        using var browser = NewBrowser();

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"{V1}/auth/login/gitlab")).StatusCode);
    }

    // --- Bases publiques ---

    private async Task<string> ImportAsAsync(HttpClient browser, string table)
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, name TEXT);");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "x.db");
        content.Add(new StringContent(table + "_db"), "name");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{V1}/import/sqlite") { Content = content };
        request.Headers.Add("X-GranitDb-Client", "1");
        var response = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("database").GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> SetPublicAsync(HttpClient browser, string id, bool isPublic, string name) =>
        browser.SendAsync(Write(HttpMethod.Put, $"{V1}/DatabaseInfo/{id}", new { name, type = "Sqlite", isPublic }));

    [Fact]
    public async Task Public_databases_are_readable_without_an_account_and_nothing_else_is()
    {
        using var admin = NewBrowser();
        await SignInGitHubAsync(admin, "octocat");
        var open = await ImportAsAsync(admin, "catalogue");
        var closed = await ImportAsAsync(admin, "salaires");
        Assert.Equal(HttpStatusCode.OK, (await SetPublicAsync(admin, open, true, "catalogue_db")).StatusCode);
        using var visitor = NewBrowser();

        var me = await MeAsync(visitor);
        Assert.False(me.GetProperty("authenticated").GetBoolean());
        Assert.True(me.GetProperty("allowAnonymousRead").GetBoolean());

        // La liste ne montre que la base publique, sans son origine.
        var list = (await (await visitor.GetAsync($"{V1}/DatabaseInfo")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var shown = Assert.Single(list);
        Assert.Equal(open, shown.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("sourceLabel").ValueKind);
        Assert.False(shown.GetProperty("canResync").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"{V1}/DatabaseInfo/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"{V1}/DatabaseInfo/{open}/export/markdown")).StatusCode);
        var tables = (await (await visitor.GetAsync($"{V1}/TableInfo/database/{open}")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal("catalogue", Assert.Single(tables).GetProperty("name").GetString());
        var tableId = tables[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"{V1}/TableInfo/{tableId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"{V1}/Completeness/database/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"{V1}/DomainInfo/database/{open}")).StatusCode);

        // La base privée, le reste de l'API et toute écriture restent fermés.
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/DatabaseInfo/{closed}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/TableInfo/database/{closed}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/DatabaseInfo/{closed}/export/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/DatabaseInfo/unknown")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/Audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/Webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/History")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/TagInfo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.SendAsync(Write(HttpMethod.Put, $"{V1}/DatabaseInfo/{open}", new { name = "x", type = "Sqlite" }))).StatusCode);

        // La recherche ne sort que des bases publiques.
        Assert.Equal(0, (await (await visitor.GetAsync($"{V1}/Search?q=salaires")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.True((await (await visitor.GetAsync($"{V1}/Search?q=catalogue")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength() > 0);
    }

    [Fact]
    public async Task Only_an_administrator_can_make_a_database_public_and_an_editor_edit_keeps_it_as_it_is()
    {
        using var admin = NewBrowser();
        await SignInGitHubAsync(admin, "octocat");
        using var editor = NewBrowser();
        await SignInGitHubAsync(editor, "alice");
        var id = await ImportAsAsync(admin, "produits");

        // Éditeur : demander « publique » est sans effet.
        Assert.Equal(HttpStatusCode.OK, (await SetPublicAsync(editor, id, true, "produits_db")).StatusCode);
        Assert.False((await (await admin.GetAsync($"{V1}/DatabaseInfo/{id}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isPublic").GetBoolean());

        await SetPublicAsync(admin, id, true, "produits_db");
        // Éditeur qui renomme sans le dire : la base reste publique.
        Assert.Equal(HttpStatusCode.OK, (await SetPublicAsync(editor, id, false, "produits renommée")).StatusCode);
        var after = await (await admin.GetAsync($"{V1}/DatabaseInfo/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("isPublic").GetBoolean());
        Assert.Equal("produits renommée", after.GetProperty("name").GetString());
        // Administrateur : peut la refermer.
        await SetPublicAsync(admin, id, false, "produits renommée");
        using var visitor = NewBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.GetAsync($"{V1}/DatabaseInfo/{id}")).StatusCode);
    }

    // --- Connecteur OpenID Connect générique ---

    private async Task<HttpResponseMessage> SignInOidcAsync(HttpClient browser, string login, string[] groups, string returnUrl = "/")
    {
        var start = await browser.GetAsync($"{V1}/auth/login/corp?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(_providers.BaseUrl + "/oidc/authorize", authorize.AbsoluteUri);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("oidc-client", query["client_id"]);
        Assert.NotNull(query["code_challenge"]); // PKCE
        var code = FakeIdentityProviders.OidcCode(login, query["nonce"]!, groups);
        return await browser.GetAsync($"/auth/callback/corp?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(query["state"]!)}");
    }

    [Fact]
    public async Task A_generic_OpenID_Connect_provider_declared_by_configuration_signs_users_in_with_their_groups_as_roles()
    {
        using var browser = NewBrowser();

        var back = await SignInOidcAsync(browser, "jdupont", ["corp-admins"], ClientOrigin + "/");

        Assert.Equal(ClientOrigin + "/", back.Headers.Location!.AbsoluteUri);
        var me = await MeAsync(browser);
        Assert.Equal("jdupont", me.GetProperty("login").GetString());
        Assert.Equal("Nom de jdupont", me.GetProperty("name").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.Equal("corp", me.GetProperty("provider").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"{V1}/Audit")).StatusCode);
    }

    [Fact]
    public async Task An_OpenID_Connect_user_in_no_listed_group_is_refused()
    {
        using var browser = NewBrowser();

        var back = await SignInOidcAsync(browser, "stranger", ["other"], ClientOrigin + "/");

        Assert.Equal(ClientOrigin + "/?auth_error=refused", back.Headers.Location!.AbsoluteUri);
        Assert.False((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task An_identity_token_bound_to_another_login_attempt_is_refused()
    {
        using var browser = NewBrowser();
        var start = await browser.GetAsync($"{V1}/auth/login/corp");
        var query = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query);
        var code = FakeIdentityProviders.OidcCode("jdupont", "another-nonce", "corp-admins");

        await browser.GetAsync($"/auth/callback/corp?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(query["state"]!)}");

        Assert.False((await MeAsync(browser)).GetProperty("authenticated").GetBoolean());
    }
}

/// <summary>Sans <c>Auth:Enabled</c> (poste de développement) l'API reste ouverte et dit qu'il n'y a pas de connexion.</summary>
public sealed class AuthDisabledTests : IDisposable
{
    private readonly TestCatalog _env = new();
    private readonly KestrelApiFactory _factory;
    private readonly HttpClient _http;

    public AuthDisabledTests()
    {
        _factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-data")
        });
        _http = _factory.CreateKestrelClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        _env.Dispose();
    }

    [Fact]
    public async Task The_api_stays_open_without_a_header_and_the_session_route_reports_it()
    {
        var me = await (await _http.GetAsync("/api/v1/auth/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(me.GetProperty("enabled").GetBoolean());
        Assert.False(me.GetProperty("authenticated").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/v1/Audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _http.PostAsJsonAsync("/api/v1/TagInfo", new { name = "libre" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/v1/auth/login/github")).StatusCode);
    }
}

/// <summary>Secrets de configuration hors fichier et chiffrement des clés au repos.</summary>
public sealed class SecretsConfigurationTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void A_value_can_point_to_an_environment_variable_or_a_secret_file()
    {
        var name = "GRANITDB_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "valeur-secrete");
        var file = _env.NewFile("secret.txt");
        File.WriteAllText(file, "  dans-un-fichier\n");
        try
        {
            Assert.Equal("valeur-secrete", GranitDB.API.Services.SecretReference.Resolve("env:" + name));
            Assert.Equal("dans-un-fichier", GranitDB.API.Services.SecretReference.Resolve("file:" + file));
            Assert.Equal("Server=x;Password=en-clair", GranitDB.API.Services.SecretReference.Resolve("Server=x;Password=en-clair"));
            Assert.True(GranitDB.API.Services.SecretReference.IsReference("ENV:" + name));
            Assert.False(GranitDB.API.Services.SecretReference.IsReference("Server=x"));
            Assert.Contains("absente", Assert.Throws<InvalidOperationException>(() => GranitDB.API.Services.SecretReference.Resolve("env:GRANITDB_TEST_MISSING")).Message);
            Assert.Contains("introuvable", Assert.Throws<InvalidOperationException>(() => GranitDB.API.Services.SecretReference.Resolve("file:" + _env.NewFile("absent.txt"))).Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task A_startup_source_reads_its_connection_from_a_reference_instead_of_the_configuration_file()
    {
        var path = _env.NewFile("source.db");
        TestCatalog.Execute(path, "CREATE TABLE t (id INTEGER PRIMARY KEY);");
        var name = "GRANITDB_TEST_SOURCE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, path);
        try
        {
            var result = await _env.Importer.SyncSourceAsync(new GranitDB.API.Options.StartupSource
                { Id = "demarrage", Type = "Sqlite", Source = "env:" + name, Name = "Au démarrage" });

            Assert.Equal(1, result.TablesAdded);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void An_unknown_key_protection_stops_the_startup()
    {
        using var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api"),
            ["Catalog:KeysProtection"] = "Coffre"
        });

        var error = Assert.ThrowsAny<Exception>(() => _ = factory.Server);

        Assert.Contains("KeysProtection", error.ToString());
    }

    [Fact]
    public async Task Keys_protected_by_a_certificate_are_encrypted_on_disk_and_still_decrypt_secrets()
    {
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=granitdb-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = _env.NewFile("keys.pfx");
        await File.WriteAllBytesAsync(pfx, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pfx-secret"));
        var keys = Path.Combine(_env.Directory, "keys");
        var password = "GRANITDB_TEST_PFX_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(password, "pfx-secret");
        try
        {
            using var factory = new KestrelApiFactory(new Dictionary<string, string>
            {
                ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api"),
                ["Catalog:KeysDirectory"] = keys,
                ["Catalog:KeysProtection"] = "Certificate",
                ["Catalog:KeysCertificatePath"] = pfx,
                ["Catalog:KeysCertificatePassword"] = "env:" + password
            });
            _ = factory.Server;
            var protector = factory.Services.GetRequiredService<GranitDB.API.Services.ISecretProtector>();

            var sealedText = protector.Protect("mot-de-passe-sql");

            Assert.Equal("mot-de-passe-sql", protector.Unprotect(sealedText));
            var key = Directory.GetFiles(keys, "*.xml").Select(File.ReadAllText).First(x => x.Contains("<key "));
            Assert.Contains("EncryptedData", key);
            Assert.DoesNotContain("<masterKey", key);
        }
        finally
        {
            Environment.SetEnvironmentVariable(password, null);
            SqliteConnection.ClearAllPools();
        }
    }
}
