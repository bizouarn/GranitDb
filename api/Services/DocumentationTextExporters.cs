using System.Text;
using GranitDB.API.Models.Descriptions;

namespace GranitDB.API.Services;

/// <summary>Exports texte de la documentation d'une base : Markdown (lisible, versionnable) et CSV (dictionnaire des colonnes, pour Excel).</summary>
public static class DocumentationTextExporters
{
    public static string ToMarkdown(HtmlDocumentationExporter.Snapshot s)
    {
        var columnsByTable = s.Columns.ToLookup(c => c.TableId);
        var indexesByTable = s.Indexes.ToLookup(i => i.TableId);
        var columnById = s.Columns.ToDictionary(c => c.Id);
        var tableById = s.Tables.ToDictionary(t => t.Id);
        var sb = new StringBuilder();

        sb.Append("# ").AppendLine(Line(s.Database.Name)).AppendLine();
        sb.Append("_").Append(s.Database.Type).Append(" — ").Append(s.Tables.Count).AppendLine(" tables_").AppendLine();
        if (!string.IsNullOrWhiteSpace(s.Database.Description)) sb.AppendLine(s.Database.Description.Trim()).AppendLine();

        if (s.Domains.Count > 0)
        {
            sb.AppendLine("## Domaines").AppendLine();
            foreach (var d in s.Domains.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var names = s.Links.Where(l => l.DomainId == d.Id).Select(l => tableById.GetValueOrDefault(l.TableId)?.Name)
                    .Where(n => n != null).Order(StringComparer.CurrentCultureIgnoreCase);
                sb.Append("- **").Append(Line(d.Name)).Append("** : ").AppendLine(string.Join(", ", names));
                if (!string.IsNullOrWhiteSpace(d.Description)) sb.Append("  ").AppendLine(Line(d.Description));
            }

            sb.AppendLine();
        }

        foreach (var t in s.Tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            sb.Append("## ").Append(Line(t.Name)).Append(t.TableType == "Table" ? "" : $" ({t.TableType})").AppendLine().AppendLine();
            if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine(t.Description.Trim()).AppendLine();

            sb.AppendLine("| # | Colonne | Type | Null | Clé | Défaut | Description |").AppendLine("|---|---|---|---|---|---|---|");
            var i = 0;
            foreach (var c in columnsByTable[t.Id].OrderBy(c => c.Position))
                sb.Append("| ").Append(++i).Append(" | ").Append(Cell(c.Name)).Append(" | ").Append(Cell(c.DataType)).Append(" | ")
                    .Append(c.IsNullable ? "oui" : "non").Append(" | ").Append(c.IsPrimaryKey ? "PK" : "").Append(" | ")
                    .Append(Cell(c.DefaultValue)).Append(" | ").Append(Cell((c.IsSensitive ? "[sensible] " : "") + c.Description)).AppendLine(" |");
            sb.AppendLine();

            // Une relation multi-colonnes : une seule ligne, avec la liste des colonnes de chaque côté.
            var refs = s.Relations
                .Where(r => columnById.ContainsKey(r.FirstId) && columnById.ContainsKey(r.SecondId))
                .Select(r => (Pairs: r.Pairs().Where(p => columnById.ContainsKey(p.First) && columnById.ContainsKey(p.Second)).ToList(),
                    Referenced: columnById[r.FirstId], Referencing: columnById[r.SecondId]))
                .Where(r => r.Referenced.TableId == t.Id || r.Referencing.TableId == t.Id).ToList();
            if (refs.Count > 0)
            {
                sb.AppendLine("Relations :").AppendLine();
                string Side(string table, IEnumerable<string> names)
                {
                    var list = names.Select(Line).ToList();
                    return Line(table) + "." + (list.Count == 1 ? list[0] : "(" + string.Join(", ", list) + ")");
                }

                foreach (var (pairs, referenced, referencing) in refs)
                    sb.Append("- ").Append(Side(tableById[referencing.TableId].Name, pairs.Select(p => columnById[p.Second].Name)))
                        .Append(" → ").AppendLine(Side(tableById[referenced.TableId].Name, pairs.Select(p => columnById[p.First].Name)));
                sb.AppendLine();
            }

            if (indexesByTable[t.Id].Any())
            {
                sb.AppendLine("Index :").AppendLine();
                foreach (var ix in indexesByTable[t.Id].OrderBy(x => x.Name))
                    sb.Append("- ").Append(Line(ix.Name)).Append(ix.IsUnique ? " (unique)" : "").Append(" : ").AppendLine(Line(ix.ColumnNames));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>Dictionnaire des colonnes : une ligne par colonne. UTF-8 avec BOM (Excel), séparateur « ; » (Excel français).</summary>
    public static string ToCsv(HtmlDocumentationExporter.Snapshot s)
    {
        var tableById = s.Tables.ToDictionary(t => t.Id);
        var sb = new StringBuilder("﻿");
        sb.AppendLine(string.Join(';', "Table", "Type de table", "Description de la table", "Colonne", "Type", "Null", "Clé primaire",
            "Sensible", "Défaut", "Description de la colonne"));
        foreach (var t in s.Tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
        foreach (var c in s.Columns.Where(c => c.TableId == t.Id).OrderBy(c => c.Position))
            sb.AppendLine(string.Join(';', Csv(t.Name), Csv(t.TableType), Csv(t.Description), Csv(c.Name), Csv(c.DataType),
                c.IsNullable ? "oui" : "non", c.IsPrimaryKey ? "oui" : "non", c.IsSensitive ? "oui" : "non", Csv(c.DefaultValue),
                Csv(c.Description)));
        return sb.ToString();
    }

    private static string Line(string? text) => (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    /// <summary>Cellule de tableau Markdown : une ligne, sans barre verticale ni balise active.</summary>
    private static string Cell(string? text) => Line(text).Replace("|", "\\|").Replace("<", "&lt;");

    /// <summary>
    ///     Champ CSV entre guillemets. Un texte commençant par = + - @ serait lu comme une formule par Excel : on le
    ///     fait précéder d'une apostrophe.
    /// </summary>
    private static string Csv(string? text)
    {
        var value = text ?? "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
