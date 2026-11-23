using System.Globalization;
using GranitDb.Client.Diagram;
using GranitDb.Client.Models;

namespace GranitDb.Tests;

/// <summary>Placement en couches du diagramme : pas de chevauchement, sens des liens, croisements, liens longs, vitesse.</summary>
public class ErLayoutTests
{
    private static TableInfo Table(string name, params string[] columns)
    {
        var cols = columns.Select((c, i) => new ColumnInfo { Id = name + "." + c, TableId = name, Name = c, DataType = "INTEGER", Position = i, IsPrimaryKey = i == 0 }).ToList();
        return new TableInfo { Id = name, Name = name, Columns = cols };
    }

    /// <summary>Graphe pseudo-aléatoire reproductible : cycles, doublons et boucles possibles (sauf <paramref name="acyclic" />).</summary>
    private static List<TableInfo> Graph(int tables, int links, int seed, bool acyclic = false)
    {
        var random = new Random(seed);
        var list = Enumerable.Range(0, tables).Select(i => Table("t" + i.ToString("D3"), "id", "a", "b", "c")).ToList();
        for (var i = 0; i < links; i++)
        {
            var referenced = random.Next(tables);
            var referencing = random.Next(tables);
            if (acyclic && referencing < referenced) (referenced, referencing) = (referencing, referenced);
            if (acyclic && referenced == referencing) continue;
            var relation = new RelationInfo
            {
                Id = "r" + i, FirstId = $"t{referenced:D3}.id", SecondId = $"t{referencing:D3}." + "abc"[random.Next(3)], RelationType = "One-to-Many"
            };
            list[referenced].Relations.Add(relation);
            if (referencing != referenced) list[referencing].Relations.Add(relation);
        }

        return list;
    }

    private static bool Overlap(ErNode a, ErNode b) =>
        a.X < b.X + b.Width - 0.5 && b.X < a.X + a.Width - 0.5 && a.Y < b.Y + b.Height - 0.5 && b.Y < a.Y + a.Height - 0.5;

    [Theory]
    [InlineData(12, 15, 1)]
    [InlineData(40, 70, 2)]
    [InlineData(120, 200, 3)]
    [InlineData(60, 20, 4)]
    public void No_two_tables_overlap_whatever_the_graph(int tables, int links, int seed)
    {
        var diagram = ErDiagram.Build(Graph(tables, links, seed));

        foreach (var a in diagram.Nodes)
        foreach (var b in diagram.Nodes)
            if (string.CompareOrdinal(a.Id, b.Id) < 0)
                Assert.False(Overlap(a, b), $"{a.Name} et {b.Name} se chevauchent");
        Assert.All(diagram.Nodes, n => Assert.True(n.X >= 0 && n.Y >= 0));
    }

    [Fact]
    public void In_a_graph_without_cycles_every_referenced_table_is_left_of_the_tables_that_reference_it()
    {
        var diagram = ErDiagram.Build(Graph(80, 140, 7, true));

        foreach (var e in diagram.Edges.Where(e => e.ReferencingTableId != e.ReferencedTableId))
            Assert.True(diagram.Node(e.ReferencedTableId)!.X < diagram.Node(e.ReferencingTableId)!.X, e.RelationId);
    }

    [Fact]
    public void A_cycle_is_laid_out_and_only_one_link_goes_backwards()
    {
        var a = Table("a", "id", "b_id");
        var b = Table("b", "id", "c_id");
        var c = Table("c", "id", "a_id");
        var r1 = new RelationInfo { Id = "r1", FirstId = "a.id", SecondId = "b.id", RelationType = "One-to-Many" };
        var r2 = new RelationInfo { Id = "r2", FirstId = "b.id", SecondId = "c.id", RelationType = "One-to-Many" };
        var r3 = new RelationInfo { Id = "r3", FirstId = "c.id", SecondId = "a.id", RelationType = "One-to-Many" };
        a.Relations = [r1, r3];
        b.Relations = [r1, r2];
        c.Relations = [r2, r3];

        var diagram = ErDiagram.Build([a, b, c]);

        var backwards = diagram.Edges.Count(e => diagram.Node(e.ReferencedTableId)!.X > diagram.Node(e.ReferencingTableId)!.X);
        Assert.Equal(1, backwards);
    }

    [Fact]
    public void A_balanced_tree_has_no_crossing()
    {
        var tables = new List<TableInfo>();
        for (var i = 0; i < 63; i++) tables.Add(Table("n" + i.ToString("D2"), "id", "parent"));
        for (var i = 1; i < 63; i++)
        {
            var r = new RelationInfo { Id = "r" + i, FirstId = $"n{(i - 1) / 2:D2}.id", SecondId = $"n{i:D2}.parent", RelationType = "One-to-Many" };
            tables[(i - 1) / 2].Relations.Add(r);
            tables[i].Relations.Add(r);
        }

        Assert.Equal(0, ErDiagram.Build(tables).Crossings());
    }

    [Fact]
    public void A_mirrored_bipartite_graph_is_untangled()
    {
        // Chaque enfant référence le parent de rang inverse : l'ordre alphabétique croiserait tous les liens.
        var parents = Enumerable.Range(0, 10).Select(i => Table("p" + i, "id")).ToList();
        var children = Enumerable.Range(0, 10).Select(i => Table("q" + (9 - i), "id", "p")).ToList();
        for (var i = 0; i < 10; i++)
        {
            var r = new RelationInfo { Id = "r" + i, FirstId = $"p{i}.id", SecondId = children[i].Columns[1].Id, RelationType = "One-to-Many" };
            parents[i].Relations.Add(r);
            children[i].Relations.Add(r);
        }

        Assert.Equal(0, ErDiagram.Build(parents.Concat(children)).Crossings());
    }

    [Fact]
    public void Layout_is_deterministic()
    {
        var first = ErDiagram.Build(Graph(70, 120, 11));
        var second = ErDiagram.Build(Graph(70, 120, 11));

        Assert.Equal(first.Nodes.Select(n => (n.Id, n.X, n.Y)), second.Nodes.Select(n => (n.Id, n.X, n.Y)));
        Assert.Equal(first.Crossings(), second.Crossings());
    }

    [Fact]
    public void Loose_tables_go_below_the_linked_ones()
    {
        var tables = Graph(30, 22, 5);
        tables.Add(Table("zzz_loose", "id"));

        var diagram = ErDiagram.Build(tables);

        var loose = diagram.Node("zzz_loose")!;
        var linkedBottom = diagram.Nodes.Where(n => n.Id != "zzz_loose" && diagram.Edges.Any(e => e.ReferencedTableId == n.Id || e.ReferencingTableId == n.Id))
            .Max(n => n.Y + n.Height);
        Assert.True(loose.Y >= linkedBottom);
    }

    // --- Liens longs : ils passent entre les tables, jamais dessus ---

    private static List<(double X, double Y)> Points(string path)
    {
        var numbers = path.Replace("M", " ").Replace("L", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
        return Enumerable.Range(0, numbers.Count / 2).Select(i => (numbers[2 * i], numbers[2 * i + 1])).ToList();
    }

    private static bool Crosses(ErNode node, (double X, double Y) a, (double X, double Y) b)
    {
        // Segments horizontaux ou verticaux : l'intérieur du rectangle, à 1 px près.
        var (x1, x2) = (Math.Min(a.X, b.X), Math.Max(a.X, b.X));
        var (y1, y2) = (Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
        return x1 < node.X + node.Width - 1 && x2 > node.X + 1 && y1 < node.Y + node.Height - 1 && y2 > node.Y + 1;
    }

    [Theory]
    [InlineData(25, 40, 21)]
    [InlineData(60, 110, 22)]
    [InlineData(100, 180, 23)]
    public void Links_never_cross_a_table_even_between_distant_columns(int tables, int links, int seed)
    {
        var diagram = ErDiagram.Build(Graph(tables, links, seed, true));
        var longLinks = 0;

        foreach (var edge in diagram.Edges)
        {
            var from = diagram.Node(edge.ReferencingTableId)!;
            var to = diagram.Node(edge.ReferencedTableId)!;
            if (Math.Abs(from.X - to.X) >= 2 * (from.Width + 90)) longLinks++;
            var points = Points(diagram.Route(edge)!.Path);
            for (var i = 1; i < points.Count; i++)
                foreach (var node in diagram.Nodes.Where(n => n != from && n != to))
                    Assert.False(Crosses(node, points[i - 1], points[i]), $"{edge.RelationId} traverse {node.Name} [{node.X:0},{node.Y:0},{node.Width:0},{node.Height:0}] seg {points[i - 1]}->{points[i]} path {diagram.Route(edge)!.Path} from[{from.Name} {from.X:0},{from.Y:0}] to[{to.Name} {to.X:0},{to.Y:0}]");
        }

        Assert.True(longLinks > 0, "le jeu d'essai doit contenir des liens longs");
    }

    [Fact]
    public void A_moved_table_drops_the_waypoints_of_its_links()
    {
        var diagram = ErDiagram.Build(Graph(60, 110, 22, true));
        var longEdge = diagram.Edges.First(e => Math.Abs(diagram.Node(e.ReferencingTableId)!.X - diagram.Node(e.ReferencedTableId)!.X) > 600);
        var before = diagram.Route(longEdge)!.Path;

        diagram.Node(longEdge.ReferencingTableId)!.Y += 40;
        var after = diagram.Route(longEdge)!.Path;

        Assert.NotEqual(before, after);
        Assert.True(Points(after).Count <= 4); // retour au tracé direct
    }

    // --- Vitesse ---

    [Theory]
    [InlineData(300, 600)]
    [InlineData(850, 1800)]
    public void A_large_database_is_laid_out_in_a_few_seconds(int tables, int links)
    {
        var graph = Graph(tables, links, 99);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var diagram = ErDiagram.Build(graph);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Placement de {tables} tables : {watch.Elapsed}");
        Assert.Equal(tables, diagram.Nodes.Count);
    }
}
