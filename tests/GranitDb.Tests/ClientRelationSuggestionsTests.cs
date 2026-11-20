using System.Net;
using Bunit;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Écran « Relations suggérées » : lecture, détection, acceptation, refus.</summary>
public class ClientRelationSuggestionsTests : ShellTestBase
{
    private const string One = """
        [{"id":"s1","databaseId":"d1","childTable":"orders","childColumn":"customer_id","childColumnId":"c1","parentTable":"customers",
          "parentColumn":"id","parentColumnId":"c2","score":95,"level":"probable","reasons":["Le nom `customer_id` désigne la table `customers`.","100 % des 40 valeurs existent dans `customers.id`."],
          "nonNullCount":40,"orphanCount":0,"status":"pending","detectedAt":"2026-11-19T10:00:00Z"}]
        """;

    private void Serve(string suggestions, bool allowProfiling = true)
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities")
                return FakeApi.Json($$"""{"allowSqlServerSources":false,"allowLocalPaths":false,"allowProfiling":{{allowProfiling.ToString().ToLowerInvariant()}}}""");
            if (r.Method == HttpMethod.Get && path == "/api/v1/DatabaseInfo/d1/relation-suggestions") return FakeApi.Json(suggestions);
            return null;
        };
    }

    private IRenderedComponent<RelationSuggestions> Open()
    {
        Nav.NavigateTo("/databases/d1/relations");
        return Ctx.Render<RelationSuggestions>(p => p.Add(x => x.Id, "d1"));
    }

    [BunitFact]
    public void Suggestions_are_listed_with_their_score_and_their_reasons()
    {
        Serve(One);

        var page = Open();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-suggestion]")));
        var row = page.Find("tr[data-suggestion='s1']");
        Assert.Contains("orders.customer_id", row.TextContent);
        Assert.Contains("customers.id", row.TextContent);
        Assert.Contains("probable (95)", row.TextContent);
        Assert.Contains("100 % des 40 valeurs", row.TextContent);
    }

    [BunitFact]
    public void Accepting_creates_the_relation_and_removes_the_row()
    {
        Serve(One);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1/relation-suggestions/s1/accept"
            ? FakeApi.Json("""{"id":"r1","firstId":"c2","secondId":"c1","relationType":"One-to-Many","metaInfo":"detected","extraColumns":[]}""")
            : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".accept-suggestion")));

        page.Find(".accept-suggestion").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("tr[data-suggestion]")));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/DatabaseInfo/d1/relation-suggestions/s1/accept"));
    }

    [BunitFact]
    public void Rejecting_removes_the_row_and_tells_the_server()
    {
        Serve(One);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1/relation-suggestions/s1/reject"
            ? FakeApi.Json("""{"id":"s1","databaseId":"d1","childTable":"orders","childColumn":"customer_id","childColumnId":"c1","parentTable":"customers","parentColumn":"id","parentColumnId":"c2","score":95,"level":"probable","reasons":[],"nonNullCount":40,"orphanCount":0,"status":"rejected","detectedAt":"2026-11-19T10:00:00Z"}""")
            : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".reject-suggestion")));

        page.Find(".reject-suggestion").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("tr[data-suggestion]")));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/DatabaseInfo/d1/relation-suggestions/s1/reject"));
    }

    [BunitFact]
    public void Detecting_runs_the_detection_then_reloads_the_list()
    {
        var served = "[]";
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities") return FakeApi.Json("""{"allowSqlServerSources":false,"allowLocalPaths":false,"allowProfiling":true}""");
            if (r.Method == HttpMethod.Post && path == "/api/v1/DatabaseInfo/d1/relation-suggestions/detect")
            {
                served = One;
                return FakeApi.Json("""{"candidates":3,"checked":3,"suggested":1,"failed":0,"skipped":0,"errors":[]}""");
            }

            return r.Method == HttpMethod.Get && path == "/api/v1/DatabaseInfo/d1/relation-suggestions" ? FakeApi.Json(served) : null;
        };
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".detect-relations")));
        Assert.NotEmpty(page.FindAll(".suggestions-empty"));

        page.Find(".detect-relations").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-suggestion]")));
        Assert.Contains("3 candidat(s)", page.Find(".detect-summary").TextContent);
    }

    [BunitFact]
    public void Detection_is_not_offered_when_profiling_is_off_and_a_refusal_shows_the_reason()
    {
        Serve("[]", false);

        var page = Open();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".suggestions-empty")));
        Assert.Empty(page.FindAll(".detect-relations"));

        Serve("[]");
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1/relation-suggestions/detect"
            ? FakeApi.Json("""{"title":"La détection des relations est désactivée (Catalog:AllowProfiling = false)","status":400}""", HttpStatusCode.BadRequest)
            : inner(r);
        var again = Open();
        again.WaitForAssertion(() => Assert.NotEmpty(again.FindAll(".detect-relations")));
        again.Find(".detect-relations").Click();
        again.WaitForAssertion(() => Assert.Contains("AllowProfiling", again.Markup));
    }
}
