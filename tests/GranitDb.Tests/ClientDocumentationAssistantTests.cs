using System.Net;
using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Écran de relecture de l'assistant de documentation et bouton « Suggérer ».</summary>
public class ClientDocumentationAssistantTests : ShellTestBase
{
    private const string Items = """
        [{"id":"s1","databaseId":"d1","kind":"description","targetType":"table","targetId":"t1","targetLabel":"orders","text":"Les commandes.","model":"qwen3:4b-instruct","uncertain":false,"status":"pending","createdAt":"2026-11-19T10:00:00Z"},
         {"id":"s2","databaseId":"d1","kind":"description","targetType":"column","targetId":"c9","targetLabel":"orders.ZX_T01","text":"Code technique.","model":"qwen3:4b-instruct","uncertain":true,"status":"pending","createdAt":"2026-11-19T10:00:00Z"},
         {"id":"s3","databaseId":"d1","kind":"sensitive","targetType":"column","targetId":"c3","targetLabel":"customers.email","text":"Le nom `email` évoque : adresse e-mail.","model":"règles","uncertain":false,"status":"pending","createdAt":"2026-11-19T10:00:00Z"}]
        """;

    private const string One = """{"id":"s1","databaseId":"d1","kind":"description","targetType":"table","targetId":"t1","targetLabel":"orders","text":"Les commandes.","model":"qwen3:4b-instruct","uncertain":false,"status":"pending","createdAt":"2026-11-19T10:00:00Z"}""";

    private const string Accepted = """{"id":"s1","databaseId":"d1","kind":"description","targetType":"table","targetId":"t1","targetLabel":"orders","text":"Les commandes (retouché).","model":"qwen3:4b-instruct","uncertain":false,"status":"accepted","createdAt":"2026-11-19T10:00:00Z"}""";

    private void Serve(bool allowAi, string items = Items)
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities")
                return FakeApi.Json($$"""{"allowSqlServerSources":false,"allowLocalPaths":false,"allowProfiling":false,"allowAi":{{allowAi.ToString().ToLowerInvariant()}},"aiModel":"qwen3:4b-instruct"}""");
            if (r.Method == HttpMethod.Get && path == "/api/v1/DescriptionSuggestions/database/d1") return FakeApi.Json(items);
            if (r.Method == HttpMethod.Get && path == "/api/v1/DescriptionSuggestions/database/d1/job")
                return FakeApi.Json("""{"title":"Pas de lot","status":404}""", HttpStatusCode.NotFound);
            return null;
        };
    }

    private IRenderedComponent<DocumentationAssistant> Open()
    {
        Nav.NavigateTo("/databases/d1/documentation");
        return Ctx.Render<DocumentationAssistant>(p => p.Add(x => x.Id, "d1"));
    }

    [BunitFact]
    public void Proposals_are_listed_with_the_doubt_and_the_origin_of_each()
    {
        Serve(true);

        var page = Open();

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("tr[data-suggestion]").Count));
        Assert.Equal("Les commandes.", page.Find("tr[data-suggestion='s1'] .suggestion-input").GetAttribute("value"));
        Assert.NotEmpty(page.FindAll("tr[data-suggestion='s2'] .uncertain"));
        Assert.Empty(page.FindAll("tr[data-suggestion='s1'] .uncertain"));
        Assert.Contains("proposé par règles", page.Find("tr[data-suggestion='s3']").TextContent);
        Assert.Contains("évoque : adresse e-mail", page.Find("tr[data-suggestion='s3'] .suggestion-reason").TextContent);
        Assert.Contains("1 proposition(s) sûre(s)", page.Find(".accept-confident").TextContent);
    }

    [BunitFact]
    public void Accepting_sends_the_edited_text_and_removes_the_row()
    {
        Serve(true);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DescriptionSuggestions/database/d1/s1/accept" ? FakeApi.Json(Accepted) : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-suggestion='s1']")));

        page.Find("tr[data-suggestion='s1'] .suggestion-input").Change("Les commandes (retouché).");
        page.Find("tr[data-suggestion='s1'] .accept-suggestion").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("tr[data-suggestion='s1']")));
        Assert.Equal("Les commandes (retouché).", BodyOf(HttpMethod.Post, "/api/v1/DescriptionSuggestions/database/d1/s1/accept").GetProperty("text").GetString());
    }

    [BunitFact]
    public void Rejecting_removes_the_row_and_tells_the_server()
    {
        Serve(true);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DescriptionSuggestions/database/d1/s2/reject"
            ? FakeApi.Json("""{"accepted":0,"skipped":1}""") : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("tr[data-suggestion='s2']")));

        page.Find("tr[data-suggestion='s2'] .reject-suggestion").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("tr[data-suggestion='s2']")));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/DescriptionSuggestions/database/d1/s2/reject"));
    }

    [BunitFact]
    public void Starting_the_batch_shows_its_progress()
    {
        Serve(true);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DescriptionSuggestions/database/d1/generate"
            ? FakeApi.Json("""{"databaseId":"d1","state":"running","tablesTotal":4,"tablesDone":1,"suggested":3,"failed":0,"errors":[],"startedAt":"2026-11-19T10:00:00Z","finishedAt":null}""")
            : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".generate-descriptions")));

        page.Find(".generate-descriptions").Click();

        page.WaitForAssertion(() => Assert.Contains("1 / 4 table(s), 3 proposition(s)", page.Find(".job-state").TextContent));
        Assert.NotEmpty(page.FindAll(".cancel-job"));
    }

    [BunitFact]
    public void A_generation_refused_by_the_server_shows_why()
    {
        Serve(true);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DescriptionSuggestions/database/d1/generate"
            ? FakeApi.Json("""{"title":"Le serveur d'IA est injoignable","status":400}""", HttpStatusCode.BadRequest) : inner(r);
        var page = Open();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".generate-descriptions")));

        page.Find(".generate-descriptions").Click();

        page.WaitForAssertion(() => Assert.Contains("injoignable", page.Markup));
    }

    [BunitFact]
    public void Without_the_assistant_only_sensitive_detection_is_offered()
    {
        Serve(false, "[]");

        var page = Open();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".ai-off")));
        Assert.Empty(page.FindAll(".generate-descriptions"));
        Assert.NotEmpty(page.FindAll(".detect-sensitive"));
    }

    [BunitFact]
    public void The_suggest_button_proposes_then_writes_only_after_accept_and_only_for_an_empty_description()
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post && path == "/api/v1/DescriptionSuggestions/database/d1/suggest") return FakeApi.Json(One);
            if (r.Method == HttpMethod.Post && path == "/api/v1/DescriptionSuggestions/database/d1/s1/accept") return FakeApi.Json(Accepted);
            return null;
        };
        string? written = null;
        var button = Ctx.Render<SuggestButton>(p => p.Add(x => x.Enabled, true).Add(x => x.DatabaseId, "d1").Add(x => x.TargetType, "table")
            .Add(x => x.TargetId, "t1").Add(x => x.Description, null)
            .Add(x => x.OnAccepted, (string text) => written = text));

        button.Find(".suggest-button").Click();
        button.WaitForAssertion(() => Assert.NotEmpty(button.FindAll(".suggestion-text")));
        Assert.Null(written); // rien n'est enregistré avant « Accepter »
        Assert.False(WasSent(HttpMethod.Post, "/api/v1/DescriptionSuggestions/database/d1/s1/accept"));
        button.Find(".suggestion-accept").Click();

        button.WaitForAssertion(() => Assert.Equal("Les commandes (retouché).", written));
        var described = Ctx.Render<SuggestButton>(p => p.Add(x => x.Enabled, true).Add(x => x.DatabaseId, "d1").Add(x => x.TargetType, "table")
            .Add(x => x.TargetId, "t1").Add(x => x.Description, "Déjà décrite."));
        Assert.Empty(described.FindAll(".suggest-button"));
        var off = Ctx.Render<SuggestButton>(p => p.Add(x => x.Enabled, false).Add(x => x.DatabaseId, "d1").Add(x => x.TargetType, "table")
            .Add(x => x.TargetId, "t1").Add(x => x.Description, null));
        Assert.Empty(off.FindAll(".suggest-button"));
    }
}
