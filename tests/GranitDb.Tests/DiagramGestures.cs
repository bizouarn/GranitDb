using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using GranitDb.Client.Pages;
using Microsoft.AspNetCore.Components.Web;
using Wpflow.Blazor;

namespace GranitDb.Tests;

/// <summary>
///     Gestes sur le diagramme. Dans Wpflow.Blazor, l'appui est un évènement Blazor ; le déplacement et le relâchement
///     arrivent par son pont JavaScript (absent sous bUnit) : on appelle donc directement ce que le pont appelle.
/// </summary>
internal static class DiagramGestures
{
    private static FlowView Flow(IRenderedComponent<Diagram> page) => page.FindComponent<FlowView>().Instance;

    public static void Press(IRenderedComponent<Diagram> page, string selector, double x = 10, double y = 10)
    {
        SendPressData(page, selector);
        page.Find(selector).PointerDown(new PointerEventArgs { ClientX = x, ClientY = y });
    }

    /// <summary>
    ///     Comme le pont JavaScript avant l'évènement d'appui : les attributs <c>data-*</c> de l'élément pressé et de ses
    ///     parents dans le nœud (c'est ce que Wpflow donne ensuite dans <c>NodeEventArgs.TargetData</c>).
    /// </summary>
    private static void SendPressData(IRenderedComponent<Diagram> page, string selector)
    {
        var data = new Dictionary<string, string>();
        var element = page.Find(selector);
        while (element != null && !element.ClassList.Contains("wf-node"))
        {
            foreach (var attribute in element.Attributes.Where(a => a.Name.StartsWith("data-", StringComparison.Ordinal)))
                data.TryAdd(attribute.Name["data-".Length..], attribute.Value);
            element = element.ParentElement;
        }

        if (element?.GetAttribute("data-node-id") is not { } nodeId) return;
        var flow = Flow(page);
        page.InvokeAsync(() => flow.OnPressData(nodeId, data)).GetAwaiter().GetResult();
    }

    /// <summary>Deux clics puis le <c>dblclick</c> du navigateur.</summary>
    public static void DoubleClick(IRenderedComponent<Diagram> page, string selector)
    {
        Click(page, selector);
        Click(page, selector);
        page.Find(selector).DoubleClick();
    }

    public static void Move(IRenderedComponent<Diagram> page, double x, double y)
    {
        var flow = Flow(page);
        page.InvokeAsync(() => flow.OnPointerMove(x, y, false, false)).GetAwaiter().GetResult();
    }

    public static void Release(IRenderedComponent<Diagram> page, double x = 10, double y = 10)
    {
        var flow = Flow(page);
        page.InvokeAsync(() => flow.OnPointerUp(x, y, false, false)).GetAwaiter().GetResult();
    }

    public static void Click(IRenderedComponent<Diagram> page, string selector)
    {
        Press(page, selector);
        Release(page);
    }

    public static void ClickTable(IRenderedComponent<Diagram> page, string table) => Click(page, Table(table));

    public static void ClickBackground(IRenderedComponent<Diagram> page) => Click(page, ".wf-flow");

    public static void Drag(IRenderedComponent<Diagram> page, string table, (double X, double Y) from, (double X, double Y) to)
    {
        Press(page, Table(table), from.X, from.Y);
        Move(page, to.X, to.Y);
        Release(page, to.X, to.Y);
    }

    /// <summary>Annonce la place du dessin à l'écran (ce que le pont JavaScript mesure dans un navigateur).</summary>
    public static void Measure(IRenderedComponent<Diagram> page, double width = 800, double height = 600)
    {
        var flow = Flow(page);
        page.InvokeAsync(() => flow.OnRootRect(0, 0, width, height)).GetAwaiter().GetResult();
    }

    public static string Table(string table) => $".er-node[data-table='{table}']";

    /// <summary>La place d'une table dans le dessin, lue sur le nœud de Wpflow qui la porte.</summary>
    public static (double X, double Y) Position(IRenderedComponent<Diagram> page, string table)
    {
        var style = page.Find(Table(table)).Closest(".wf-node")!.GetAttribute("style")!;
        var match = Regex.Match(style, @"translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)");
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>L'échelle à laquelle le dessin est affiché.</summary>
    public static double Scale(IRenderedComponent<Diagram> page)
    {
        var style = page.Find(".wf-viewport").GetAttribute("style")!;
        return double.Parse(Regex.Match(style, @"scale\(([\d.]+)\)").Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
