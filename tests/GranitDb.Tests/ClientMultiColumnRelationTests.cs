using GranitDb.Client.Diagram;
using GranitDb.Client.Models;

namespace GranitDb.Tests;

/// <summary>Relations multi-colonnes dans le modèle du diagramme : un seul lien, toutes les colonnes montrées.</summary>
public class ClientMultiColumnRelationTests
{
    private static ColumnInfo Col(string id, string table, string name, int pos) =>
        new() { Id = id, TableId = table, Name = name, DataType = "INTEGER", Position = pos };

    private static TableInfo[] Composite()
    {
        var rel = new RelationInfo
        {
            Id = "r1", FirstId = "p-a", SecondId = "c-pa", RelationType = "One-to-Many",
            ExtraColumns = [new RelationColumnPair { FirstId = "p-b", SecondId = "c-pb" }]
        };
        return
        [
            new TableInfo { Id = "t-p", Name = "parent", Columns = [Col("p-a", "t-p", "a", 0), Col("p-b", "t-p", "b", 1), Col("p-x", "t-p", "x", 2)], Relations = [rel] },
            new TableInfo { Id = "t-c", Name = "child", Columns = [Col("c-pa", "t-c", "pa", 0), Col("c-pb", "t-c", "pb", 1), Col("c-y", "t-c", "y", 2)], Relations = [rel] }
        ];
    }

    [Fact]
    public void A_composite_relation_is_one_edge_that_keeps_all_its_columns_in_a_reduced_view()
    {
        var diagram = ErDiagram.Build(Composite(), relatedColumnsOnly: true);

        var edge = Assert.Single(diagram.Edges);
        Assert.Equal(2, edge.Pairs().Count());
        Assert.Equal(["a", "b"], diagram.Node("t-p")!.Rows.Select(r => r.Name));
        Assert.Equal(["pa", "pb"], diagram.Node("t-c")!.Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_composite_edge_leaves_from_every_column_and_is_exported_as_a_single_relation()
    {
        var diagram = ErDiagram.Build(Composite(), relatedColumnsOnly: true);
        var simple = ErDiagram.Build([new TableInfo { Id = "t-p", Name = "parent", Columns = [Col("p-a", "t-p", "a", 0)] },
            new TableInfo { Id = "t-c", Name = "child", Columns = [Col("c-pa", "t-c", "pa", 0)],
                Relations = [new RelationInfo { Id = "r", FirstId = "p-a", SecondId = "c-pa", RelationType = "One-to-Many" }] }]);

        var route = diagram.Route(diagram.Edges[0])!;

        // Un trait par colonne de chaque côté et une barre : plus de segments qu'un lien simple.
        Assert.True(route.Path.Split('M').Length > simple.Route(simple.Edges[0])!.Path.Split('M').Length + 3);
        Assert.Equal(1, diagram.ToMermaid().Split('\n').Count(l => l.Contains("||--o{")));
        Assert.Contains("\"pa, pb\"", diagram.ToMermaid());
    }

    [Fact]
    public void Choosing_one_column_of_a_composite_key_points_out_all_the_others()
    {
        var diagram = ErDiagram.Build(Composite());

        Assert.Equal(new[] { "c-pa", "c-pb", "p-b" }.Order(), diagram.SiblingColumns("p-a").Order());
        Assert.Equal(new[] { "c-pa", "p-a", "p-b" }.Order(), diagram.SiblingColumns("c-pb").Order());
        Assert.Empty(diagram.SiblingColumns("c-y"));
    }

    [Fact]
    public void A_simple_relation_has_no_sibling_columns()
    {
        var diagram = ErDiagram.Build([new TableInfo { Id = "t-p", Name = "parent", Columns = [Col("p-a", "t-p", "a", 0)] },
            new TableInfo { Id = "t-c", Name = "child", Columns = [Col("c-pa", "t-c", "pa", 0)],
                Relations = [new RelationInfo { Id = "r", FirstId = "p-a", SecondId = "c-pa", RelationType = "One-to-Many" }] }]);

        Assert.Empty(diagram.SiblingColumns("c-pa"));
    }
}
