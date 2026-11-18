using System.Globalization;
using System.Text;
using GranitDb.Client.Models;

namespace GranitDb.Client.Diagram;

/// <summary>Une colonne affichée dans une table du diagramme.</summary>
public record ErRow(string ColumnId, string Name, string DataType, bool IsPrimaryKey, bool IsForeignKey);

/// <summary>Une table du diagramme, à la position (<see cref="X" />, <see cref="Y" />) ; déplaçable.</summary>
public class ErNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string TableType { get; init; } = "Table";

    /// <summary>Table d'un autre domaine, reliée au domaine affiché : réduite aux colonnes de ses relations.</summary>
    public bool IsFrontier { get; init; }

    /// <summary>Première ligne de la description de la table, sous son nom (comme la documentation Dataedo).</summary>
    public string? Caption { get; init; }

    public double HeaderHeight => Caption == null ? ErDiagram.HeaderHeight : ErDiagram.HeaderHeight + ErDiagram.CaptionHeight;

    public IReadOnlyList<ErRow> Rows { get; init; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; init; }
    public double Height { get; init; }

    public double RowY(int index)
    {
        return Y + HeaderHeight + (index + 0.5) * ErDiagram.RowHeight;
    }
}

/// <summary>Une relation : la colonne qui référence pointe vers la colonne référencée.</summary>
public record ErEdge(
    string RelationId,
    string ReferencingTableId,
    string ReferencingColumnId,
    string ReferencedTableId,
    string ReferencedColumnId,
    string RelationType);

/// <summary>Tracé d'une relation : points d'attache et courbe de Bézier.</summary>
public record ErRoute(double X1, double Y1, double X2, double Y2, string Path, bool Vertical = false);

/// <summary>
///     Modèle d'un diagramme entité-relation : tables, relations, placement automatique et exports (SVG autonome,
///     Mermaid). Pur calcul, indépendant de l'affichage : l'interface déplace les nœuds en modifiant X et Y.
/// </summary>
public class ErDiagram
{
    public const double HeaderHeight = 30;
    public const double CaptionHeight = 16;
    public const int MaxCaption = 34;
    public const double RowHeight = 20;
    public const double Margin = 24;
    private const double ColumnGap = 90;
    private const double RowGap = 36;

    private readonly Dictionary<string, ErNode> _byId;

    private ErDiagram(List<ErNode> nodes, List<ErEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
        _byId = nodes.ToDictionary(n => n.Id);
    }

    public IReadOnlyList<ErNode> Nodes { get; }
    public IReadOnlyList<ErEdge> Edges { get; }

    public ErNode? Node(string id)
    {
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>
    ///     Construit le diagramme des tables dont l'identifiant est dans <paramref name="visible" /> (toutes si
    ///     <c>null</c>). Seules les relations entre deux tables retenues sont gardées. Les nœuds sont placés.
    ///     Les tables de <paramref name="frontier" /> (vue d'un domaine) ne montrent que les colonnes de leurs relations.
    /// </summary>
    public static ErDiagram Build(IEnumerable<TableInfo> tables, ISet<string>? visible = null,
        ISet<string>? frontier = null, bool relatedColumnsOnly = false)
    {
        var chosen = tables.Where(t => visible == null || visible.Contains(t.Id)).OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var tableOfColumn = chosen.SelectMany(t => t.Columns.Select(c => (Column: c.Id, Table: t.Id)))
            .ToDictionary(x => x.Column, x => x.Table);

        // Chaque relation est portée par les deux tables qu'elle touche : on la garde une fois.
        var edges = chosen.SelectMany(t => t.Relations).GroupBy(r => r.Id).Select(g => g.First())
            .Where(r => tableOfColumn.ContainsKey(r.FirstId) && tableOfColumn.ContainsKey(r.SecondId))
            .Select(r => new ErEdge(r.Id, tableOfColumn[r.SecondId], r.SecondId, tableOfColumn[r.FirstId], r.FirstId,
                r.RelationType))
            .OrderBy(e => e.RelationId, StringComparer.Ordinal).ToList();

        var foreignKeys = edges.Select(e => e.ReferencingColumnId).ToHashSet();
        var related = edges.SelectMany(e => new[] { e.ReferencingColumnId, e.ReferencedColumnId }).ToHashSet();
        var nodes = chosen.Select(t =>
        {
            var isFrontier = frontier?.Contains(t.Id) == true;
            var ordered = t.Columns.OrderBy(c => c.Position).ToList();
            var shown = isFrontier || relatedColumnsOnly ? ordered.Where(c => related.Contains(c.Id)).ToList() : ordered;
            // Une table sans relation (vue réduite) garde au moins ses clés, sinon ses premières colonnes.
            if (shown.Count == 0 && !isFrontier)
                shown = ordered.Where(c => c.IsPrimaryKey).Concat(ordered.Where(c => !c.IsPrimaryKey)).Take(4).ToList();
            var rows = shown.Select(c => new ErRow(c.Id, c.Name, c.DataType, c.IsPrimaryKey, foreignKeys.Contains(c.Id))).ToList();
            var caption = isFrontier ? null : CaptionOf(t.Description);
            if (string.Equals(caption, t.Name, StringComparison.OrdinalIgnoreCase)) caption = null;
            var header = caption == null ? HeaderHeight : HeaderHeight + CaptionHeight;
            return new ErNode
            {
                Id = t.Id, Name = t.Name, TableType = t.TableType, Rows = rows, IsFrontier = isFrontier, Caption = caption,
                Width = WidthOf(t.Name, rows),
                Height = header + Math.Max(1, rows.Count) * RowHeight + 6
            };
        }).ToList();

        var diagram = new ErDiagram(nodes, edges);
        diagram.AutoLayout();
        return diagram;
    }

    /// <summary>Première ligne non vide de la description, tronquée pour tenir sous le nom de la table.</summary>
    private static string? CaptionOf(string? description)
    {
        var line = description?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(line)) return null;
        return line.Length <= MaxCaption ? line : line[..(MaxCaption - 1)] + "…";
    }

    private static double WidthOf(string name, IReadOnlyList<ErRow> rows)
    {
        var widest = Math.Max(name.Length * 8.5 + 28,
            rows.Count == 0 ? 0 : rows.Max(r => (r.Name.Length + r.DataType.Length) * 7.0 + 64));
        return Math.Clamp(Math.Ceiling(widest), 210, 380);
    }

    /// <summary>
    ///     Place les tables en colonnes : une table référencée est à gauche de celles qui la référencent. Les tables
    ///     sans relation sont rangées en grille sous le graphe.
    /// </summary>
    public void AutoLayout()
    {
        var linked = Edges.SelectMany(e => new[] { e.ReferencingTableId, e.ReferencedTableId }).ToHashSet();
        var layer = Nodes.ToDictionary(n => n.Id, _ => 0);
        // Relaxation bornée : une boucle de références ne fait pas diverger le calcul.
        for (var pass = 0; pass < Nodes.Count; pass++)
        {
            var changed = false;
            foreach (var e in Edges.Where(e => e.ReferencingTableId != e.ReferencedTableId))
            {
                var wanted = Math.Min(layer[e.ReferencedTableId] + 1, Nodes.Count);
                if (layer[e.ReferencingTableId] >= wanted) continue;
                layer[e.ReferencingTableId] = wanted;
                changed = true;
            }

            if (!changed) break;
        }

        var x = Margin;
        var bottom = Margin;
        foreach (var column in Nodes.Where(n => linked.Contains(n.Id)).GroupBy(n => layer[n.Id]).OrderBy(g => g.Key))
        {
            var y = Margin;
            foreach (var node in column)
            {
                node.X = x;
                node.Y = y;
                y += node.Height + RowGap;
            }

            x += column.Max(n => n.Width) + ColumnGap;
            bottom = Math.Max(bottom, y);
        }

        var loose = Nodes.Where(n => !linked.Contains(n.Id)).ToList();
        if (loose.Count == 0) return;
        var perRow = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(loose.Count)));
        var cx = Margin;
        var cy = bottom;
        var rowHeight = 0.0;
        for (var i = 0; i < loose.Count; i++)
        {
            if (i > 0 && i % perRow == 0)
            {
                cx = Margin;
                cy += rowHeight + RowGap;
                rowHeight = 0;
            }

            loose[i].X = cx;
            loose[i].Y = cy;
            cx += loose[i].Width + ColumnGap / 2;
            rowHeight = Math.Max(rowHeight, loose[i].Height);
        }
    }

    /// <summary>Taille du dessin : la boîte qui contient toutes les tables, marge comprise.</summary>
    public (double Width, double Height) Size()
    {
        if (Nodes.Count == 0) return (2 * Margin, 2 * Margin);
        return (Nodes.Max(n => n.X + n.Width) + Margin, Nodes.Max(n => n.Y + n.Height) + Margin);
    }

    /// <summary>Tracé d'une relation, de la ligne de la colonne qui référence à celle de la colonne référencée.</summary>
    public ErRoute? Route(ErEdge edge)
    {
        var from = Node(edge.ReferencingTableId);
        var to = Node(edge.ReferencedTableId);
        if (from == null || to == null) return null;
        var fromRow = Math.Max(0, IndexOf(from, edge.ReferencingColumnId));
        var toRow = Math.Max(0, IndexOf(to, edge.ReferencedColumnId));
        var y1 = from.RowY(fromRow);
        var y2 = to.RowY(toRow);

        // Séparation verticale plus grande que l'horizontale : liens orthogonaux bas -> haut, comme dans Dataedo.
        var hGap = Math.Max(to.X - (from.X + from.Width), from.X - (to.X + to.Width));
        var vGap = Math.Max(to.Y - (from.Y + from.Height), from.Y - (to.Y + to.Height));
        if (vGap >= 20 && vGap > hGap)
        {
            var down = from.Y < to.Y;
            var xa = Spread(from, edge.RelationId);
            var xb = Spread(to, edge.RelationId);
            var ya = down ? from.Y + from.Height : from.Y;
            var yb = down ? to.Y : to.Y + to.Height;
            var mid = (ya + yb) / 2;
            return new ErRoute(xa, ya, xb, yb, string.Create(CultureInfo.InvariantCulture,
                $"M {xa:0.#} {ya:0.#} L {xa:0.#} {mid:0.#} L {xb:0.#} {mid:0.#} L {xb:0.#} {yb:0.#}"), true);
        }

        double x1, x2, c1, c2;
        var sideBySide = true;
        if (from.X + from.Width + 20 <= to.X)
        {
            (x1, x2) = (from.X + from.Width, to.X);
            var d = Math.Max(40, (x2 - x1) / 2);
            (c1, c2) = (x1 + d, x2 - d);
        }
        else if (to.X + to.Width + 20 <= from.X)
        {
            (x1, x2) = (from.X, to.X + to.Width);
            var d = Math.Max(40, (x1 - x2) / 2);
            (c1, c2) = (x1 - d, x2 + d);
        }
        else
        {
            // Les deux tables se chevauchent en largeur (ou même table) : on sort et on rentre par la droite.
            (x1, x2) = (from.X + from.Width, to.X + to.Width);
            var bulge = Math.Max(x1, x2) + 60;
            (c1, c2) = (bulge, bulge);
            sideBySide = false;
        }

        if (sideBySide)
        {
            var xm = (x1 + x2) / 2;
            return new ErRoute(x1, y1, x2, y2, string.Create(CultureInfo.InvariantCulture,
                $"M {x1:0.#} {y1:0.#} L {xm:0.#} {y1:0.#} L {xm:0.#} {y2:0.#} L {x2:0.#} {y2:0.#}"));
        }

        return new ErRoute(x1, y1, x2, y2, string.Create(CultureInfo.InvariantCulture,
            $"M {x1:0.#} {y1:0.#} C {c1:0.#} {y1:0.#}, {c2:0.#} {y2:0.#}, {x2:0.#} {y2:0.#}"));
    }

    /// <summary>Point d'attache sur le bord haut ou bas, réparti selon la relation pour que les liens ne se superposent pas.</summary>
    private static double Spread(ErNode node, string relationId)
    {
        var hash = 17;
        foreach (var c in relationId) hash = unchecked(hash * 31 + c);
        var slot = (hash & int.MaxValue) % 7;
        return node.X + node.Width * (0.2 + 0.1 * slot);
    }

    private static int IndexOf(ErNode node, string columnId)
    {
        for (var i = 0; i < node.Rows.Count; i++)
            if (node.Rows[i].ColumnId == columnId)
                return i;
        return -1;
    }

    /// <summary>Cardinalité aux extrémités : (côté qui référence, côté référencé).</summary>
    public static (string Referencing, string Referenced) Cardinality(string relationType)
    {
        return relationType switch
        {
            "One-to-One" => ("1", "1"),
            "Many-to-Many" => ("N", "N"),
            _ => ("N", "1")
        };
    }

    // --- Exports ---

    /// <summary>SVG autonome (styles en ligne, fond blanc) : s'ouvre dans un navigateur, un éditeur ou un document.</summary>
    public string ToSvg()
    {
        var (width, height) = Size();
        var inv = CultureInfo.InvariantCulture;
        var svg = new StringBuilder();
        svg.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:0.#}\" height=\"{height:0.#}\" ")
            .Append(inv, $"viewBox=\"0 0 {width:0.#} {height:0.#}\" font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"12\">\n");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#ffffff\"/>\n");

        foreach (var edge in Edges)
        {
            var route = Route(edge);
            if (route == null) continue;
            svg.Append(inv, $"<path d=\"{route.Path}\" fill=\"none\" stroke=\"#607d8b\" stroke-width=\"1.5\"/>\n");
            var (many, one) = Cardinality(edge.RelationType);
            var toRight = route.X2 > route.X1;
            svg.Append(inv, $"<text x=\"{route.X1 + (toRight ? 6 : -6):0.#}\" y=\"{route.Y1 - 5:0.#}\" fill=\"#455a64\" text-anchor=\"{(toRight ? "start" : "end")}\">{many}</text>\n");
            svg.Append(inv, $"<text x=\"{route.X2 + (toRight ? -6 : 6):0.#}\" y=\"{route.Y2 - 5:0.#}\" fill=\"#455a64\" text-anchor=\"{(toRight ? "end" : "start")}\">{one}</text>\n");
        }

        foreach (var node in Nodes)
        {
            svg.Append(inv, $"<g transform=\"translate({node.X:0.#} {node.Y:0.#})\">\n");
            svg.Append(inv, $"<rect width=\"{node.Width:0.#}\" height=\"{node.Height:0.#}\" rx=\"4\" fill=\"#ffffff\" stroke=\"#90a4ae\"/>\n");
            svg.Append(inv, $"<rect width=\"{node.Width:0.#}\" height=\"{node.HeaderHeight}\" rx=\"4\" fill=\"{(node.TableType == "Table" ? "#1976d2" : "#7b1fa2")}\"/>\n");
            svg.Append(inv, $"<text x=\"10\" y=\"{HeaderHeight / 2 + 4}\" fill=\"#ffffff\" font-weight=\"bold\">{Xml(node.Name)}</text>\n");
            if (node.Caption != null)
                svg.Append(inv, $"<text x=\"10\" y=\"{HeaderHeight + CaptionHeight / 2 + 1}\" fill=\"#e3f2fd\" font-size=\"11\">{Xml(node.Caption)}</text>\n");
            for (var i = 0; i < node.Rows.Count; i++)
            {
                var row = node.Rows[i];
                var y = node.HeaderHeight + (i + 0.5) * RowHeight + 4;
                var mark = row.IsPrimaryKey ? "PK" : row.IsForeignKey ? "FK" : "";
                svg.Append(inv, $"<text x=\"10\" y=\"{y:0.#}\" fill=\"#d84315\" font-size=\"10\" font-weight=\"bold\">{mark}</text>\n");
                svg.Append(inv, $"<text x=\"34\" y=\"{y:0.#}\" fill=\"#263238\"{(row.IsPrimaryKey ? " font-weight=\"bold\"" : "")}>{Xml(row.Name)}</text>\n");
                svg.Append(inv, $"<text x=\"{node.Width - 8:0.#}\" y=\"{y:0.#}\" fill=\"#78909c\" text-anchor=\"end\">{Xml(row.DataType)}</text>\n");
            }

            svg.Append("</g>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static string Xml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>
    ///     Diagramme Mermaid (<c>erDiagram</c>) : à coller dans une page Markdown de GitHub/GitLab ou dans
    ///     mermaid.live. Les noms sont réduits aux caractères que Mermaid accepte.
    /// </summary>
    public string ToMermaid()
    {
        var names = UniqueNames();
        var text = new StringBuilder("erDiagram\n");
        foreach (var node in Nodes)
        {
            text.Append("    ").Append(names[node.Id]).Append(" {\n");
            var used = new HashSet<string>();
            foreach (var row in node.Rows)
            {
                var column = Identifier(row.Name);
                while (!used.Add(column)) column += "_";
                var keys = string.Join(", ", new[] { row.IsPrimaryKey ? "PK" : null, row.IsForeignKey ? "FK" : null }
                    .Where(k => k != null));
                text.Append("        ").Append(Identifier(row.DataType, "unknown")).Append(' ').Append(column)
                    .Append(keys.Length > 0 ? " " + keys : "").Append('\n');
            }

            text.Append("    }\n");
        }

        foreach (var edge in Edges)
        {
            if (!names.ContainsKey(edge.ReferencedTableId) || !names.ContainsKey(edge.ReferencingTableId)) continue;
            var link = edge.RelationType switch
            {
                "One-to-One" => "||--o|",
                "Many-to-Many" => "}o--o{",
                _ => "||--o{"
            };
            var label = Node(edge.ReferencingTableId)!.Rows.FirstOrDefault(r => r.ColumnId == edge.ReferencingColumnId)?.Name
                        ?? "references";
            text.Append("    ").Append(names[edge.ReferencedTableId]).Append(' ').Append(link).Append(' ')
                .Append(names[edge.ReferencingTableId]).Append(" : \"").Append(label.Replace("\"", "'")).Append("\"\n");
        }

        return text.ToString();
    }

    private Dictionary<string, string> UniqueNames()
    {
        var used = new HashSet<string>();
        var result = new Dictionary<string, string>();
        foreach (var node in Nodes)
        {
            var name = Identifier(node.Name, "table");
            while (!used.Add(name)) name += "_";
            result[node.Id] = name;
        }

        return result;
    }

    /// <summary>Réduit un nom à lettres, chiffres et soulignés (<c>sales.Orders</c> devient <c>sales_Orders</c>).</summary>
    private static string Identifier(string name, string fallback = "col")
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        if (cleaned.Length == 0) return fallback;
        return char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }
}
