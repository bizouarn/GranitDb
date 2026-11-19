using System.Net;
using Bunit;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Profil des colonnes dans la page d'une table : lecture, calcul à la demande, droits.</summary>
public class ClientProfilingTests : ShellTestBase
{
    private const string Profile = """
        {"tableId":"t1","rowCount":1200,"profiledAt":"2026-11-19T10:00:00Z","columns":[
          {"columnId":"c1","column":"id","rowCount":1200,"nullCount":0,"nullRatio":0,"distinctCount":1200,"kind":"unique","profiledAt":"2026-11-19T10:00:00Z"},
          {"columnId":"c2","column":"email","rowCount":1200,"nullCount":300,"nullRatio":0.25,"distinctCount":850,"kind":"high","profiledAt":"2026-11-19T10:00:00Z"}]}
        """;

    private const string NoProfile = """{"tableId":"t1","rowCount":null,"profiledAt":null,"columns":[]}""";

    private void Serve(bool allowProfiling, string profile, string session = "")
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities")
                return FakeApi.Json($$"""{"allowSqlServerSources":false,"allowLocalPaths":false,"allowProfiling":{{allowProfiling.ToString().ToLowerInvariant()}}}""");
            if (path == "/api/v1/TableInfo/t1/profile") return FakeApi.Json(profile);
            if (session != "" && path == "/api/v1/Auth/me") return FakeApi.Json(session);
            return null;
        };
    }

    private IRenderedComponent<DatabaseDetail> OpenTable()
    {
        Nav.NavigateTo("/databases/d1/tables/t1");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1").Add(x => x.TableRoute, "t1"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-column]")));
        return page;
    }

    [BunitFact]
    public void A_profiled_table_shows_nulls_and_distinct_values_per_column_with_the_kind()
    {
        Serve(false, Profile);

        var page = OpenTable();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("td.column-profile")));
        var id = page.Find("tr[data-column='id'] td.column-profile");
        Assert.Contains("aucun nul", id.TextContent);
        Assert.Contains("1 200 valeurs", id.TextContent.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("unique", id.QuerySelector(".profile-kind")!.TextContent.Trim());
        var email = page.Find("tr[data-column='email'] td.column-profile");
        Assert.Contains("25 % de nuls", email.TextContent);
        Assert.Contains("1 200 ligne(s)", page.Find(".profile-summary").TextContent.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Empty(page.FindAll(".profile-table")); // pas de droit de profiler : lecture seule
    }

    [BunitFact]
    public void A_table_never_profiled_has_no_profile_column_and_offers_to_profile_when_allowed()
    {
        Serve(true, NoProfile);

        var page = OpenTable();

        page.WaitForAssertion(() => Assert.Equal("Profiler la table", page.Find(".profile-table").TextContent.Trim()));
        Assert.Empty(page.FindAll("td.column-profile"));
    }

    [BunitFact]
    public void Profiling_is_not_offered_when_the_server_does_not_allow_it()
    {
        Serve(false, NoProfile);

        var page = OpenTable();

        Assert.Empty(page.FindAll(".profile-table"));
        Assert.Empty(page.FindAll(".profile-database"));
    }

    [BunitFact]
    public void Clicking_profile_computes_it_on_the_server_and_shows_the_result()
    {
        Serve(true, NoProfile);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/t1/profile"
            ? FakeApi.Json(Profile)
            : inner(r);
        var page = OpenTable();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".profile-table")));

        page.Find(".profile-table").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("td.column-profile")));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/TableInfo/t1/profile"));
        Assert.Equal("Actualiser le profil", page.Find(".profile-table").TextContent.Trim());
    }

    [BunitFact]
    public void A_refusal_is_shown_next_to_the_table_with_the_servers_reason()
    {
        Serve(true, NoProfile);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/t1/profile"
            ? FakeApi.Json("""{"title":"Profilage de customers impossible : no such table","status":400}""", HttpStatusCode.BadRequest)
            : inner(r);
        var page = OpenTable();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".profile-table")));

        page.Find(".profile-table").Click();

        page.WaitForAssertion(() => Assert.Contains("no such table", page.Markup));
    }

    [BunitFact]
    public void Only_an_administrator_sees_the_profile_buttons_when_authentication_is_on()
    {
        Serve(true, NoProfile, """{"enabled":true,"authenticated":true,"login":"bob","role":"Editor","providers":[]}""");
        Ctx.Services.GetService(typeof(GranitDb.Client.Workspace.AuthState));
        ((GranitDb.Client.Workspace.AuthState)Ctx.Services.GetService(typeof(GranitDb.Client.Workspace.AuthState))!).LoadAsync().GetAwaiter().GetResult();

        var page = OpenTable();

        Assert.Empty(page.FindAll(".profile-table"));
    }
}
