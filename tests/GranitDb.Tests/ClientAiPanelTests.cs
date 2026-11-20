using System.Net;
using Bunit;
using GranitDb.Client.Components;

namespace GranitDb.Tests;

/// <summary>Panneau « Assistant d'IA » : état du modèle et essai, pour l'administrateur seulement quand l'IA est activée.</summary>
public class ClientAiPanelTests : ShellTestBase
{
    private void Serve(bool allowAi, string status = """{"enabled":true,"model":"qwen3:4b-instruct","endpoint":"http://ollama:11434","reachable":true,"modelAvailable":true,"message":null}""")
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities")
                return FakeApi.Json($$"""{"allowSqlServerSources":false,"allowLocalPaths":false,"allowProfiling":false,"allowAi":{{allowAi.ToString().ToLowerInvariant()}},"aiModel":"qwen3:4b-instruct"}""");
            if (path == "/api/v1/Ai/status") return FakeApi.Json(status);
            if (r.Method == HttpMethod.Post && path == "/api/v1/Ai/test")
                return FakeApi.Json("""{"model":"qwen3:4b-instruct","reply":"{}","validJson":true,"elapsedMs":2500,"promptTokens":20,"completionTokens":8}""");
            return null;
        };
    }

    [BunitFact]
    public void The_panel_shows_the_model_state_and_tests_it_on_demand()
    {
        Serve(true);

        var page = Ctx.Render<AiPanel>();

        page.WaitForAssertion(() => Assert.Contains("qwen3:4b-instruct prêt", page.Find(".ai-status").TextContent));
        page.Find(".ai-test").Click();
        page.WaitForAssertion(() => Assert.Contains("2,5 s", page.Find(".ai-result").TextContent.Replace('.', ',')));
        Assert.Contains("JSON valide", page.Find(".ai-result").TextContent);
    }

    [BunitFact]
    public void The_panel_explains_a_model_still_downloading()
    {
        Serve(true, """{"enabled":true,"model":"qwen3:4b-instruct","endpoint":"http://ollama:11434","reachable":true,"modelAvailable":false,"message":"Le modèle qwen3:4b-instruct n'est pas (encore) sur le serveur"}""");

        var page = Ctx.Render<AiPanel>();

        page.WaitForAssertion(() => Assert.Contains("pas (encore)", page.Find(".ai-status").TextContent));
    }

    [BunitFact]
    public void A_refused_test_shows_the_reason()
    {
        Serve(true);
        var inner = Override;
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/Ai/test"
            ? FakeApi.Json("""{"title":"Le serveur d'IA est injoignable","status":400}""", HttpStatusCode.BadRequest)
            : inner(r);
        var page = Ctx.Render<AiPanel>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".ai-test")));

        page.Find(".ai-test").Click();

        page.WaitForAssertion(() => Assert.Contains("injoignable", page.Find(".ai-error").TextContent));
    }

    [BunitFact]
    public void Nothing_is_shown_when_the_assistant_is_off()
    {
        Serve(false);

        var page = Ctx.Render<AiPanel>();

        page.WaitForState(() => true);
        Assert.Empty(page.FindAll(".ai-panel"));
    }
}
