using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Jalon 11, lot 9 : l'assistant d'import à étapes (type → connexion testée → contenu → récapitulatif).</summary>
public class ClientImportWizardTests : ShellTestBase
{
    private string _testResult = """
        {"ok":true,"message":"Connexion réussie : 2 table(s) et 1 vue(s) dans 2 schéma(s).","tables":2,"views":1,
         "schemas":[{"name":"dbo","tables":2},{"name":"sales","tables":1}]}
        """;

    public ClientImportWizardTests()
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/Capabilities") return FakeApi.Json("""{"allowSqlServerSources":true,"allowLocalPaths":false}""");
            if (r.Method == HttpMethod.Post && path == "/api/v1/import/sqlserver/test") return FakeApi.Json(_testResult);
            if (path.StartsWith("/api/v1/import/sqlserver") && r.Method != HttpMethod.Get)
                return FakeApi.Json("""{"database":{"id":"d5","name":"Boutique","type":"SqlServer","canResync":true},"sync":{"tablesAdded":3,"tablesRemoved":1,"columnsAdded":9}}""",
                    r.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK);
            return null;
        };
    }

    private IRenderedComponent<Databases> OpenHome()
    {
        var home = Ctx.Render<Databases>();
        home.WaitForAssertion(() => Assert.Contains("Boutique", home.Markup));
        home.Find("button.connect-sqlserver").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".import-wizard")));
        return home;
    }

    private void Fill()
    {
        Dialogs!.Find(".field-server input").Input("srv");
        Dialogs!.Find(".field-database input").Input("Boutique");
        Dialogs!.Find(".field-user input").Input("lecteur");
        Dialogs!.Find(".field-password input").Input("s3cret");
    }

    private JsonElement Body(HttpMethod method, string path)
    {
        var index = Api.Requests.ToList().FindLastIndex(r => r.Method == method && r.RequestUri!.AbsolutePath == path);
        Assert.True(index >= 0, $"{method} {path} n'a pas été appelé");
        return JsonDocument.Parse(Api.Bodies[index]).RootElement;
    }

    private string CurrentStep() => Dialogs!.Find(".wizard-step.current").TextContent;

    [BunitFact]
    public void The_wizard_walks_through_four_steps_then_imports_and_opens_the_database()
    {
        var home = OpenHome();

        Assert.Equal(4, Dialogs!.FindAll(".wizard-step").Count);
        Assert.Contains("Type de base", CurrentStep());
        Dialogs!.Find("button.wizard-next").Click();

        Assert.Contains("Connexion", CurrentStep());
        Assert.True(Dialogs!.Find("button.wizard-next").HasAttribute("disabled")); // incomplet : un bouton grisé, pas une erreur après coup
        Fill();
        Dialogs!.Find("button.wizard-next").Click();

        Dialogs!.WaitForAssertion(() => Assert.Contains("Contenu", CurrentStep()));
        Assert.Equal(2, Dialogs!.FindAll("#schemas input[type=checkbox]").Count);
        Dialogs!.Find(".field-name input").Input("Ma boutique");
        Dialogs!.Find("button.wizard-next").Click();

        Assert.Contains("Récapitulatif", CurrentStep());
        Assert.Contains("Ma boutique", Dialogs!.Find(".wizard-summary-text").TextContent);
        Assert.Contains("tous les schémas", Dialogs!.Find(".wizard-summary-text").TextContent);
        Dialogs!.Find("button.wizard-finish").Click();

        home.WaitForAssertion(() => Assert.EndsWith("/databases/d5", Nav.Uri));
        var body = Body(HttpMethod.Post, "/api/v1/import/sqlserver");
        Assert.Equal("srv", body.GetProperty("server").GetString());
        Assert.Equal("Ma boutique", body.GetProperty("name").GetString());
    }

    [BunitFact]
    public void A_failed_test_keeps_the_user_on_the_connection_step_with_the_instruction()
    {
        _testResult = """{"ok":false,"message":"Identifiants refusés par le serveur : vérifiez l'identifiant.","tables":0,"views":0,"schemas":[]}""";
        OpenHome();
        Dialogs!.Find("button.wizard-next").Click();
        Fill();

        Dialogs!.Find("button.wizard-next").Click();

        Dialogs!.WaitForAssertion(() => Assert.Contains("Identifiants refusés", Dialogs!.Find(".connection-error").TextContent));
        Assert.Contains("Connexion", CurrentStep());
        Assert.DoesNotContain(Api.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/import/sqlserver");
    }

    [BunitFact]
    public void Unchecking_every_schema_blocks_the_next_step_and_a_restricted_choice_is_sent_and_summarized()
    {
        OpenHome();
        Dialogs!.Find("button.wizard-next").Click();
        Fill();
        Dialogs!.Find("button.wizard-next").Click();
        Dialogs!.WaitForAssertion(() => Assert.Contains("Contenu", CurrentStep()));

        var boxes = Dialogs!.FindAll("#schemas input[type=checkbox]");
        boxes[0].Change(false);
        Dialogs!.FindAll("#schemas input[type=checkbox]")[1].Change(false);
        Assert.True(Dialogs!.Find("button.wizard-next").HasAttribute("disabled"));
        Assert.NotEmpty(Dialogs!.FindAll(".wizard-schema-hint"));

        Dialogs!.FindAll("#schemas input[type=checkbox]")[1].Change(true);
        Dialogs!.Find("button.wizard-next").Click();
        Assert.Contains("le schéma sales", Dialogs!.Find(".wizard-summary-text").TextContent);
        Dialogs!.Find("button.wizard-finish").Click();

        Dialogs!.WaitForAssertion(() => Assert.EndsWith("/databases/d5", Nav.Uri));
        Assert.Equal("sales", Body(HttpMethod.Post, "/api/v1/import/sqlserver").GetProperty("schemas")[0].GetString());
    }

    [BunitFact]
    public void Previous_goes_back_and_cancel_closes_without_importing()
    {
        OpenHome();
        Dialogs!.Find("button.wizard-next").Click();
        Assert.Contains("Connexion", CurrentStep());

        Dialogs!.Find("button.wizard-back").Click();
        Assert.Contains("Type de base", CurrentStep());
        Dialogs!.Find("button.wizard-cancel").Click();

        Dialogs!.WaitForAssertion(() => Assert.Empty(Dialogs!.FindAll(".import-wizard")));
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/import/sqlserver"));
    }

    [BunitFact]
    public void Editing_a_connection_skips_the_type_and_the_name_and_replaces_the_connection_of_that_database()
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/DatabaseInfo/d1") return FakeApi.Json("""{"id":"d1","name":"Distante","type":"SqlServer","canResync":true,"sourceLabel":"srv:1444 / Boutique"}""");
            if (r.Method == HttpMethod.Post && path == "/api/v1/import/sqlserver/test") return FakeApi.Json(_testResult);
            if (r.Method == HttpMethod.Put && path == "/api/v1/import/sqlserver/d1")
                return FakeApi.Json("""{"database":{"id":"d1","name":"Distante","type":"SqlServer","canResync":true},"sync":{"tablesAdded":0,"tablesRemoved":0,"columnsAdded":1}}""");
            return null;
        };
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));

        page.Find("button.edit-connection").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".import-wizard")));

        Assert.Equal(3, Dialogs!.FindAll(".wizard-step").Count);
        Assert.Contains("Connexion", CurrentStep());
        Fill();
        Dialogs!.Find("button.wizard-next").Click();
        Dialogs!.WaitForAssertion(() => Assert.Contains("Schémas", CurrentStep()));
        Assert.Empty(Dialogs!.FindAll(".field-name"));
        Dialogs!.Find("button.wizard-next").Click();
        Assert.Contains("Distante", Dialogs!.Find(".wizard-summary-text").TextContent);
        Dialogs!.Find("button.wizard-finish").Click();

        Dialogs!.WaitForAssertion(() => Assert.Empty(Dialogs!.FindAll(".import-wizard")));
        Assert.Equal("srv", Body(HttpMethod.Put, "/api/v1/import/sqlserver/d1").GetProperty("server").GetString());
    }
}
