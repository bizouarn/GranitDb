using System.Net;
using Bunit;
using GranitDb.Client.Pages;
using GranitDb.Client.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace GranitDb.Tests;

/// <summary>Réglage et état de la synchronisation automatique sur la page d'une base.</summary>
public class ClientScheduleTests : ShellTestBase
{
    private const string Resyncable = """{"id":"d1","name":"Boutique","type":"Sqlite","canResync":true,"description":"Ma boutique"}""";

    private void Serve(string database, string? session = null)
    {
        Override = r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/api/v1/DatabaseInfo/d1" && r.Method == HttpMethod.Get) return FakeApi.Json(database);
            if (session != null && path == "/api/v1/Auth/me") return FakeApi.Json(session);
            if (path == "/api/v1/DatabaseInfo/d1/schedule")
            {
                var minutes = System.Text.Json.JsonDocument.Parse(Api.Bodies[^1]).RootElement.GetProperty("intervalMinutes").GetInt32();
                return minutes == 1
                    ? FakeApi.Json("""{"title":"L'intervalle minimal est de 5 minute(s).","status":400}""", HttpStatusCode.BadRequest)
                    : FakeApi.Json($$"""{"id":"d1","name":"Boutique","type":"Sqlite","canResync":true,"syncIntervalMinutes":{{(minutes == 0 ? "null" : minutes.ToString())}}}""");
            }

            return null;
        };
    }

    private IRenderedComponent<DatabaseDetail> Open()
    {
        Nav.NavigateTo("/databases/d1");
        var page = Ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "d1"));
        page.WaitForAssertion(() => Assert.Contains("customers", page.Markup));
        return page;
    }

    [BunitFact]
    public void The_schedule_menu_shows_the_current_interval_and_changing_it_calls_the_api()
    {
        Serve(Resyncable);
        var page = Open();
        Assert.Equal("0", page.Find(".field-schedule select").GetAttribute("value"));

        page.Find(".field-schedule select").Change("60");

        page.WaitForAssertion(() => Assert.True(WasSent(HttpMethod.Put, "/api/v1/DatabaseInfo/d1/schedule")));
        Assert.Equal(60, BodyOf(HttpMethod.Put, "/api/v1/DatabaseInfo/d1/schedule").GetProperty("intervalMinutes").GetInt32());
        page.WaitForAssertion(() => Assert.Contains("toutes les 60 minute(s)", page.Markup));
    }

    [BunitFact]
    public void Turning_it_off_sends_zero()
    {
        Serve("""{"id":"d1","name":"Boutique","type":"Sqlite","canResync":true,"syncIntervalMinutes":15}""");
        var page = Open();
        Assert.Equal("15", page.Find(".field-schedule select").GetAttribute("value"));

        page.Find(".field-schedule select").Change("0");

        page.WaitForAssertion(() => Assert.Equal(0, BodyOf(HttpMethod.Put, "/api/v1/DatabaseInfo/d1/schedule").GetProperty("intervalMinutes").GetInt32()));
        page.WaitForAssertion(() => Assert.Contains("désactivée", page.Markup));
    }

    [BunitFact]
    public void The_last_result_is_shown_with_the_reason_when_it_failed()
    {
        Serve("""{"id":"d1","name":"Boutique","type":"SqlServer","canResync":true,"syncIntervalMinutes":60,"lastSyncAt":"2026-11-19T08:00:00Z","lastSyncOk":false,"lastSyncError":"Identifiants refusés par le serveur"}""");

        var page = Open();

        var status = page.Find(".sync-status").TextContent;
        Assert.Contains("échec", status);
        Assert.Contains("Identifiants refusés", status);
    }

    [BunitFact]
    public void A_successful_last_run_says_so()
    {
        Serve("""{"id":"d1","name":"Boutique","type":"Sqlite","canResync":true,"syncIntervalMinutes":60,"lastSyncAt":"2026-11-19T08:00:00Z","lastSyncOk":true}""");

        var page = Open();

        Assert.Contains("réussie", page.Find(".sync-status").TextContent);
    }

    [BunitFact]
    public void A_refusal_is_shown_at_the_top_of_the_page()
    {
        Serve(Resyncable);
        var page = Open();
        page.Find(".field-schedule select").Change("15");
        // « 15 » est accepté par le faux serveur ; on force un refus avec l'intervalle trop court.
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo/d1/schedule"
            ? FakeApi.Json("""{"title":"L'intervalle minimal est de 5 minute(s).","status":400}""", HttpStatusCode.BadRequest)
            : null;

        page.Find(".field-schedule select").Change("60");

        page.WaitForAssertion(() => Assert.Contains("intervalle minimal", page.Markup));
    }

    [BunitFact]
    public void A_database_without_a_source_to_reread_has_no_schedule_menu()
    {
        Serve("""{"id":"d1","name":"Boutique","type":"Sqlite","canResync":false}""");

        var page = Open();

        Assert.Empty(page.FindAll(".field-schedule"));
    }

    [BunitFact]
    public void An_editor_sees_the_schedule_and_its_status_but_cannot_change_it()
    {
        Serve("""{"id":"d1","name":"Boutique","type":"Sqlite","canResync":true,"syncIntervalMinutes":60}""",
            """{"enabled":true,"authenticated":true,"login":"alice","role":"Editor","providers":[]}""");
        ((AuthState)Ctx.Services.GetService(typeof(AuthState))!).LoadAsync().GetAwaiter().GetResult();

        var page = Open();

        Assert.Equal("60", page.Find(".field-schedule select").GetAttribute("value"));
        Assert.All(page.FindAll(".field-schedule option"), o => Assert.True(o.HasAttribute("disabled")));
    }
}
