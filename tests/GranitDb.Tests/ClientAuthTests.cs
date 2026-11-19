using System.Net;
using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Layout;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace GranitDb.Tests;

/// <summary>Authentification côté interface : écran de connexion, compte, et ce que chaque rôle voit.</summary>
public class ClientAuthTests : ShellTestBase
{
    public ClientAuthTests() : base(popoverProvider: false)
    {
    }

    private const string Providers = """[{"id":"github","displayName":"GitHub"},{"id":"corp","displayName":"SSO d'entreprise"}]""";

    private void Session(string json) =>
        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/Auth/me" ? FakeApi.Json(json) : null;

    private static string Signed(string role, string login = "alice") =>
        $$"""{"enabled":true,"authenticated":true,"login":"{{login}}","name":"Alice A.","avatarUrl":null,"role":"{{role}}","provider":"github","providers":{{Providers}}}""";

    private IRenderedComponent<MainLayout> RenderLayout() =>
        Ctx.Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "contenu"))));

    [BunitFact]
    public void Without_authentication_on_the_server_the_app_shows_as_before_with_every_right()
    {
        var layout = RenderLayout(); // le faux API répond 404 à la session : pas d'authentification

        layout.WaitForAssertion(() => Assert.Contains("contenu", layout.Markup));
        Assert.Empty(layout.FindAll(".login-screen"));
        Assert.Empty(layout.FindAll(".user-menu"));
        var auth = Ctx.Services.GetRequiredService<AuthState>();
        Assert.True(auth.CanEdit);
        Assert.True(auth.IsAdmin);
    }

    [BunitFact]
    public void Without_a_session_the_login_screen_replaces_the_app_and_offers_each_configured_provider()
    {
        Session($$"""{"enabled":true,"authenticated":false,"providers":{{Providers}}}""");

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".login-screen")));
        Assert.DoesNotContain("contenu", layout.Markup);
        var buttons = layout.FindAll("a.login-provider");
        Assert.Equal(["github", "corp"], buttons.Select(b => b.GetAttribute("data-provider")).ToArray());
        Assert.Contains("Se connecter avec SSO d'entreprise", buttons[1].TextContent);
        var href = buttons[0].GetAttribute("href")!;
        Assert.StartsWith("http://api.test/api/v1/Auth/login/github?returnUrl=", href);
        Assert.Contains(Uri.EscapeDataString(Nav.BaseUri), href);
        // Aucune page n'a été chargée sans compte : pas de lecture du catalogue.
        Assert.DoesNotContain(Api.Requests, r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo");
    }

    [BunitFact]
    public void A_refused_sign_in_explains_it_and_the_return_address_drops_the_error_code()
    {
        Session($$"""{"enabled":true,"authenticated":false,"providers":{{Providers}}}""");
        Nav.NavigateTo("/databases?auth_error=refused");

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.Contains("aucun rôle", layout.Find(".login-error").TextContent));
        var href = layout.Find("a.login-provider").GetAttribute("href")!;
        Assert.DoesNotContain("auth_error", Uri.UnescapeDataString(href));

    }

    [BunitFact]
    public void A_failed_sign_in_says_so_without_technical_detail()
    {
        Session($$"""{"enabled":true,"authenticated":false,"providers":{{Providers}}}""");
        Nav.NavigateTo("/?auth_error=failed");

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.Contains("a échoué", layout.Find(".login-error").TextContent));
    }

    [BunitFact]
    public void Without_an_account_but_with_public_databases_the_app_opens_read_only_with_a_sign_in_menu()
    {
        Session($$"""{"enabled":true,"authenticated":false,"allowAnonymousRead":true,"providers":{{Providers}}}""");

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.Contains("contenu", layout.Markup));
        Assert.Empty(layout.FindAll(".login-screen"));
        Assert.Equal("Visiteur", layout.Find(".visitor-chip").TextContent.Trim());
        Assert.NotEmpty(layout.FindAll(".visitor-login"));
        var auth = Ctx.Services.GetRequiredService<AuthState>();
        Assert.True(auth.IsVisitor);
        Assert.False(auth.CanEdit);
        Assert.False(auth.IsAdmin);
    }

    [BunitFact]
    public void A_signed_in_reader_sees_the_app_a_read_only_mark_and_no_administration_entries()
    {
        Session(Signed("Reader"));

        var layout = RenderLayout();

        layout.WaitForAssertion(() => Assert.Contains("contenu", layout.Markup));
        Assert.Equal("Lecture seule", layout.Find(".read-only-chip").TextContent.Trim());
        var auth = Ctx.Services.GetRequiredService<AuthState>();
        Assert.False(auth.CanEdit);
        Assert.False(auth.IsAdmin);
        Assert.True(auth.IsReadOnly);
    }

    [BunitFact]
    public void An_editor_may_edit_but_is_not_an_administrator_and_an_administrator_may_do_everything()
    {
        Session(Signed("Editor"));
        var editor = RenderLayout();
        editor.WaitForAssertion(() => Assert.Contains("contenu", editor.Markup));
        var auth = Ctx.Services.GetRequiredService<AuthState>();
        Assert.True(auth.CanEdit);
        Assert.False(auth.IsAdmin);
        Assert.Empty(editor.FindAll(".read-only-chip"));

        Session(Signed("Admin"));
        auth.LoadAsync().GetAwaiter().GetResult();
        Assert.True(auth.IsAdmin);
        Assert.Equal("Administrateur", AuthState.RoleLabel(auth.Role));
    }

    [BunitFact]
    public void Signing_out_calls_the_api_and_returns_to_the_login_screen()
    {
        Session(Signed("Reader"));
        var layout = RenderLayout();
        layout.WaitForAssertion(() => Assert.Contains("contenu", layout.Markup));

        Session($$"""{"enabled":true,"authenticated":false,"providers":{{Providers}}}""");
        Ctx.Services.GetRequiredService<AuthState>().LogoutAsync().GetAwaiter().GetResult();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".login-screen")));
        Assert.True(WasSent(HttpMethod.Post, "/api/v1/Auth/logout"));
    }

    [BunitFact]
    public void An_expired_session_answered_401_brings_back_the_login_screen_without_a_reload()
    {
        Session(Signed("Editor"));
        var layout = RenderLayout();
        layout.WaitForAssertion(() => Assert.Contains("contenu", layout.Markup));

        Override = r => r.RequestUri!.AbsolutePath == "/api/v1/DatabaseInfo"
            ? FakeApi.Json("""{"title":"Connexion requise.","status":401}""", HttpStatusCode.Unauthorized)
            : r.RequestUri.AbsolutePath == "/api/v1/Auth/me" ? FakeApi.Json($$"""{"enabled":true,"authenticated":false,"providers":{{Providers}}}""") : null;
        Assert.ThrowsAsync<HttpRequestException>(() => Api.Client.GetAllDatabaseAsync()).GetAwaiter().GetResult();

        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".login-screen")));
    }

    [BunitFact]
    public void A_reader_sees_text_where_an_editor_would_click_to_edit()
    {
        Session(Signed("Reader"));
        Ctx.Services.GetRequiredService<AuthState>().LoadAsync().GetAwaiter().GetResult();

        var text = Ctx.Render<InlineText>(p => p.Add(x => x.Value, "Les clients"));

        Assert.Contains("Les clients", text.Markup);
        Assert.Empty(text.FindAll("[role=button]"));
        Assert.Empty(text.FindAll(".inline-pencil"));
    }

    [BunitFact]
    public void An_editor_still_gets_the_in_place_editor()
    {
        Session(Signed("Editor"));
        Ctx.Services.GetRequiredService<AuthState>().LoadAsync().GetAwaiter().GetResult();

        var text = Ctx.Render<InlineText>(p => p.Add(x => x.Value, "Les clients"));

        Assert.NotEmpty(text.FindAll("[role=button]"));
    }

    [BunitFact]
    public async Task The_session_call_never_fails_the_app_when_the_server_does_not_know_authentication()
    {
        Api.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var session = await Api.Client.GetSessionAsync();

        Assert.False(session.Enabled);
        Assert.False(session.Authenticated);
    }
}
