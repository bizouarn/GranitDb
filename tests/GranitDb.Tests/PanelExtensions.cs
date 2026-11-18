using AngleSharp.Dom;
using Bunit;

namespace GranitDb.Tests;

internal static class PanelExtensions
{
    /// <summary>Déplie tous les panneaux de tables : leur contenu n'est rendu qu'une fois ouvert (bases de plusieurs centaines de tables).</summary>
    public static void ExpandAllTables<T>(this IRenderedComponent<T> page) where T : Microsoft.AspNetCore.Components.IComponent
    {
        foreach (var header in page.FindAll(".mud-expand-panel-header")) header.Click();
    }
}
