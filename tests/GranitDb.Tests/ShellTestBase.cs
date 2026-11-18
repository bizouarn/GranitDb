using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace GranitDb.Tests;

/// <summary>
///     Contexte commun aux tests de l'espace de travail : un petit catalogue servi par un faux API (deux bases, des
///     tables, un domaine), l'état de l'espace de travail et les notifications.
/// </summary>
public abstract class ShellTestBase : IAsyncLifetime
{
    protected readonly BunitContext Ctx = new();
    protected readonly FakeApi Api = new();

    /// <summary>Le fournisseur de fenêtres flottantes : le contenu des menus ouverts y est rendu, pas dans la page.</summary>
    protected IRenderedComponent<MudBlazor.MudPopoverProvider>? Popovers { get; private set; }

    /// <summary>Le fournisseur de fenêtres de saisie : le contenu d'une fenêtre ouverte y est rendu, pas dans la page.</summary>
    protected IRenderedComponent<MudBlazor.MudDialogProvider>? Dialogs { get; private set; }

    /// <summary>Remplace la réponse d'une requête (null : réponse par défaut).</summary>
    protected Func<HttpRequestMessage, HttpResponseMessage?> Override = _ => null;

    /// <param name="popoverProvider">Faux quand la mise en page (qui a le sien) est rendue.</param>
    protected ShellTestBase(bool popoverProvider = true)
    {
        Ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        Ctx.Services.AddMudServices();
        Ctx.Services.AddGranitWorkspace();
        Ctx.Services.AddScoped(_ => Api.Client);
        if (popoverProvider)
        {
            Popovers = Ctx.Render<MudBlazor.MudPopoverProvider>();
            Dialogs = Ctx.Render<MudBlazor.MudDialogProvider>();
        }
        Api.Respond = Serve;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Ctx.DisposeAsync();

    protected WorkspaceState State => Ctx.Services.GetRequiredService<WorkspaceState>();

    protected Notifier Notices => Ctx.Services.GetRequiredService<Notifier>();

    protected NavigationManager Nav => Ctx.Services.GetRequiredService<NavigationManager>();

    protected const string Databases = """[{"id":"d2","name":"RH","type":"SqlServer"},{"id":"d1","name":"Boutique","type":"Sqlite","description":"Ma boutique"}]""";

    // customers (id, email sensible) ← orders (customer_id) ; tags (label) ; domaine « Ventes » = orders.
    protected const string BoutiqueTables = """
        [{"id":"t1","name":"customers","tableType":"Table","description":"Les clients","columns":[
            {"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0},
            {"id":"c2","name":"email","dataType":"TEXT","position":1,"isSensitive":true}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c3","relationType":"One-to-Many","metaInfo":"auto:fk"}]},
         {"id":"t2","name":"orders","tableType":"Table","columns":[
            {"id":"c3","name":"customer_id","dataType":"INTEGER","position":0}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c3","relationType":"One-to-Many","metaInfo":"auto:fk"}]},
         {"id":"t3","name":"tags","tableType":"View","columns":[{"id":"c4","name":"label","dataType":"TEXT","position":0}],"indexes":[],"relations":[]}]
        """;

    protected const string RhTables = """[{"id":"t9","name":"employees","tableType":"Table","columns":[{"id":"c9","name":"name","dataType":"TEXT","position":0}],"indexes":[],"relations":[]}]""";

    protected const string BoutiqueDomains = """[{"id":"g1","databaseId":"d1","name":"Ventes","color":"#1976d2","tables":[{"tableId":"t2","x":120,"y":40}]}]""";

    protected const string Completeness = """
        {"databaseId":"d1","tables":3,"tablesDescribed":1,"columns":4,"columnsDescribed":0,"sensitiveColumns":1,"sensitiveUndescribed":1,"percent":14,
         "perTable":[{"tableId":"t1","tableName":"customers","tableDescribed":true,"columns":2,"columnsDescribed":0,"sensitiveColumns":1,"sensitiveUndescribed":1,"percent":33},
                     {"tableId":"t2","tableName":"orders","tableDescribed":false,"columns":1,"columnsDescribed":0,"sensitiveColumns":0,"sensitiveUndescribed":0,"percent":0}]}
        """;

    protected const string TagLinks = """[{"tagId":"g1","tagName":"RGPD","color":null,"entityType":"table","entityId":"t1"}]""";

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        if (Override(request) is { } custom) return custom;
        var path = request.RequestUri!.AbsolutePath;
        // Comme l'API : une création (POST sur la collection) répond 201, les autres écritures 200.
        if (request.Method != HttpMethod.Get)
            return FakeApi.Json("{}", request.Method == HttpMethod.Post && System.Text.RegularExpressions.Regex.IsMatch(path,
                "^/api/v1/(DomainInfo|RelationInfo|TagInfo|Webhooks|ColumnInfo|TableInfo|DatabaseInfo)$") ? HttpStatusCode.Created : HttpStatusCode.OK);
        return path switch
        {
            "/api/v1/DatabaseInfo" => FakeApi.Json(Databases),
            "/api/v1/DatabaseInfo/d1" => FakeApi.Json("""{"id":"d1","name":"Boutique","type":"Sqlite","description":"Ma boutique"}"""),
            "/api/v1/DatabaseInfo/d2" => FakeApi.Json("""{"id":"d2","name":"RH","type":"SqlServer"}"""),
            "/api/v1/TableInfo/database/d1" => FakeApi.Json(BoutiqueTables),
            "/api/v1/TableInfo/database/d2" => FakeApi.Json(RhTables),
            "/api/v1/DomainInfo/database/d1" => FakeApi.Json(BoutiqueDomains),
            "/api/v1/DomainInfo/database/d2" => FakeApi.Json("[]"),
            "/api/v1/TagInfo/database/d1/links" => FakeApi.Json(TagLinks),
            "/api/v1/Completeness/database/d1" => FakeApi.Json(Completeness),
            "/api/v1/Impact/table/t1" => FakeApi.Json("""{"root":{"tableId":"t1","tableName":"customers","columnId":null,"columnName":null,"depth":0},"dependents":[{"tableId":"t2","tableName":"orders","columnId":null,"columnName":null,"depth":1}],"dependencies":[]}"""),
            "/api/v1/Impact/column/c2" => FakeApi.Json("""{"root":{"tableId":"t1","tableName":"customers","columnId":"c2","columnName":"email","depth":0},"dependents":[],"dependencies":[]}"""),
            "/api/v1/Search" => FakeApi.Json("""
                [{"kind":"table","id":"t1","name":"customers","databaseId":"d1","databaseName":"Boutique","tableId":"t1","tableName":"customers"},
                 {"kind":"column","id":"c3","name":"customer_id","databaseId":"d1","databaseName":"Boutique","tableId":"t2","tableName":"orders"}]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    protected IEnumerable<HttpRequestMessage> Requests(HttpMethod method, string pathAndQuery) =>
        Api.Requests.Where(r => r.Method == method && r.RequestUri!.PathAndQuery == pathAndQuery);

    protected bool WasSent(HttpMethod method, string pathAndQuery) => Requests(method, pathAndQuery).Any();

    protected JsonElement BodyOf(HttpMethod method, string pathAndQuery)
    {
        var request = Requests(method, pathAndQuery).Last();
        var body = Api.Bodies[Api.Requests.IndexOf(request)];
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }
}
