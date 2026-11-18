using System.IO.Compression;
using System.Security;
using System.Text;

namespace GranitDB.API.Services;

/// <summary>
///     Documentation d'une base en classeur Excel natif (.xlsx) : un onglet pour les tables, les colonnes, les relations et les
///     index. Écrit à la main (un .xlsx est un zip de XML) : aucune dépendance. Les textes sont des chaînes littérales, jamais
///     des formules : un contenu commençant par « = » reste du texte.
/// </summary>
public static class XlsxExporter
{
    private sealed record Sheet(string Name, string[] Header, List<string?[]> Rows, int[] Widths);

    public static byte[] ToXlsx(HtmlDocumentationExporter.Snapshot s)
    {
        var tableById = s.Tables.ToDictionary(t => t.Id);
        var columnById = s.Columns.ToDictionary(c => c.Id);
        var columnsByTable = s.Columns.ToLookup(c => c.TableId);
        var ordered = s.Tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var domainsOf = s.Links.GroupBy(l => l.TableId).ToDictionary(g => g.Key,
            g => string.Join(", ", g.Select(l => s.Domains.FirstOrDefault(d => d.Id == l.DomainId)?.Name).Where(n => n != null).Order()));

        var tables = new Sheet("Tables", ["Table", "Type", "Domaines", "Colonnes", "Description"],
            ordered.Select(t => new[] { t.Name, t.TableType, domainsOf.GetValueOrDefault(t.Id), columnsByTable[t.Id].Count().ToString(), t.Description }).ToList(),
            [34, 10, 28, 10, 70]);

        var columns = new Sheet("Colonnes", ["Table", "Colonne", "Type", "Null", "Clé primaire", "Sensible", "Défaut", "Description"],
            ordered.SelectMany(t => columnsByTable[t.Id].OrderBy(c => c.Position).Select(c => new[]
            {
                t.Name, c.Name, c.DataType, c.IsNullable ? "oui" : "non", c.IsPrimaryKey ? "oui" : "non", c.IsSensitive ? "oui" : "non",
                c.DefaultValue, c.Description
            })).ToList(), [34, 30, 18, 6, 12, 10, 18, 70]);

        var relations = new Sheet("Relations", ["Table", "Colonnes", "Table référencée", "Colonnes référencées", "Type", "Description"],
            s.Relations.Where(r => columnById.ContainsKey(r.FirstId) && columnById.ContainsKey(r.SecondId))
                .Select(r =>
                {
                    var pairs = r.Pairs().Where(p => columnById.ContainsKey(p.First) && columnById.ContainsKey(p.Second)).ToList();
                    return new[]
                    {
                        tableById[columnById[r.SecondId].TableId].Name, string.Join(", ", pairs.Select(p => columnById[p.Second].Name)),
                        tableById[columnById[r.FirstId].TableId].Name, string.Join(", ", pairs.Select(p => columnById[p.First].Name)),
                        r.RelationType, r.Description
                    };
                }).OrderBy(r => r[0], StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r[2]).ToList(), [34, 30, 34, 30, 14, 60]);

        var indexes = new Sheet("Index", ["Table", "Index", "Unique", "Colonnes"],
            ordered.SelectMany(t => s.Indexes.Where(i => i.TableId == t.Id).OrderBy(i => i.Name)
                .Select(i => new[] { t.Name, i.Name, i.IsUnique ? "oui" : "non", i.ColumnNames })).ToList(), [34, 34, 8, 50]);

        return Build([tables, columns, relations, indexes]);
    }

    private static byte[] Build(Sheet[] sheets)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))
                + "</Types>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + string.Concat(sheets.Select((sh, i) => $"<sheet name=\"{Esc(sh.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))
                + "</sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + $"<Relationship Id=\"rId{sheets.Length + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            // Style 0 : normal, style 1 : en-tête en gras sur fond bleu pâle, style 2 : texte qui passe à la ligne.
            Add(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E2F3\"/></patternFill></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs>"
                + "</styleSheet>");
            for (var i = 0; i < sheets.Length; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
        }

        return stream.ToArray();
    }

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols>");
        for (var i = 0; i < sheet.Widths.Length; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sheet.Widths[i]}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        Row(sb, 1, sheet.Header, 1);
        for (var r = 0; r < sheet.Rows.Count; r++) Row(sb, r + 2, sheet.Rows[r], 2);
        sb.Append("</sheetData>");
        sb.Append($"<autoFilter ref=\"A1:{Column(sheet.Header.Length - 1)}{Math.Max(1, sheet.Rows.Count + 1)}\"/></worksheet>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, int number, IReadOnlyList<string?> cells, int style)
    {
        sb.Append($"<row r=\"{number}\">");
        for (var i = 0; i < cells.Count; i++)
        {
            var value = cells[i];
            if (string.IsNullOrEmpty(value)) continue;
            sb.Append($"<c r=\"{Column(i)}{number}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(value)}</t></is></c>");
        }

        sb.Append("</row>");
    }

    private static string Column(int index) => ((char)('A' + index)).ToString(); // moins de 26 colonnes par onglet

    /// <summary>Échappe le XML et retire les caractères de contrôle interdits (Excel refuserait le fichier).</summary>
    private static string Esc(string text) =>
        SecurityElement.Escape(new string(text.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray()))!;

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
