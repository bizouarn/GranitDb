using GranitDb.Client.Workspace;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bunit;
using GranitDb.Client.Diagram;
using GranitDb.Client.Models;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace GranitDb.Tests;

/// <summary>Jalon 7 : modèle du diagramme entité-relation (placement, exports SVG et Mermaid).</summary>
public class ErDiagramTests
{
    private static ColumnInfo Col(string id, string table, string name, string type = "INTEGER", bool pk = false, int pos = 0)
    {
        return new ColumnInfo { Id = id, TableId = table, Name = name, DataType = type, IsPrimaryKey = pk, Position = pos };
    }

    private static RelationInfo Rel(string id, string referenced, string referencing, string type = "One-to-Many")
    {
        return new RelationInfo { Id = id, FirstId = referenced, SecondId = referencing, RelationType = type };
    }

    /// <summary>customers ← orders ← order_lines, plus une table isolée.</summary>
    private static TableInfo[] Shop()
    {
        var r1 = Rel("r1", "c-id", "o-cust");
        var r2 = Rel("r2", "o-id", "l-order");
        return
        [
            new TableInfo { Id = "t-c", Name = "customers", Columns = [Col("c-id", "t-c", "id", pk: true), Col("c-mail", "t-c", "email", "TEXT", pos: 1)], Relations = [r1] },
            new TableInfo { Id = "t-o", Name = "orders", Columns = [Col("o-id", "t-o", "id", pk: true), Col("o-cust", "t-o", "customer_id", pos: 1)], Relations = [r1, r2] },
            new TableInfo { Id = "t-l", Name = "order_lines", Columns = [Col("l-order", "t-l", "order_id")], Relations = [r2] },
            new TableInfo { Id = "t-g", Name = "tags", TableType = "View", Columns = [Col("g-id", "t-g", "id")] }
        ];
    }

    [Fact]
    public void A_referenced_table_is_placed_left_of_the_tables_that_reference_it()
    {
        var diagram = ErDiagram.Build(Shop());

        var customers = diagram.Node("t-c")!;
        var orders = diagram.Node("t-o")!;
        var lines = diagram.Node("t-l")!;
        Assert.True(customers.X < orders.X);
        Assert.True(orders.X < lines.X);
    }

    [Fact]
    public void Tables_never_overlap_after_the_automatic_layout()
    {
        var diagram = ErDiagram.Build(Shop());

        foreach (var a in diagram.Nodes)
        foreach (var b in diagram.Nodes.Where(b => b.Id != a.Id))
        {
            var overlap = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
            Assert.False(overlap, $"{a.Name} et {b.Name} se chevauchent");
        }
    }

    [Fact]
    public void Tables_without_relations_go_below_the_graph()
    {
        var diagram = ErDiagram.Build(Shop());

        var graphBottom = new[] { "t-c", "t-o", "t-l" }.Max(id => diagram.Node(id)!.Y + diagram.Node(id)!.Height);
        Assert.True(diagram.Node("t-g")!.Y >= graphBottom);
    }

    [Fact]
    public void A_relation_is_kept_once_even_though_both_tables_carry_it()
    {
        var diagram = ErDiagram.Build(Shop());

        Assert.Equal(["r1", "r2"], diagram.Edges.Select(e => e.RelationId).ToArray());
        var edge = diagram.Edges.Single(e => e.RelationId == "r1");
        Assert.Equal(("t-o", "t-c"), (edge.ReferencingTableId, edge.ReferencedTableId));
    }

    [Fact]
    public void A_subset_of_tables_keeps_only_the_relations_between_them()
    {
        var diagram = ErDiagram.Build(Shop(), new HashSet<string> { "t-c", "t-l" });

        Assert.Equal(["customers", "order_lines"], diagram.Nodes.Select(n => n.Name).ToArray());
        Assert.Empty(diagram.Edges);
    }

    [Fact]
    public void A_relation_loop_and_a_self_reference_do_not_hang_the_layout()
    {
        var tables = new[]
        {
            new TableInfo { Id = "a", Name = "a", Columns = [Col("a1", "a", "x"), Col("a2", "a", "parent")], Relations = [Rel("r1", "a1", "a2")] },
            new TableInfo { Id = "b", Name = "b", Columns = [Col("b1", "b", "x")], Relations = [] }
        };
        tables[0].Relations.Add(Rel("r2", "a1", "b1"));
        tables[1].Relations.Add(Rel("r3", "b1", "a2"));

        var diagram = ErDiagram.Build(tables);

        Assert.Equal(3, diagram.Edges.Count);
        Assert.All(diagram.Edges, e => Assert.NotNull(diagram.Route(e)));
    }

    [Fact]
    public void Route_paths_use_the_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            var diagram = ErDiagram.Build(Shop());
            diagram.Node("t-o")!.Y += 0.5;

            var path = diagram.Route(diagram.Edges[0])!.Path;

            Assert.DoesNotContain(",", path.Replace(", ", " "));
            Assert.StartsWith("M ", path);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Moving_a_table_moves_the_end_of_its_relations_and_the_drawing_size_follows()
    {
        var diagram = ErDiagram.Build(Shop());
        var before = diagram.Route(diagram.Edges.Single(e => e.RelationId == "r1"))!;
        var sizeBefore = diagram.Size();

        diagram.Node("t-o")!.Y += 500;

        var after = diagram.Route(diagram.Edges.Single(e => e.RelationId == "r1"))!;
        // Très écartée en hauteur, la relation passe par les bords haut/bas des tables : son extrémité suit quand même la table.
        Assert.True(after.Y1 > before.Y1 + 400);
        Assert.True(diagram.Size().Height > sizeBefore.Height);
    }

    [Fact]
    public void Svg_export_is_self_contained_and_escapes_names()
    {
        var tables = Shop();
        tables[3].Name = "a<b>&\"c";
        var svg = ErDiagram.Build(tables).ToSvg();

        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("customers", svg);
        Assert.Contains("a&lt;b&gt;&amp;&quot;c", svg);
        Assert.DoesNotContain("var(--", svg); // pas de dépendance au thème de l'application
        var document = new System.Xml.XmlDocument();
        document.LoadXml(svg); // bien formé
        Assert.Equal(4, document.GetElementsByTagName("g").Count);
        Assert.Equal(2, document.GetElementsByTagName("path").Count);
    }

    [Fact]
    public void Mermaid_export_lists_entities_keys_and_relations()
    {
        var mermaid = ErDiagram.Build(Shop()).ToMermaid();

        Assert.StartsWith("erDiagram\n", mermaid);
        Assert.Contains("    customers {\n        INTEGER id PK\n        TEXT email\n    }", mermaid);
        Assert.Contains("INTEGER customer_id FK", mermaid);
        Assert.Contains("customers ||--o{ orders : \"customer_id\"", mermaid);
        Assert.Contains("orders ||--o{ order_lines : \"order_id\"", mermaid);
    }

    [Fact]
    public void Mermaid_names_are_reduced_to_what_mermaid_accepts_and_stay_unique()
    {
        var tables = new[]
        {
            new TableInfo { Id = "1", Name = "sales.Orders", Columns = [Col("a", "1", "order id", "VARCHAR(30)"), Col("b", "1", "order-id", "")] },
            new TableInfo { Id = "2", Name = "sales_Orders", Columns = [Col("c", "2", "1st")] }
        };

        var mermaid = ErDiagram.Build(tables).ToMermaid();

        Assert.Contains("sales_Orders {", mermaid);
        Assert.Contains("sales_Orders_ {", mermaid);
        Assert.Contains("VARCHAR_30 order_id", mermaid);
        Assert.Contains("unknown order_id_", mermaid);
        Assert.Contains("_1st", mermaid);
    }

    [Fact]
    public void Mermaid_relation_symbols_follow_the_relation_type()
    {
        var tables = new[]
        {
            new TableInfo { Id = "1", Name = "a", Columns = [Col("a", "1", "id")], Relations = [Rel("r1", "a", "b", "One-to-One"), Rel("r2", "a", "c", "Many-to-Many")] },
            new TableInfo { Id = "2", Name = "b", Columns = [Col("b", "2", "a_id")] },
            new TableInfo { Id = "3", Name = "c", Columns = [Col("c", "3", "a_id")] }
        };
        tables[1].Relations.Add(tables[0].Relations[0]);
        tables[2].Relations.Add(tables[0].Relations[1]);

        var mermaid = ErDiagram.Build(tables).ToMermaid();

        Assert.Contains("a ||--o| b", mermaid);
        Assert.Contains("a }o--o{ c", mermaid);
    }

    [Fact]
    public void An_empty_diagram_exports_without_failing()
    {
        var diagram = ErDiagram.Build([]);

        Assert.Equal("erDiagram\n", diagram.ToMermaid());
        Assert.Contains("<svg", diagram.ToSvg());
        Assert.Empty(diagram.Nodes);
    }
}

/// <summary>Page du diagramme : sélection des tables, déplacement, analyse d'impact, exports.</summary>
public class ClientDiagramPageTests : IAsyncLifetime
{
    private readonly BunitContext _ctx = new();
    private readonly FakeApi _api = new();

    public ClientDiagramPageTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddMudServices();
        _ctx.Services.AddGranitWorkspace();
        _ctx.Services.AddScoped(_ => _api.Client);
        _ctx.Render<MudBlazor.MudPopoverProvider>();
        _api.Respond = Serve;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    private const string Tables = """
        [{"id":"t1","name":"customers","tableType":"Table","columns":[{"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c2","relationType":"One-to-Many"}]},
         {"id":"t2","name":"orders","tableType":"Table","columns":[{"id":"c2","name":"customer_id","dataType":"INTEGER","position":0}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c2","relationType":"One-to-Many"}]},
         {"id":"t3","name":"tags","tableType":"Table","columns":[{"id":"c3","name":"label","dataType":"TEXT","position":0}],"indexes":[],"relations":[]}]
        """;

    private const string CustomersImpact = """
        {"root":{"tableId":"t1","tableName":"customers","columnId":null,"columnName":null,"depth":0},
         "dependents":[{"tableId":"t2","tableName":"orders","columnId":null,"columnName":null,"depth":1}],"dependencies":[]}
        """;

    private HttpResponseMessage Serve(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            _ when path.StartsWith("/api/v1/TableInfo/database/") => FakeApi.Json(Tables),
            "/api/v1/Impact/table/t1" => FakeApi.Json(CustomersImpact),
            "/api/v1/Impact/column/c2" => FakeApi.Json("""
                {"root":{"tableId":"t2","tableName":"orders","columnId":"c2","columnName":"customer_id","depth":0},
                 "dependents":[],"dependencies":[{"tableId":"t1","tableName":"customers","columnId":"c1","columnName":"id","depth":1}]}
                """),
            _ when path.StartsWith("/api/v1/Impact/") => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ when path.StartsWith("/api/v1/DatabaseInfo/") => FakeApi.Json("""{"id":"db","name":"Boutique","type":"Sqlite"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private IRenderedComponent<Diagram> Open()
    {
        var page = _ctx.Render<Diagram>(p => p.Add(x => x.Id, "db"));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("g.er-node")));
        return page;
    }

    /// <summary>Décoche la case de la table dans la liste des tables affichées.</summary>
    private static void Uncheck(IRenderedComponent<Diagram> page, string table)
    {
        page.FindAll("label").First(l => l.TextContent.Trim() == table).QuerySelector("input")!.Change(false);
    }

    private static string Translate(IRenderedComponent<Diagram> page, string table)
    {
        return page.Find($"g.er-node[data-table='{table}']").GetAttribute("transform")!;
    }

    [BunitFact]
    public void The_diagram_shows_every_table_with_its_columns_and_a_link_between_related_tables()
    {
        var page = Open();

        Assert.Equal(["customers", "orders", "tags"],
            page.FindAll("g.er-node").Select(n => n.GetAttribute("data-table")).Order().ToArray());
        Assert.NotNull(page.Find("g.er-row[data-column='customer_id']"));
        Assert.Single(page.FindAll("path.er-edge"));
        Assert.Contains("Tables affichées (3 / 3)", page.Markup);
    }

    [BunitFact]
    public void Unchecking_a_table_removes_it_and_the_relations_that_touch_it()
    {
        var page = Open();

        Uncheck(page, "orders");

        page.WaitForAssertion(() =>
        {
            Assert.Equal(2, page.FindAll("g.er-node").Count);
            Assert.Empty(page.FindAll("path.er-edge"));
            Assert.Contains("Tables affichées (2 / 3)", page.Markup);
        });
    }

    [BunitFact]
    public void Clicking_a_table_shows_what_depends_on_it_and_highlights_it()
    {
        var page = Open();

        var customers = page.Find("g.er-node[data-table='customers']");
        customers.PointerDown(new PointerEventArgs { ClientX = 10, ClientY = 10 });
        customers.PointerUp(new PointerEventArgs { ClientX = 10, ClientY = 10 });

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Impact de customers", page.Find("#impact").TextContent);
            Assert.Contains("orders", page.Find("#impact").TextContent);
            Assert.Contains("direct", page.Find("#impact").TextContent);
        });
        Assert.Contains("er-selected", page.Find("g.er-node[data-table='customers']").ClassName);
        Assert.Contains("er-dependent", page.Find("g.er-node[data-table='orders']").ClassName);
        Assert.Contains("er-edge-active", page.Find("path.er-edge").ClassName);
        Assert.Contains(_api.Requests, r => r.RequestUri!.AbsolutePath == "/api/v1/Impact/table/t1");
    }

    [BunitFact]
    public void Clicking_a_column_shows_the_impact_of_that_column()
    {
        var page = Open();

        var column = page.Find("g.er-row[data-column='customer_id']");
        column.PointerDown(new PointerEventArgs { ClientX = 5, ClientY = 5 });
        column.PointerUp(new PointerEventArgs { ClientX = 5, ClientY = 5 });

        page.WaitForAssertion(() => Assert.Contains("Impact de orders.customer_id", page.Find("#impact").TextContent));
        Assert.Contains("customers.id", page.Find("#impact").TextContent);
        Assert.Contains("er-selected", page.Find("g.er-row[data-column='customer_id']").ClassName);
        Assert.Contains("er-dependency", page.Find("g.er-row[data-column='id']").ClassName);
    }

    [BunitFact]
    public void Dragging_a_table_moves_it_without_selecting_it()
    {
        var page = Open();
        var before = Translate(page, "tags");

        var tags = page.Find("g.er-node[data-table='tags']");
        tags.PointerDown(new PointerEventArgs { ClientX = 100, ClientY = 100 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 260, ClientY = 180 });
        page.Find("g.er-node[data-table='tags']").PointerUp(new PointerEventArgs { ClientX = 260, ClientY = 180 });

        Assert.NotEqual(before, Translate(page, "tags"));
        Assert.DoesNotContain("er-selected", page.Find("g.er-node[data-table='tags']").ClassName);
        Assert.DoesNotContain(_api.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/Impact/"));
    }

    [BunitFact]
    public void A_moved_table_keeps_its_place_when_another_table_is_toggled()
    {
        var page = Open();
        page.Find("g.er-node[data-table='tags']").PointerDown(new PointerEventArgs { ClientX = 100, ClientY = 100 });
        page.Find("svg.er-svg").PointerMove(new PointerEventArgs { ClientX = 300, ClientY = 300 });
        page.Find("g.er-node[data-table='tags']").PointerUp(new PointerEventArgs { ClientX = 300, ClientY = 300 });
        var moved = Translate(page, "tags");

        Uncheck(page, "orders");

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("g.er-node").Count));
        Assert.Equal(moved, Translate(page, "tags"));
    }

    [BunitFact]
    public void Export_buttons_hand_the_svg_mermaid_and_png_to_the_browser()
    {
        var page = Open();

        page.FindAll("button").First(b => b.TextContent.Trim() == "SVG").Click();
        page.FindAll("button").First(b => b.TextContent.Trim() == "Mermaid").Click();
        page.FindAll("button").First(b => b.TextContent.Trim() == "PNG").Click();
        page.FindAll("button").First(b => b.TextContent.Contains("Copier")).Click();

        var calls = _ctx.JSInterop.Invocations.ToList();
        var svg = calls.Single(c => c.Identifier == "granitdb.download" && (string?)c.Arguments[0] == "Boutique.svg");
        Assert.StartsWith("<svg", (string)svg.Arguments[2]!);
        var mermaid = calls.Single(c => c.Identifier == "granitdb.download" && (string?)c.Arguments[0] == "Boutique.mmd");
        Assert.StartsWith("erDiagram", (string)mermaid.Arguments[2]!);
        Assert.Contains(calls, c => c.Identifier == "granitdb.downloadPng" && (string?)c.Arguments[0] == "Boutique.png");
        Assert.Contains(calls, c => c.Identifier == "granitdb.copyText" && ((string)c.Arguments[0]!).StartsWith("erDiagram"));
    }

    [BunitFact]
    public void Exports_only_contain_the_tables_that_are_shown()
    {
        var page = Open();
        Uncheck(page, "tags");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("g.er-node").Count));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Mermaid").Click();

        var mermaid = (string)_ctx.JSInterop.Invocations.Single(c => c.Identifier == "granitdb.download").Arguments[2]!;
        Assert.DoesNotContain("tags", mermaid);
        Assert.Contains("customers", mermaid);
    }

    [BunitFact]
    public void Keeping_only_the_neighbours_of_the_selection_hides_the_unrelated_tables()
    {
        var page = Open();
        var customers = page.Find("g.er-node[data-table='customers']");
        customers.PointerDown(new PointerEventArgs { ClientX = 1, ClientY = 1 });
        customers.PointerUp(new PointerEventArgs { ClientX = 1, ClientY = 1 });
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#impact")));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Voisines").Click();

        page.WaitForAssertion(() => Assert.Equal(["customers", "orders"],
            page.FindAll("g.er-node").Select(n => n.GetAttribute("data-table")).Order().ToArray()));
    }

    [BunitFact]
    public void Zoom_buttons_scale_the_drawing_within_limits()
    {
        var page = Open();
        var width = double.Parse(page.Find("svg.er-svg").GetAttribute("width")!, CultureInfo.InvariantCulture);

        page.Find("button[title='Zoom avant']").Click();
        var zoomed = double.Parse(page.Find("svg.er-svg").GetAttribute("width")!, CultureInfo.InvariantCulture);
        for (var i = 0; i < 40; i++) page.Find("button[title='Zoom arrière']").Click();
        var minimum = double.Parse(page.Find("svg.er-svg").GetAttribute("width")!, CultureInfo.InvariantCulture);

        Assert.True(zoomed > width);
        Assert.Equal(width * 0.3, minimum, 0);
        Assert.Contains("30 %", page.Markup);
    }

    [BunitFact]
    public void An_unknown_database_shows_a_warning_instead_of_a_diagram()
    {
        _api.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var page = _ctx.Render<Diagram>(p => p.Add(x => x.Id, "nope"));

        page.WaitForAssertion(() => Assert.Contains("Base introuvable", page.Markup));
        Assert.Empty(page.FindAll("svg.er-svg"));
    }

    [BunitFact]
    public void The_detail_page_links_to_the_diagram()
    {
        var detail = _ctx.Render<DatabaseDetail>(p => p.Add(x => x.Id, "db"));

        detail.WaitForAssertion(() => Assert.NotEmpty(detail.FindAll("a[href='/databases/db/diagram']")));
    }
}

public class ClientImpactApiTests
{
    [Fact]
    public async Task GetImpactAsync_reads_the_versioned_endpoint_and_returns_null_when_unknown()
    {
        var api = new FakeApi
        {
            Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/known")
                ? FakeApi.Json("""{"root":{"tableId":"t","tableName":"T","columnId":null,"columnName":null,"depth":0},"dependents":[{"tableId":"u","tableName":"U","columnId":"c","columnName":"x","depth":2}],"dependencies":[]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)
        };

        var known = await api.Client.GetImpactAsync("column", "known");
        var unknown = await api.Client.GetImpactAsync("table", "a/b");

        Assert.Equal("http://api.test/api/v1/Impact/column/known", api.Requests[0].RequestUri!.ToString());
        Assert.Equal("http://api.test/api/v1/Impact/table/a%2Fb", api.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal(2, Assert.Single(known!.Dependents).Depth);
        Assert.Equal("x", known.Dependents[0].ColumnName);
        Assert.Null(unknown);
        _ = JsonSerializer.Serialize(known); // sérialisable (utile aux tests de l'interface)
    }
}
