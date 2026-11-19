using System.Net;
using Bunit;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>La page de connexion à un serveur propose SQL Server, PostgreSQL et MySQL.</summary>
public class ClientServerEngineTests : ShellTestBase
{
    public ClientServerEngineTests()
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities") return FakeApi.Json("""{"allowSqlServerSources":true,"allowLocalPaths":false}""");
            if (r.Method == HttpMethod.Post && path == "/api/v1/import/server/test")
                return FakeApi.Json("""{"ok":true,"message":"Connexion réussie : 3 table(s) et 0 vue(s) dans 2 schéma(s).","tables":3,"views":0,"schemas":[{"name":"public","tables":2},{"name":"sales","tables":1}]}""");
            if (path.StartsWith("/api/v1/import/server") && r.Method != HttpMethod.Get)
                return FakeApi.Json("""{"database":{"id":"d7","name":"Pg","type":"PostgreSql","canResync":true},"sync":{"tablesAdded":3,"tablesRemoved":0,"columnsAdded":9}}""",
                    r.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
            return null;
        };
    }

    private IRenderedComponent<ConnectSqlServer> Open(string url = "/import/server")
    {
        Nav.NavigateTo(url);
        var page = Ctx.Render<ConnectSqlServer>();
        page.WaitForAssertion(() => Assert.DoesNotContain("progressbar", page.Markup));
        return page;
    }

    private static void Fill(IRenderedComponent<ConnectSqlServer> page)
    {
        page.Find(".field-server input").Input("pg.exemple.fr");
        page.Find(".field-database input").Input("app");
        page.Find(".field-user input").Input("lecteur");
        page.Find(".field-password input").Input("s3cret");
    }

    [BunitFact]
    public void The_engine_menu_lists_the_three_engines_and_SQL_Server_is_the_default()
    {
        var page = Open();

        var options = page.FindAll(".field-engine select option").Select(o => o.GetAttribute("value")).ToArray();
        Assert.Equal(["SqlServer", "PostgreSql", "MySql"], options);
        Assert.Contains("SQL Server", page.Find("h1, h2, h3, h4, .mud-typography-h4").TextContent);
        Assert.NotEmpty(page.FindAll(".field-auth option[value='Integrated']"));
    }

    [BunitFact]
    public void Choosing_PostgreSQL_adapts_the_title_the_hint_the_port_and_removes_windows_authentication()
    {
        var page = Open();
        page.Find(".field-auth select").Change("Integrated");

        page.Find(".field-engine select").Change("PostgreSql");

        Assert.Contains("PostgreSQL", page.Find(".mud-typography-h4").TextContent);
        Assert.Contains("USAGE", page.Markup);
        Assert.Contains("5432", page.Markup);
        Assert.Empty(page.FindAll(".field-auth option[value='Integrated']"));
        Assert.NotEmpty(page.FindAll(".field-user")); // retombé sur l'identifiant et le mot de passe
    }

    [BunitFact]
    public void A_PostgreSQL_import_goes_through_the_generic_route_with_its_engine_and_lets_schemas_be_chosen()
    {
        var page = Open();
        page.Find(".field-engine select").Change("PostgreSql");
        Fill(page);

        page.Find("button.test-connection").Click();
        page.WaitForAssertion(() => Assert.Equal(["public (2)", "sales (1)"], page.FindAll("#schemas label").Select(l => l.TextContent.Trim()).ToArray()));
        page.Find("button.save-connection").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Post, "/api/v1/import/server")));
        var body = BodyOf(HttpMethod.Post, "/api/v1/import/server");
        Assert.Equal("PostgreSql", body.GetProperty("engine").GetString());
        Assert.Equal("pg.exemple.fr", body.GetProperty("server").GetString());
        Assert.False(WasSent(HttpMethod.Post, "/api/v1/import/sqlserver"));
        page.WaitForAssertion(() => Assert.EndsWith("/databases/d7", Nav.Uri));
    }

    [BunitFact]
    public void MySQL_has_no_schema_choice_and_sends_no_schemas()
    {
        var page = Open();
        page.Find(".field-engine select").Change("MySql");
        Fill(page);

        page.Find("button.test-connection").Click();
        page.WaitForAssertion(() => Assert.Contains("Connexion réussie", page.Find(".connection-test").TextContent));
        Assert.Empty(page.FindAll("#schemas"));
        page.Find("button.save-connection").Click();

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Post, "/api/v1/import/server")));
        var body = BodyOf(HttpMethod.Post, "/api/v1/import/server");
        Assert.Equal("MySql", body.GetProperty("engine").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("schemas").ValueKind);
        Assert.Contains("3306", page.Markup);
    }

    [BunitFact]
    public void The_connection_of_an_existing_PostgreSQL_database_keeps_its_engine_and_cannot_change_it()
    {
        var existing = Override;
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d9"
            ? FakeApi.Json("""{"id":"d9","name":"Pg","type":"PostgreSql","canResync":true,"sourceLabel":"pg:5432 / app"}""")
            : existing(r);

        var page = Open("/import/server?id=d9");

        Assert.Empty(page.FindAll(".field-engine"));
        Assert.Contains("Modifier la connexion de Pg", page.Markup);
        Fill(page);
        page.Find("button.save-connection").Click();
        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/import/server/d9")));
        Assert.Equal("PostgreSql", BodyOf(HttpMethod.Put, "/api/v1/import/server/d9").GetProperty("engine").GetString());
    }
}
