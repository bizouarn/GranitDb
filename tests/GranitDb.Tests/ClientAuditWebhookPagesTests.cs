using System.Net;
using Bunit;
using GranitDb.Client.Pages;

namespace GranitDb.Tests;

/// <summary>Pages client du journal d'audit et des webhooks.</summary>
public class ClientAuditWebhookPagesTests : ShellTestBase
{
    private const string AuditRows = """
        [{"id":"a1","at":"2026-11-18T10:00:00Z","actor":"alice","method":"DELETE","path":"/DatabaseInfo/d1","status":204,"controller":"DatabaseInfo","entityId":"d1","ip":"10.0.0.1"},
         {"id":"a2","at":"2026-11-18T09:00:00Z","method":"POST","path":"/import/sqlite","status":400}]
        """;

    private const string Hooks = """
        [{"id":"w1","url":"https://example.org/hook","events":["schema.changed","database.deleted"],"isActive":true,"hasSecret":true,"description":"Notifier l'équipe","databaseId":"d1","createdAt":"2026-11-01T00:00:00Z"}]
        """;

    private static HttpResponseMessage WithTotal(string json, int total)
    {
        var response = FakeApi.Json(json);
        response.Headers.Add("X-Total-Count", total.ToString());
        return response;
    }

    private string LastAuditQuery() => Api.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/v1/Audit").RequestUri!.Query;

    // --- Journal d'audit ---

    private IRenderedComponent<Audit> OpenAudit(int total = 2)
    {
        Override = r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/Audit" ? WithTotal(AuditRows, total) : null;
        var page = Ctx.Render<Audit>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-audit]")));
        return page;
    }

    [BunitFact]
    public void The_audit_page_lists_the_entries_with_who_did_what()
    {
        var page = OpenAudit();

        Assert.Equal(["a1", "a2"], page.FindAll("[data-audit]").Select(r => r.GetAttribute("data-audit")!).ToArray());
        var first = page.Find("[data-audit='a1']").TextContent;
        Assert.Contains("alice", first);
        Assert.Contains("DELETE", first);
        Assert.Contains("/DatabaseInfo/d1", first);
        Assert.Contains("—", page.Find("[data-audit='a2']").TextContent); // pas d'auteur connu
        Assert.Contains("1–2 sur 2", page.Find(".audit-range").TextContent);
        Assert.True(page.Find(".audit-next").HasAttribute("disabled"));
        Assert.True(page.Find(".audit-previous").HasAttribute("disabled"));
    }

    [BunitFact]
    public void Filters_are_sent_to_the_api_and_paging_moves_by_pages()
    {
        var page = OpenAudit(total: 120);
        Assert.False(page.Find(".audit-next").HasAttribute("disabled"));

        page.Find(".audit-actor").Input("alice");
        page.Find(".audit-method select").Change("DELETE");
        page.Find(".audit-filters form").Submit();

        page.WaitForAssertion(() =>
        {
            var query = LastAuditQuery();
            Assert.Contains("actor=alice", query);
            Assert.Contains("method=DELETE", query);
            Assert.Contains("skip=0", query);
        });

        page.Find(".audit-next").Click();
        page.WaitForAssertion(() => Assert.Contains("skip=50", LastAuditQuery()));
        Assert.Contains("actor=alice", LastAuditQuery()); // les filtres restent
    }

    [BunitFact]
    public void An_empty_journal_says_so()
    {
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Audit" ? WithTotal("[]", 0) : null;

        var page = Ctx.Render<Audit>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find(".audit-empty")));
    }

    // --- Webhooks ---

    private IRenderedComponent<Webhooks> OpenWebhooks(Func<HttpRequestMessage, HttpResponseMessage?>? more = null)
    {
        Override = r => more?.Invoke(r) ?? (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks" ? FakeApi.Json(Hooks) : null);
        var page = Ctx.Render<Webhooks>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-webhook]")));
        return page;
    }

    private void OpenDialog(IRenderedComponent<Webhooks> page)
    {
        page.Find(".webhook-add").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".webhook-dialog")));
    }

    [BunitFact]
    public void The_save_button_stays_off_until_the_address_and_an_event_are_valid()
    {
        var page = OpenWebhooks();
        OpenDialog(page);
        Assert.True(Dialogs!.Find("button.webhook-save").HasAttribute("disabled"));

        Dialogs!.Find(".webhook-form-url").Input("ftp://exemple");
        Assert.Contains("http://", Dialogs!.Find(".webhook-dialog-url-hint").TextContent);
        Assert.True(Dialogs!.Find("button.webhook-save").HasAttribute("disabled"));

        Dialogs!.Find(".webhook-form-url").Input("https://exemple.org/x");
        Assert.False(Dialogs!.Find("button.webhook-save").HasAttribute("disabled"));
        Dialogs!.Find("input[data-event='schema.changed']").Change(false); // plus aucun événement
        Assert.True(Dialogs!.Find("button.webhook-save").HasAttribute("disabled"));
    }

    [BunitFact]
    public void The_webhooks_page_shows_each_webhook_without_its_secret()
    {
        var page = OpenWebhooks();

        var text = page.Find("[data-webhook='https://example.org/hook']").TextContent;
        Assert.Contains("schema.changed", text);
        Assert.Contains("database.deleted", text);
        Assert.Contains("signé", text);
        Assert.Contains("actif", text);
        Assert.Contains("Notifier l'équipe", text);
        Assert.Contains("Boutique", text); // la base surveillée est nommée
    }

    [BunitFact]
    public void A_webhook_is_created_from_the_form_with_the_chosen_events()
    {
        var page = OpenWebhooks(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks"
            ? FakeApi.Json("""{"id":"w2"}""", HttpStatusCode.Created)
            : null);

        OpenDialog(page);
        Dialogs!.Find(".webhook-form-url").Input("https://hooks.example.org/granit");
        Dialogs!.Find(".webhook-form-secret").Input("s3cret");
        Dialogs!.Find("input[data-event='database.imported']").Change(true);
        Dialogs!.Find("button.webhook-save").Click();

        page.WaitForAssertion(() => Assert.Empty(Dialogs!.FindAll(".webhook-dialog")));
        var body = BodyOf(HttpMethod.Post, "/api/v1/Webhooks");
        Assert.Equal("https://hooks.example.org/granit", body.GetProperty("url").GetString());
        Assert.Equal("s3cret", body.GetProperty("secret").GetString());
        Assert.Equal(["schema.changed", "database.imported"], body.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [BunitFact]
    public void A_refused_address_shows_the_servers_explanation_and_keeps_the_form()
    {
        var page = OpenWebhooks(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Adresse locale ou privée refusée.") }
            : null);

        OpenDialog(page);
        Dialogs!.Find(".webhook-form-url").Input("http://localhost/x");
        Dialogs!.Find("button.webhook-save").Click();

        Dialogs!.WaitForAssertion(() => Assert.Contains("Adresse locale ou privée refusée.", Dialogs!.Find(".webhook-dialog-error").TextContent));
        Assert.NotEmpty(Dialogs!.FindAll(".webhook-dialog"));
    }

    [BunitFact]
    public void Editing_leaves_the_secret_alone_unless_a_new_one_is_typed()
    {
        var page = OpenWebhooks(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks/w1"
            ? FakeApi.Json("{}")
            : null);

        page.Find(".webhook-edit").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".webhook-dialog")));
        Assert.Equal("https://example.org/hook", Dialogs!.Find(".webhook-form-url").GetAttribute("value"));
        Dialogs!.Find(".webhook-form-active").Change(false);
        Dialogs!.Find("button.webhook-save").Click();

        page.WaitForAssertion(() => Assert.Empty(Dialogs!.FindAll(".webhook-dialog")));
        var body = BodyOf(HttpMethod.Put, "/api/v1/Webhooks/w1");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("secret").ValueKind);
        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    [BunitFact]
    public void A_test_send_shows_whether_it_got_through_and_the_deliveries_can_be_listed()
    {
        var page = OpenWebhooks(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks/w1/test"
            ? FakeApi.Json("""{"id":"x","event":"ping","success":false,"statusCode":500,"attempts":3,"error":"HTTP 500"}""")
            : r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks/w1/deliveries"
                ? FakeApi.Json("""[{"id":"e1","at":"2026-11-18T10:00:00Z","event":"schema.changed","success":true,"statusCode":200,"attempts":1}]""")
                : null);

        page.Find(".webhook-test").Click();
        page.WaitForAssertion(() => Assert.Contains("Essai échoué : HTTP 500", page.Find(".webhook-test-result").TextContent));

        page.Find(".webhook-deliveries").Click();
        page.WaitForAssertion(() => Assert.Contains("réussi (200)", page.Find("[data-delivery='e1']").TextContent));
    }

    [BunitFact]
    public void Deleting_a_webhook_asks_for_confirmation_first()
    {
        var page = OpenWebhooks(r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks/w1"
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : null);

        page.Find(".webhook-delete").Click();
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".confirm-dialog")));
        Assert.DoesNotContain(Api.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Contains("ne peut pas être annulé", Dialogs!.Find(".confirm-dialog-consequence").TextContent);
        Dialogs!.Find("button.confirm-dialog-confirm").Click();

        page.WaitForAssertion(() => Assert.Contains(Api.Requests, r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == "/api/v1/Webhooks/w1"));
    }
}
