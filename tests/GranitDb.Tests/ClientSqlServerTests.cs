using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Layout;
using GranitDb.Client.Pages;
using GranitDb.Client.Workspace;
using ApiException = GranitDb.Client.api.ApiException;

namespace GranitDb.Tests;

/// <summary>Client HTTP : capacités et connexion à un serveur SQL Server.</summary>
public class ClientSqlServerApiTests
{
    private static GranitDb.Client.Models.SqlServerConnection Connection() => new()
    {
        Server = "srv", Port = 1444, Database = "Boutique", User = "lecteur", Password = "s3cret", Schemas = ["dbo"], Name = "Distante"
    };

    [Fact]
    public async Task Capabilities_are_read_and_default_to_nothing_allowed_when_unavailable()
    {
        var on = new FakeApi { Respond = _ => FakeApi.Json("""{"allowSqlServerSources":true,"allowLocalPaths":false}""") };
        var missing = new FakeApi();
        var down = new FakeApi { Respond = _ => throw new HttpRequestException("refused") };
        var garbage = new FakeApi { Respond = _ => FakeApi.Json("[1,2]") };

        Assert.True((await on.Client.GetCapabilitiesAsync()).AllowSqlServerSources);
        Assert.Equal("http://api.test/api/v1/Capabilities", on.Requests.Single().RequestUri!.ToString());
        Assert.False((await missing.Client.GetCapabilitiesAsync()).AllowSqlServerSources);
        Assert.False((await down.Client.GetCapabilitiesAsync()).AllowSqlServerSources);
        Assert.False((await garbage.Client.GetCapabilitiesAsync()).AllowSqlServerSources);
    }

    [Fact]
    public async Task A_test_posts_the_connection_and_reads_the_result_including_a_failure()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"ok":false,"message":"Identifiants refusés par le serveur","tables":0,"views":0,"schemas":[]}""")
        };

        var result = await api.Client.TestSqlServerAsync(Connection());

        Assert.False(result.Ok);
        Assert.Contains("Identifiants refusés", result.Message);
        Assert.Equal("http://api.test/api/v1/import/sqlserver/test", api.Requests.Single().RequestUri!.ToString());
        using var body = JsonDocument.Parse(api.Bodies.Single());
        Assert.Equal("srv", body.RootElement.GetProperty("server").GetString());
        Assert.Equal(1444, body.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("lecteur", body.RootElement.GetProperty("user").GetString());
        Assert.Equal("Sql", body.RootElement.GetProperty("authMode").GetString());
        Assert.True(body.RootElement.GetProperty("encrypt").GetBoolean());
        Assert.Equal("dbo", body.RootElement.GetProperty("schemas")[0].GetString());
    }

    [Fact]
    public async Task A_test_refused_by_the_api_raises_its_message()
    {
        var disabled = new FakeApi { Respond = _ => FakeApi.Json("""{"title":"Les sources SQL Server sont désactivées","status":400}""", HttpStatusCode.BadRequest) };
        var invalid = new FakeApi { Respond = _ => FakeApi.Json("""{"title":"One or more validation errors occurred.","status":400,"errors":{"Server":["Le serveur est obligatoire."]}}""", HttpStatusCode.BadRequest) };

        Assert.Contains("désactivées", (await Assert.ThrowsAsync<ApiException>(() => disabled.Client.TestSqlServerAsync(Connection()))).Message);
        Assert.Equal("Le serveur est obligatoire.", (await Assert.ThrowsAsync<ApiException>(() => invalid.Client.TestSqlServerAsync(Connection()))).Message);
    }

    [Fact]
    public async Task Import_and_replacement_use_their_endpoints_and_escape_the_identifier()
    {
        var api = new FakeApi
        {
            Respond = _ => FakeApi.Json("""{"database":{"id":"d5","name":"Distante","type":"SqlServer","canResync":true,"sourceLabel":"srv:1444 / Boutique"},"sync":{"tablesAdded":3,"columnsAdded":9}}""", HttpStatusCode.Created)
        };

        var imported = await api.Client.ImportSqlServerAsync(Connection());
        await api.Client.UpdateSqlServerConnectionAsync("a/b c", Connection());

        Assert.Equal("d5", imported.Database.Id);
        Assert.True(imported.Database.CanResync);
        Assert.Equal("srv:1444 / Boutique", imported.Database.SourceLabel);
        Assert.Equal(3, imported.Sync.TablesAdded);
        Assert.Equal(HttpMethod.Post, api.Requests[0].Method);
        Assert.Equal("http://api.test/api/v1/import/sqlserver", api.Requests[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, api.Requests[1].Method);
        Assert.Equal("http://api.test/api/v1/import/sqlserver/a%2Fb%20c", api.Requests[1].RequestUri!.AbsoluteUri);
    }
}

/// <summary>La page « Connecter un serveur SQL Server ».</summary>
public class ClientConnectSqlServerTests : ShellTestBase
{
    private bool _enabled = true;
    private string _testResult = """
        {"ok":true,"message":"Connexion réussie : 2 table(s) et 1 vue(s) dans 2 schéma(s).","tables":2,"views":1,
         "schemas":[{"name":"dbo","tables":2},{"name":"sales","tables":1}]}
        """;
    private HttpResponseMessage? _importResponse;

    public ClientConnectSqlServerTests()
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities") return FakeApi.Json($$"""{"allowSqlServerSources":{{(_enabled ? "true" : "false")}},"allowLocalPaths":false}""");
            if (r.Method == HttpMethod.Post && path == "/api/v1/import/sqlserver/test") return FakeApi.Json(_testResult);
            if (path.StartsWith("/api/v1/import/sqlserver") && r.Method != HttpMethod.Get)
                return _importResponse ?? FakeApi.Json("""{"database":{"id":"d5","name":"Boutique","type":"SqlServer","canResync":true},"sync":{"tablesAdded":3,"tablesRemoved":1,"columnsAdded":9}}""", HttpStatusCode.Created);
            return null;
        };
    }

    private IRenderedComponent<ConnectSqlServer> Open(string? query = null)
    {
        Nav.NavigateTo("/import/sqlserver" + query);
        var page = Ctx.Render<ConnectSqlServer>();
        page.WaitForAssertion(() => Assert.DoesNotContain("progressbar", page.Markup));
        return page;
    }

    private static void Fill(IRenderedComponent<ConnectSqlServer> page, string server = "srv", string database = "Boutique", string user = "lecteur", string password = "s3cret")
    {
        page.Find(".field-server input").Input(server);
        page.Find(".field-database input").Input(database);
        if (user != "")
        {
            page.Find(".field-user input").Input(user);
            page.Find(".field-password input").Input(password);
        }
    }

    private static AngleSharp.Dom.IElement TestButton(IRenderedComponent<ConnectSqlServer> page) => page.Find("button.test-connection");

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<ConnectSqlServer> page) => page.Find("button.save-connection");

    private JsonElement Body(string method, string path) => BodyOf(method == "PUT" ? HttpMethod.Put : HttpMethod.Post, path);

    [BunitFact]
    public void When_the_sources_are_disabled_the_page_explains_how_to_enable_them_and_shows_no_form()
    {
        _enabled = false;

        var page = Open();

        Assert.Contains("Catalog:AllowSqlServerSources", page.Find(".sqlserver-disabled").TextContent);
        Assert.Empty(page.FindAll(".field-server"));
        Assert.Empty(page.FindAll("button.test-connection"));
    }

    [BunitFact]
    public void The_buttons_wait_for_the_required_fields()
    {
        var page = Open();
        Assert.True(TestButton(page).HasAttribute("disabled"));
        Assert.True(SaveButton(page).HasAttribute("disabled"));

        page.Find(".field-server input").Input("srv");
        page.Find(".field-database input").Input("Boutique");
        Assert.True(TestButton(page).HasAttribute("disabled")); // l'authentification SQL veut aussi identifiant et mot de passe
        page.Find(".field-user input").Input("lecteur");
        page.Find(".field-password input").Input("s3cret");

        Assert.False(TestButton(page).HasAttribute("disabled"));
        Assert.False(SaveButton(page).HasAttribute("disabled"));
    }

    [BunitFact]
    public void Windows_authentication_needs_no_credentials()
    {
        var page = Open();

        page.Find(".field-auth select").Change("Integrated");
        page.Find(".field-server input").Input("srv");
        page.Find(".field-database input").Input("Boutique");

        Assert.Empty(page.FindAll(".field-user"));
        Assert.Empty(page.FindAll(".field-password"));
        Assert.False(TestButton(page).HasAttribute("disabled"));
    }

    [BunitFact]
    public void The_password_field_is_masked_and_an_unencrypted_connection_is_warned_about()
    {
        var page = Open();

        Assert.Equal("password", page.Find(".field-password input").GetAttribute("type"));
        Assert.Empty(page.FindAll(".unencrypted-warning"));
        page.Find(".field-encrypt input").Change(false);

        Assert.Contains("en clair", page.Find(".unencrypted-warning").TextContent);
    }

    [BunitFact]
    public void A_successful_test_shows_what_would_be_documented_and_lets_the_schemas_be_chosen()
    {
        var page = Open();
        Fill(page);

        TestButton(page).Click();

        page.WaitForAssertion(() => Assert.Contains("Connexion réussie", page.Find(".connection-test").TextContent));
        Assert.Equal(["dbo (2)", "sales (1)"], page.FindAll("#schemas label").Select(l => l.TextContent.Trim()).ToArray());
        Assert.All(page.FindAll("#schemas input[type=checkbox]"), c => Assert.True(c.HasAttribute("checked")));
        Assert.Equal("srv", BodyOf(HttpMethod.Post, "/api/v1/import/sqlserver/test").GetProperty("server").GetString());
    }

    [BunitFact]
    public void A_failed_test_shows_the_instruction_and_no_schema_list()
    {
        _testResult = """{"ok":false,"message":"Identifiants refusés par le serveur : vérifiez l'identifiant, le mot de passe et le mode d'authentification.","tables":0,"views":0,"schemas":[]}""";
        var page = Open();
        Fill(page);

        TestButton(page).Click();

        page.WaitForAssertion(() => Assert.Contains("Identifiants refusés", page.Find(".connection-test").TextContent));
        Assert.Contains("mud-alert-text-error", page.Find(".connection-test").ClassName); // une erreur, pas un succès
        Assert.Empty(page.FindAll("#schemas"));
    }

    [BunitFact]
    public void A_request_refused_by_the_api_shows_its_message()
    {
        var overrideBefore = Override;
        Override = r => r.RequestUri!.AbsolutePath.EndsWith("/test")
            ? FakeApi.Json("""{"title":"Les sources SQL Server sont désactivées","status":400}""", HttpStatusCode.BadRequest)
            : overrideBefore(r);
        var page = Open();
        Fill(page);

        TestButton(page).Click();

        page.WaitForAssertion(() => Assert.Contains("désactivées", page.Find(".connection-error").TextContent));
        Assert.Empty(page.FindAll(".connection-test"));
    }

    [BunitFact]
    public void An_unreachable_api_is_reported()
    {
        var overrideBefore = Override;
        Override = r => r.RequestUri!.AbsolutePath.EndsWith("/test") ? throw new HttpRequestException("refused") : overrideBefore(r);
        var page = Open();
        Fill(page);

        TestButton(page).Click();

        page.WaitForAssertion(() => Assert.Contains("L'API est injoignable", page.Find(".connection-error").TextContent));
    }

    [BunitFact]
    public void Importing_sends_the_form_confirms_with_what_was_found_and_opens_the_database()
    {
        var seen = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => seen.Add((db, source));
        var page = Open();
        Fill(page);
        page.Find(".field-name input").Input("Distante");

        SaveButton(page).Click();

        page.WaitForAssertion(() => Assert.EndsWith("/databases/d5", Nav.Uri));
        var body = BodyOf(HttpMethod.Post, "/api/v1/import/sqlserver");
        Assert.Equal("srv", body.GetProperty("server").GetString());
        Assert.Equal("Boutique", body.GetProperty("database").GetString());
        Assert.Equal("lecteur", body.GetProperty("user").GetString());
        Assert.Equal("s3cret", body.GetProperty("password").GetString());
        Assert.Equal("Distante", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("encrypt").GetBoolean());
        Assert.False(body.GetProperty("trustServerCertificate").GetBoolean());
        Assert.Contains(Notices.Toasts, t => t.Message == "Base Boutique importée : 3 table(s), 9 colonne(s).");
        Assert.Contains(seen, s => s.Item1 == null && s.Item2 != null);
    }

    [BunitFact]
    public void After_a_test_all_schemas_ticked_means_no_restriction_and_a_choice_is_sent_as_a_list()
    {
        var page = Open();
        Fill(page);
        TestButton(page).Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#schemas")));

        SaveButton(page).Click();
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Post, "/api/v1/import/sqlserver")));
        Assert.Equal(JsonValueKind.Null, BodyOf(HttpMethod.Post, "/api/v1/import/sqlserver").GetProperty("schemas").ValueKind);

        Nav.NavigateTo("/import/sqlserver");
        var second = Open();
        Fill(second);
        TestButton(second).Click();
        second.WaitForAssertion(() => Assert.NotEmpty(second.FindAll("#schemas")));
        second.FindAll("#schemas label").First(l => l.TextContent.Contains("dbo")).QuerySelector("input")!.Change(false);
        SaveButton(second).Click();

        second.WaitForAssertion(() => Assert.Equal(2, Requests(HttpMethod.Post, "/api/v1/import/sqlserver").Count()));
        var schemas = BodyOf(HttpMethod.Post, "/api/v1/import/sqlserver").GetProperty("schemas");
        Assert.Equal(["sales"], schemas.EnumerateArray().Select(s => s.GetString()!).ToArray());
    }

    [BunitFact]
    public void Unticking_every_schema_is_refused_before_calling_the_api()
    {
        var page = Open();
        Fill(page);
        TestButton(page).Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#schemas")));
        foreach (var box in page.FindAll("#schemas input[type=checkbox]")) box.Change(false);

        SaveButton(page).Click();

        Assert.Contains("Cochez au moins un schéma", page.Find(".connection-error").TextContent);
        Assert.False(WasSent(HttpMethod.Post, "/api/v1/import/sqlserver"));
    }

    [BunitFact]
    public void A_refused_import_shows_the_message_and_stays_on_the_form()
    {
        _importResponse = FakeApi.Json("""{"title":"Aucune table à documenter : vérifiez les schémas choisis","status":400}""", HttpStatusCode.BadRequest);
        var page = Open();
        Fill(page);

        SaveButton(page).Click();

        page.WaitForAssertion(() => Assert.Contains("Aucune table à documenter", page.Find(".connection-error").TextContent));
        Assert.DoesNotContain("/databases/", Nav.Uri);
        Assert.NotEmpty(page.FindAll(".field-server"));
        Assert.Empty(Notices.Toasts);
    }

    [BunitFact]
    public void The_tab_is_titled_for_the_workspace()
    {
        Open();

        Assert.Equal("Connecter SQL Server", State.Tabs.Single(t => t.Href == "/import/sqlserver").Title);
    }

    // --- Modifier la connexion d'une base ---

    [BunitFact]
    public void Editing_a_connection_replaces_it_without_asking_for_the_name_again()
    {
        var seen = new List<(string?, object?)>();
        State.CatalogChanged += (db, source) => seen.Add((db, source));
        var page = Open("?id=d2");
        page.WaitForAssertion(() => Assert.Contains("Modifier la connexion de RH", page.Markup));
        Assert.Empty(page.FindAll(".field-name"));
        Assert.Contains("Enregistrer la connexion", SaveButton(page).TextContent);
        Fill(page, server: "autre-srv", database: "RH");

        SaveButton(page).Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/import/sqlserver/d2")));
        Assert.Equal("autre-srv", BodyOf(HttpMethod.Put, "/api/v1/import/sqlserver/d2").GetProperty("server").GetString());
        Assert.False(WasSent(HttpMethod.Post, "/api/v1/import/sqlserver"));
        Assert.Contains(Notices.Toasts, t => t.Message.StartsWith("Connexion enregistrée et base resynchronisée"));
        Assert.Contains(seen, s => s.Item1 == "d2");
        Assert.Equal("Connexion · RH", State.Tabs.Single(t => t.Href == "/import/sqlserver?id=d2").Title);
    }

    [BunitFact]
    public void Editing_the_connection_of_an_unknown_database_says_so()
    {
        var page = Open("?id=inconnue");

        page.WaitForAssertion(() => Assert.Contains("Base introuvable", page.Markup));
        Assert.Empty(page.FindAll(".field-server"));
    }
}

/// <summary>L'accueil, la palette et la page d'une base face aux sources SQL Server.</summary>
public class ClientSqlServerWorkspaceTests : ShellTestBase
{
    [BunitFact]
    public void The_home_page_offers_the_connection_only_when_the_server_allows_it()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Capabilities" ? FakeApi.Json("""{"allowSqlServerSources":true,"allowLocalPaths":false}""") : null;
        var enabled = Ctx.Render<Databases>();
        enabled.WaitForAssertion(() => Assert.Contains("Boutique", enabled.Markup));
        Assert.Equal("/import/sqlserver", enabled.Find("a.connect-sqlserver").GetAttribute("href"));
        Assert.Empty(enabled.FindAll(".sqlserver-disabled"));

        Override = _ => null;
        var disabled = Ctx.Render<Databases>();
        disabled.WaitForAssertion(() => Assert.Contains("Boutique", disabled.Markup));
        Assert.Empty(disabled.FindAll("a.connect-sqlserver"));
        Assert.Contains("Catalog:AllowSqlServerSources", disabled.Find(".sqlserver-disabled").TextContent);
    }

    [BunitFact]
    public void The_command_palette_offers_to_connect_a_server()
    {
        var palette = Ctx.Render<CommandPalette>();
        State.SetPalette(true);
        palette.WaitForAssertion(() => Assert.Contains("Ouvrir Boutique", palette.Markup));

        palette.FindAll(".palette-item").First(i => i.QuerySelector(".palette-label")!.TextContent == "Connecter un serveur SQL Server").Click();

        Assert.EndsWith("/import/sqlserver", Nav.Uri);
    }

    private const string SqlServerDatabase = """{"id":"d1","name":"Distante","type":"SqlServer","canResync":true,"sourceLabel":"srv:1444 / Boutique"}""";

    [BunitFact]
    public void A_database_with_a_stored_connection_shows_its_source_and_can_be_edited_and_resynchronized()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1" ? FakeApi.Json(SqlServerDatabase) : null;

        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));

        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        Assert.Contains("srv:1444 / Boutique", page.Find(".source-label").TextContent);
        Assert.Equal("/import/sqlserver?id=d1", page.Find("a.edit-connection").GetAttribute("href"));
        Assert.Contains(page.FindAll("button"), b => b.TextContent.Contains("Resynchroniser"));
    }

    [BunitFact]
    public void A_file_database_has_no_connection_to_edit_and_a_database_that_cannot_be_reread_has_no_resync_button()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1"
            ? FakeApi.Json("""{"id":"d1","name":"Fichier","type":"Sqlite","canResync":true}""")
            : r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d2" ? FakeApi.Json("""{"id":"d2","name":"Figée","type":"SqlServer","canResync":false}""") : null;

        var file = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        file.WaitForAssertion(() => Assert.Contains("customers", file.Markup));
        Assert.Empty(file.FindAll("a.edit-connection"));
        Assert.Empty(file.FindAll(".source-label"));
        Assert.Contains(file.FindAll("button"), b => b.TextContent.Contains("Resynchroniser"));

        var frozen = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d2"));
        frozen.WaitForAssertion(() => Assert.Contains("Figée", frozen.Markup));
        Assert.Empty(frozen.FindAll("a.edit-connection"));
        Assert.DoesNotContain(frozen.FindAll("button"), b => b.TextContent.Contains("Resynchroniser"));
    }
}
