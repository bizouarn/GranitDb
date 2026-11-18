using System.Text;
using System.Text.Json;
using GranitDB.API.Data;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;

namespace GranitDB.API.Services;

/// <summary>
///     Export HTML autonome de la documentation d'une base : un seul fichier (données, styles et script embarqués),
///     lisible sans serveur, présenté comme une documentation Dataedo (menu bleu à gauche, arbre domaines / tables,
///     fil d'Ariane, tableau récapitulatif, colonnes, relations, index).
/// </summary>
public static class HtmlDocumentationExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Tout ce que contiennent les exports d'une base, lu une fois.</summary>
    public sealed record Snapshot(DatabaseInfo Database, IReadOnlyList<TableInfo> Tables, IReadOnlyList<ColumnInfo> Columns,
        IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<RelationInfo> Relations, IReadOnlyList<DomainInfo> Domains,
        IReadOnlyList<DomainTableInfo> Links, IReadOnlyList<DomainHiddenRelationInfo> Hidden);

    public static async Task<Snapshot?> LoadAsync(ICatalogRepository repository, string databaseId, CancellationToken ct)
    {
        var database = await repository.GetAsync<DatabaseInfo>(databaseId, ct);
        if (database == null) return null;

        var byDatabase = new[] { new CatalogFilter("DatabaseId", databaseId) };
        var domains = await repository.GetAllAsync<DomainInfo>(byDatabase, ct);
        var links = new List<DomainTableInfo>();
        var hidden = new List<DomainHiddenRelationInfo>();
        foreach (var domain in domains)
        {
            links.AddRange(await repository.FindAsync<DomainTableInfo>(nameof(DomainTableInfo.DomainId), domain.Id, ct));
            hidden.AddRange(await repository.FindAsync<DomainHiddenRelationInfo>(nameof(DomainHiddenRelationInfo.DomainId), domain.Id, ct));
        }

        return new Snapshot(database, await repository.GetAllAsync<TableInfo>(byDatabase, ct),
            await repository.GetAllAsync<ColumnInfo>(byDatabase, ct), await repository.GetAllAsync<IndexInfo>(byDatabase, ct),
            await repository.GetRelationsOfDatabaseAsync(databaseId, ct), domains, links, hidden);
    }

    public static async Task<string?> ExportAsync(ICatalogRepository repository, string databaseId, CancellationToken ct)
    {
        var snapshot = await LoadAsync(repository, databaseId, ct);
        return snapshot == null ? null : Render(snapshot);
    }

    public static string Render(Snapshot snapshot)
    {
        var (database, tables, columns, indexes, relations, domains, links, hiddenMarks) =
            (snapshot.Database, snapshot.Tables, snapshot.Columns, snapshot.Indexes, snapshot.Relations, snapshot.Domains,
                snapshot.Links, snapshot.Hidden);

        var columnsById = columns.ToDictionary(c => c.Id);
        var tablesById = tables.ToDictionary(t => t.Id);
        var data = new
        {
            database = new { name = database.Name, type = database.Type, description = database.Description, exportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC" },
            domains = domains.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => new
            {
                id = d.Id,
                name = d.Name,
                color = d.Color,
                description = d.Description,
                tables = links.Where(l => l.DomainId == d.Id && tablesById.ContainsKey(l.TableId))
                    .Select(l => l.TableId).ToArray(),
                // Place des tables dans le plan du domaine, telle qu'enregistrée dans l'application.
                layout = links.Where(l => l.DomainId == d.Id && l.X != null && l.Y != null)
                    .ToDictionary(l => l.TableId, l => new[] { l.X!.Value, l.Y!.Value }),
                hidden = hiddenMarks.Where(h => h.DomainId == d.Id).Select(h => h.RelationId).ToArray()
            }),
            tables = tables.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => new
            {
                id = t.Id,
                name = t.Name,
                type = t.TableType,
                description = t.Description,
                columns = columns.Where(c => c.TableId == t.Id).OrderBy(c => c.Position).Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    type = c.DataType,
                    pk = c.IsPrimaryKey,
                    nullable = c.IsNullable,
                    @default = c.DefaultValue,
                    sensitive = c.IsSensitive,
                    description = c.Description
                }),
                indexes = indexes.Where(i => i.TableId == t.Id).OrderBy(i => i.Name).Select(i => new
                {
                    name = i.Name,
                    unique = i.IsUnique,
                    columns = i.ColumnNames
                })
            }),
            // Une relation : la colonne référencée (FirstId) et la colonne qui référence (SecondId).
            relations = relations.Where(r => columnsById.ContainsKey(r.FirstId) && columnsById.ContainsKey(r.SecondId))
                .Select(r => new
                {
                    id = r.Id,
                    referencedTable = columnsById[r.FirstId].TableId,
                    referencedColumn = columnsById[r.FirstId].Name,
                    referencingTable = columnsById[r.SecondId].TableId,
                    referencingColumn = columnsById[r.SecondId].Name,
                    // Relation multi-colonnes : les paires suivantes (la première est ci-dessus), dans l'ordre de la clé.
                    extra = r.ExtraColumns.Where(p => columnsById.ContainsKey(p.FirstId) && columnsById.ContainsKey(p.SecondId))
                        .Select(p => new { referencedColumn = columnsById[p.FirstId].Name, referencingColumn = columnsById[p.SecondId].Name }),
                    auto = r.MetaInfo == RelationInfo.AutoMarker,
                    description = r.Description
                })
        };

        // Dans un <script>, la séquence "</" fermerait la balise : on l'échappe.
        var json = JsonSerializer.Serialize(data, JsonOptions).Replace("</", "<\\/").Replace("<!--", "<\\!--");
        var title = System.Net.WebUtility.HtmlEncode(database.Name);
        return new StringBuilder(Template)
            .Replace("{{TITLE}}", title)
            .Replace("{{DATA}}", json)
            .ToString();
    }

    private const string Template = """
<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{TITLE}} — documentation</title>
<style>
:root{--primary200:#DFEBF1;--primary700:#4C799E;--primary800:#3A6388;--primary900:#30526f;--line:#dde3e8;--muted:#667;}
*{box-sizing:border-box}
html,body{margin:0;height:100%}
body{font-family:-apple-system,"Segoe UI","Open Sans",Arial,sans-serif;font-size:14px;color:#222;background:#fff}
a{color:var(--primary900);text-decoration:none}a:hover{color:var(--primary700);text-decoration:underline}
h1{font-size:22px;font-weight:700;margin:0 0 14px}
h2{font-size:15px;font-weight:700;margin:26px 0 8px;padding-bottom:4px;border-bottom:1px solid var(--line)}
.layout{display:flex;height:100vh}
.side{width:300px;min-width:220px;background:var(--primary900);color:#fff;display:flex;flex-direction:column}
.side-head{background:var(--primary800);padding:14px 16px;margin-bottom:10px}
.side-head a{color:#fff;font-weight:700;font-size:16px}
.side-head small{display:block;color:var(--primary200);margin-top:2px;font-size:12px}
.search{margin:0 12px 10px}
.search input{width:100%;padding:7px 9px;border:0;border-radius:3px;font:inherit}
.tree{flex:1;overflow:auto;padding:0 6px 16px}
.tree a{color:#fff;display:block;padding:3px 8px;border-radius:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.tree a:hover{color:var(--primary200);background:rgba(255,255,255,.08);text-decoration:none}
.tree a.active{background:rgba(255,255,255,.18);font-weight:600}
.tree .group{display:flex;align-items:center;padding:4px 4px;cursor:pointer;font-weight:600;user-select:none}
.tree .group:before{content:"\25B8";display:inline-block;width:14px;transition:transform .1s}
.tree .group.open:before{transform:rotate(90deg)}
.tree .group .n{margin-left:auto;color:var(--primary200);font-weight:400;font-size:12px}
.tree .children{display:none;margin-left:14px}.tree .children.open{display:block}
.dot{display:inline-block;width:9px;height:9px;border-radius:2px;margin-right:7px;background:#9db4c6}
.content{flex:1;overflow:auto;padding:18px 32px 60px}
.crumb{font-size:13px;color:var(--muted);margin-bottom:10px}.crumb span{margin:0 5px}
.summary{border-collapse:collapse;margin-bottom:6px}
.summary th{text-align:left;font-weight:600;color:var(--muted);padding:3px 24px 3px 0;vertical-align:top;font-size:13px}
.summary td{padding:3px 0;font-size:13px}
.desc{white-space:pre-wrap}
.empty{color:var(--muted);font-style:italic}
table.grid{border-collapse:collapse;width:100%;font-size:13px}
table.grid th{background:#f2f5f7;text-align:left;padding:6px 10px;border:1px solid var(--line);font-weight:600;white-space:nowrap}
table.grid td{padding:6px 10px;border:1px solid var(--line);vertical-align:top}
table.grid tr:hover td{background:#f8fafb}
.badge{display:inline-block;font-size:11px;padding:0 6px;border-radius:9px;background:var(--primary200);color:var(--primary900);margin-left:4px}
.badge.warn{background:#fde8d8;color:#8a4b12}
.pk{font-weight:700}
.meta{margin-top:30px;color:var(--muted);font-size:12px;border-top:1px solid var(--line);padding-top:8px}
.erd-wrap{border:1px solid var(--line);border-radius:4px;overflow:auto;max-height:78vh;background:#fcfdfe}
.erd-wrap svg{display:block}
.erd text{font-size:12px;fill:#222}
.erd .hd{fill:var(--primary800)}.erd .hdt{fill:#fff;font-weight:700}
.erd .box{fill:#fff;stroke:#8fa6b8}.erd .pkc{font-weight:700}
.erd .edge{fill:none;stroke:#757575;stroke-width:1.2}
.erd .card{font-size:10px;fill:#555}
.erd a:hover .box{stroke:var(--primary700);stroke-width:2}
.erd .center .box{stroke:var(--primary800);stroke-width:2}
.menu-btn{display:none}
@media(max-width:800px){.layout{flex-direction:column;height:auto}.side{width:100%;max-height:50vh}.content{padding:14px}}
@media print{.side{display:none}.content{overflow:visible}}
</style>
</head>
<body>
<div class="layout">
  <nav class="side">
    <div class="side-head"><a href="#/" id="dbname"></a><small id="dbtype"></small></div>
    <div class="search"><input id="q" type="search" placeholder="Rechercher une table ou une colonne…" aria-label="Rechercher"></div>
    <div class="tree" id="tree"></div>
  </nav>
  <main class="content" id="main"></main>
</div>
<script>
var DATA = {{DATA}};
</script>
<script>
(function(){
var D = DATA, byId = {}, rels = D.relations;
// Toutes les paires d'une relation (référencée, qui référence) : la première, puis celles d'une clé multi-colonnes.
function pairsOf(r){
  return [[r.referencedColumn, r.referencingColumn]].concat((r.extra || []).map(function(p){ return [p.referencedColumn, p.referencingColumn]; }));
}
function joinCols(r, i){ return pairsOf(r).map(function(p){ return p[i]; }).join(', '); }
D.tables.forEach(function(t){ byId[t.id] = t; });
var domainsOf = {};
D.domains.forEach(function(d){ d.tables.forEach(function(id){ (domainsOf[id] = domainsOf[id] || []).push(d); }); });

function el(tag, attrs, kids){
  var e = document.createElement(tag);
  for (var k in (attrs || {})) {
    if (k === 'class') e.className = attrs[k];
    else if (k === 'text') e.textContent = attrs[k];
    else e.setAttribute(k, attrs[k]);
  }
  (kids || []).forEach(function(c){ if (c != null) e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
  return e;
}
function link(href, text){ return el('a', {href: href, text: text}); }
function tableLink(id){ var t = byId[id]; return t ? link('#/table/' + id, t.name) : el('span', {text: '?'}); }
function desc(text){ return text && text.trim() ? el('div', {class: 'desc', text: text}) : el('span', {class: 'empty', text: 'Pas de description'}); }
function crumb(parts){
  var c = el('div', {class: 'crumb'});
  parts.forEach(function(p, i){ if (i) c.appendChild(el('span', {text: '›'})); c.appendChild(typeof p === 'string' ? document.createTextNode(p) : p); });
  return c;
}
function grid(headers, rows){
  var t = el('table', {class: 'grid'});
  t.appendChild(el('thead', {}, [el('tr', {}, headers.map(function(h){ return el('th', {text: h}); }))]));
  t.appendChild(el('tbody', {}, rows.map(function(r){ return el('tr', {}, r.map(function(c){ return el('td', {}, [c]); })); })));
  return t;
}
function summary(rows){
  return el('table', {class: 'summary'}, rows.map(function(r){ return el('tr', {}, [el('th', {text: r[0]}), el('td', {}, [r[1]])]); }));
}
function tableList(ids){
  var items = ids.map(function(id){ return byId[id]; }).filter(Boolean)
    .sort(function(a, b){ return a.name.localeCompare(b.name); });
  if (!items.length) return el('p', {class: 'empty', text: 'Aucune table.'});
  return grid(['Table', 'Type', 'Colonnes', 'Description'], items.map(function(t){
    return [tableLink(t.id), document.createTextNode(t.type), document.createTextNode(String(t.columns.length)), desc(t.description)];
  }));
}

function home(){
  var m = el('div');
  m.appendChild(crumb([D.database.name]));
  m.appendChild(el('h1', {text: D.database.name}));
  m.appendChild(summary([
    ['Type', document.createTextNode(D.database.type)],
    ['Tables', document.createTextNode(String(D.tables.filter(function(t){ return t.type !== 'View'; }).length))],
    ['Vues', document.createTextNode(String(D.tables.filter(function(t){ return t.type === 'View'; }).length))],
    ['Domaines', document.createTextNode(String(D.domains.length))],
    ['Description', desc(D.database.description)]
  ]));
  if (D.domains.length) {
    m.appendChild(el('h2', {text: 'Domaines'}));
    m.appendChild(grid(['Domaine', 'Tables', 'Description'], D.domains.map(function(d){
      return [link('#/domain/' + d.id, d.name), document.createTextNode(String(d.tables.length)), desc(d.description)];
    })));
  }
  m.appendChild(el('h2', {text: 'Tables'}));
  m.appendChild(tableList(D.tables.map(function(t){ return t.id; })));
  m.appendChild(el('div', {class: 'meta', text: 'Exporté le ' + D.database.exportedAt + ' par GranitDb'}));
  return m;
}

function domainPage(id){
  var d = D.domains.filter(function(x){ return x.id === id; })[0];
  if (!d) return notFound();
  var m = el('div');
  m.appendChild(crumb([link('#/', D.database.name), 'Domaines', d.name]));
  m.appendChild(el('h1', {text: d.name}));
  m.appendChild(summary([['Tables', document.createTextNode(String(d.tables.length))], ['Description', desc(d.description)]]));
  if (d.tables.length > 1) {
    m.appendChild(el('h2', {text: 'Diagramme (ERD)'}));
    m.appendChild(domainErd(d));
  }
  m.appendChild(el('h2', {text: 'Tables du domaine'}));
  m.appendChild(tableList(d.tables));
  return m;
}

function tablePage(id){
  var t = byId[id];
  if (!t) return notFound();
  var m = el('div');
  var ds = domainsOf[id] || [];
  m.appendChild(crumb([link('#/', D.database.name)].concat(ds.length ? [link('#/domain/' + ds[0].id, ds[0].name)] : []).concat([t.name])));
  m.appendChild(el('h1', {text: t.name}));
  m.appendChild(summary([
    ['Type', document.createTextNode(t.type)],
    ['Domaines', ds.length ? el('span', {}, ds.map(function(d, i){ return el('span', {}, [i ? ', ' : '', link('#/domain/' + d.id, d.name)]); })) : el('span', {class: 'empty', text: '—'})],
    ['Description', desc(t.description)]
  ]));

  var around = neighboursErd(t);
  if (around) { m.appendChild(el('h2', {text: 'Diagramme (ERD)'})); m.appendChild(around); }

  m.appendChild(el('h2', {text: 'Colonnes (' + t.columns.length + ')'}));
  m.appendChild(grid(['#', 'Nom', 'Type', 'Null', 'Défaut', 'Description'], t.columns.map(function(c, i){
    var name = el('span', {class: c.pk ? 'pk' : '', text: c.name});
    if (c.pk) name.appendChild(el('span', {class: 'badge', text: 'PK'}));
    if (c.sensitive) name.appendChild(el('span', {class: 'badge warn', text: 'sensible'}));
    return [document.createTextNode(String(i + 1)), name, document.createTextNode(c.type), document.createTextNode(c.nullable ? 'oui' : 'non'),
            document.createTextNode(c['default'] == null ? '' : c['default']), desc(c.description)];
  })));

  var out = rels.filter(function(r){ return r.referencingTable === id; });
  var inc = rels.filter(function(r){ return r.referencedTable === id; });
  if (out.length) {
    m.appendChild(el('h2', {text: 'Références vers (' + out.length + ')'}));
    m.appendChild(grid(['Colonne', 'Table référencée', 'Colonne référencée', 'Origine'], out.map(function(r){
      return [document.createTextNode(joinCols(r, 1)), tableLink(r.referencedTable), document.createTextNode(joinCols(r, 0)), document.createTextNode(r.auto ? 'clé étrangère' : 'manuelle')];
    })));
  }
  if (inc.length) {
    m.appendChild(el('h2', {text: 'Référencée par (' + inc.length + ')'}));
    m.appendChild(grid(['Colonne', 'Table', 'Colonne qui référence', 'Origine'], inc.map(function(r){
      return [document.createTextNode(joinCols(r, 0)), tableLink(r.referencingTable), document.createTextNode(joinCols(r, 1)), document.createTextNode(r.auto ? 'clé étrangère' : 'manuelle')];
    })));
  }
  if (t.indexes.length) {
    m.appendChild(el('h2', {text: 'Index (' + t.indexes.length + ')'}));
    m.appendChild(grid(['Nom', 'Unique', 'Colonnes'], t.indexes.map(function(i){
      return [document.createTextNode(i.name), document.createTextNode(i.unique ? 'oui' : 'non'), document.createTextNode(i.columns)];
    })));
  }
  return m;
}

function notFound(){ return el('div', {}, [el('h1', {text: 'Introuvable'}), link('#/', 'Retour à l\'accueil')]); }

// --- Diagrammes (ERD) ---
var NS = 'http://www.w3.org/2000/svg', ROW = 18, HEAD = 26;
function sv(tag, attrs, text){
  var e = document.createElementNS(NS, tag);
  for (var k in (attrs || {})) e.setAttribute(k, attrs[k]);
  if (text != null) e.textContent = text;
  return e;
}
// Mesure les tables d'un diagramme : colonnes montrées (celles des relations tracées, plus la clé de la table centrale), largeur, hauteur.
function measure(nodes, edges, center){
  nodes.forEach(function(n){
    var t = byId[n.id], used = {};
    edges.forEach(function(e){
      pairsOf(e).forEach(function(p){
        if (e.referencedTable === n.id) used[p[0]] = 1;
        if (e.referencingTable === n.id) used[p[1]] = 1;
      });
    });
    n.rows = t.columns.filter(function(c){ return used[c.name] || (c.pk && n.id === center); });
    var longest = Math.max(t.name.length, Math.max.apply(null, [0].concat(n.rows.map(function(c){ return c.name.length + (c.type || '').length + 3; }))));
    n.w = Math.max(150, Math.ceil(longest * 7) + 24);
    n.h = HEAD + Math.max(1, n.rows.length) * ROW + 6;
  });
}
// nodes : [{id, x, y}] déjà mesurés ; les tables sans place passent en grille sous les autres.
function drawErd(nodes, edges, color, center){
  var by = {}; nodes.forEach(function(n){ by[n.id] = n; });
  var placed = nodes.filter(function(n){ return n.x != null && n.y != null; });
  var bottom = placed.length ? Math.max.apply(null, placed.map(function(n){ return n.y + n.h; })) + 40 : 0;
  var loose = nodes.filter(function(n){ return n.x == null || n.y == null; });
  var cols = Math.max(1, Math.ceil(Math.sqrt(loose.length))), x = 0, y = bottom, rowH = 0;
  loose.forEach(function(n, i){
    if (i && i % cols === 0) { x = 0; y += rowH + 40; rowH = 0; }
    n.x = x; n.y = y; x += n.w + 60; rowH = Math.max(rowH, n.h);
  });
  var minX = Math.min.apply(null, nodes.map(function(n){ return n.x; })) - 30, minY = Math.min.apply(null, nodes.map(function(n){ return n.y; })) - 30;
  var maxX = Math.max.apply(null, nodes.map(function(n){ return n.x + n.w; })) + 30, maxY = Math.max.apply(null, nodes.map(function(n){ return n.y + n.h; })) + 30;
  var svg = sv('svg', {class: 'erd', viewBox: minX + ' ' + minY + ' ' + (maxX - minX) + ' ' + (maxY - minY), width: maxX - minX, height: maxY - minY});
  function rowY(n, name){
    for (var i = 0; i < n.rows.length; i++) if (n.rows[i].name === name) return n.y + HEAD + i * ROW + ROW / 2;
    return n.y + HEAD / 2;
  }
  var seen = {};
  edges.forEach(function(e){
    var a = by[e.referencingTable], b = by[e.referencedTable];
    if (!a || !b || a === b) return;
    var key = e.id || (e.referencingTable + '>' + e.referencingColumn + '>' + e.referencedTable + '>' + e.referencedColumn);
    if (seen[key]) return;
    seen[key] = 1;
    // Une seule ligne par relation : une relation multi-colonnes part de toutes ses colonnes (un trait court par
    // colonne, réunis par une barre) et arrive de même.
    var ps = pairsOf(e), yas = ps.map(function(p){ return rowY(a, p[1]); }), ybs = ps.map(function(p){ return rowY(b, p[0]); });
    var multi = ps.length > 1, sa, sb, ex = multi ? 10 : 0;
    function avg(v){ return v.reduce(function(s, x){ return s + x; }, 0) / v.length; }
    var ya = avg(yas), yb = avg(ybs), x1, x2, mid, d, la, lb;
    if (a.x + a.w + 20 <= b.x) { sa = 1; sb = -1; x1 = a.x + a.w; x2 = b.x; }
    else if (b.x + b.w + 20 <= a.x) { sa = -1; sb = 1; x1 = a.x; x2 = b.x + b.w; }
    else { sa = 1; sb = 1; x1 = a.x + a.w; x2 = b.x + b.w; }
    var bx1 = x1 + sa * ex, bx2 = x2 + sb * ex;
    mid = sa === sb ? Math.max(bx1, bx2) + 24 : (bx1 + bx2) / 2;
    d = 'M' + bx1 + ' ' + ya + 'H' + mid + 'V' + yb + 'H' + bx2;
    la = sa > 0 ? bx1 + 4 : bx1 - 9;
    lb = sb > 0 ? bx2 + 4 : bx2 - 9;
    if (multi) {
      yas.forEach(function(y){ d += 'M' + x1 + ' ' + y + 'H' + bx1; });
      ybs.forEach(function(y){ d += 'M' + x2 + ' ' + y + 'H' + bx2; });
      d += 'M' + bx1 + ' ' + Math.min.apply(null, yas) + 'V' + Math.max.apply(null, yas);
      d += 'M' + bx2 + ' ' + Math.min.apply(null, ybs) + 'V' + Math.max.apply(null, ybs);
    }
    svg.appendChild(sv('path', {'class': 'edge', d: d}));
    svg.appendChild(sv('text', {'class': 'card', x: la, y: ya - 3}, 'N'));
    svg.appendChild(sv('text', {'class': 'card', x: lb, y: yb - 3}, '1'));
  });
  nodes.forEach(function(n){
    var t = byId[n.id], g = sv('g', {'class': n.id === center ? 'center' : ''});
    var a = sv('a', {href: '#/table/' + n.id});
    a.appendChild(sv('title', {}, t.name));
    a.appendChild(sv('rect', {'class': 'box', x: n.x, y: n.y, width: n.w, height: n.h, rx: 3}));
    a.appendChild(sv('rect', {'class': 'hd', x: n.x, y: n.y, width: n.w, height: HEAD, rx: 3, style: color ? 'fill:' + color : ''}));
    a.appendChild(sv('text', {'class': 'hdt', x: n.x + 8, y: n.y + 17}, t.name));
    n.rows.forEach(function(c, i){
      a.appendChild(sv('text', {'class': c.pk ? 'pkc' : '', x: n.x + 8, y: n.y + HEAD + i * ROW + 13}, c.name + (c.type ? ' : ' + c.type : '')));
    });
    g.appendChild(a);
    svg.appendChild(g);
  });
  var wrap = el('div', {'class': 'erd-wrap'});
  wrap.appendChild(svg);
  return wrap;
}
function domainErd(d){
  var members = {}, hidden = {};
  d.tables.forEach(function(id){ if (byId[id]) members[id] = 1; });
  (d.hidden || []).forEach(function(id){ hidden[id] = 1; });
  var edges = rels.filter(function(r){ return members[r.referencedTable] && members[r.referencingTable] && !hidden[r.id]; });
  var nodes = Object.keys(members).map(function(id){ var p = d.layout && d.layout[id]; return {id: id, x: p ? p[0] : null, y: p ? p[1] : null}; });
  measure(nodes, edges, null);
  return drawErd(nodes, edges, d.color || null, null);
}
// La table, ses parents (qu'elle référence) à gauche et ses enfants (qui la référencent) à droite.
function neighboursErd(t){
  var parents = {}, children = {}, edges = [];
  rels.forEach(function(r){
    if (r.referencingTable === t.id && r.referencedTable !== t.id) { parents[r.referencedTable] = 1; edges.push(r); }
    else if (r.referencedTable === t.id && r.referencingTable !== t.id) { children[r.referencingTable] = 1; edges.push(r); }
  });
  var pIds = Object.keys(parents).slice(0, 10), cIds = Object.keys(children).filter(function(id){ return !parents[id]; }).slice(0, 10);
  if (!pIds.length && !cIds.length) return null;
  var all = [t.id].concat(pIds, cIds), keep = {};
  all.forEach(function(id){ keep[id] = 1; });
  edges = edges.filter(function(r){ return keep[r.referencedTable] && keep[r.referencingTable]; });
  var nodes = all.map(function(id){ return {id: id}; }), n = {};
  measure(nodes, edges, t.id);
  nodes.forEach(function(x){ n[x.id] = x; });
  function stack(ids, x){ var y = 0; ids.forEach(function(id){ n[id].x = x; n[id].y = y; y += n[id].h + 24; }); return y; }
  var leftW = Math.max.apply(null, [0].concat(pIds.map(function(id){ return n[id].w; })));
  var cx = pIds.length ? leftW + 90 : 0, rx = cx + n[t.id].w + 90;
  var hp = stack(pIds, 0), hc = stack(cIds, rx);
  n[t.id].x = cx;
  n[t.id].y = Math.max(0, Math.max(hp, hc, n[t.id].h) / 2 - n[t.id].h / 2);
  return drawErd(nodes, edges, null, t.id);
}

// --- Arbre ---
var tree = document.getElementById('tree');
document.getElementById('dbname').textContent = D.database.name;
document.getElementById('dbtype').textContent = D.database.type + ' · ' + D.tables.length + ' tables';
function group(label, count, items, openByDefault, color){
  var wrap = el('div');
  var head = el('div', {class: 'group' + (openByDefault ? ' open' : '')}, [el('span', {class: 'dot', style: color ? 'background:' + color : ''}), label, el('span', {class: 'n', text: String(count)})]);
  var kids = el('div', {class: 'children' + (openByDefault ? ' open' : '')});
  items.forEach(function(i){ kids.appendChild(i); });
  head.addEventListener('click', function(){ head.classList.toggle('open'); kids.classList.toggle('open'); });
  wrap.appendChild(head); wrap.appendChild(kids);
  return wrap;
}
function leaf(t){ var a = link('#/table/' + t.id, t.name); a.setAttribute('data-q', t.name.toLowerCase()); return a; }
function buildTree(){
  tree.textContent = '';
  var big = D.tables.length > 300;
  D.domains.forEach(function(d){
    tree.appendChild(group(link('#/domain/' + d.id, d.name), d.tables.length, d.tables.map(function(id){ return byId[id]; }).filter(Boolean).sort(function(a, b){ return a.name.localeCompare(b.name); }).map(leaf), false, d.color));
  });
  var tabs = D.tables.filter(function(t){ return t.type !== 'View'; }), views = D.tables.filter(function(t){ return t.type === 'View'; });
  tree.appendChild(group('Tables', tabs.length, tabs.map(leaf), !big && !D.domains.length));
  if (views.length) tree.appendChild(group('Vues', views.length, views.map(leaf), false));
}
buildTree();

var q = document.getElementById('q');
q.addEventListener('input', function(){
  var term = q.value.trim().toLowerCase();
  if (!term) { buildTree(); mark(); return; }
  tree.textContent = '';
  var hits = D.tables.filter(function(t){
    return t.name.toLowerCase().indexOf(term) >= 0 || t.columns.some(function(c){ return c.name.toLowerCase().indexOf(term) >= 0; });
  }).slice(0, 200);
  tree.appendChild(group('Résultats', hits.length, hits.map(leaf), true));
  mark();
});
function mark(){
  var cur = location.hash;
  Array.prototype.forEach.call(tree.querySelectorAll('a'), function(a){ a.classList.toggle('active', a.getAttribute('href') === cur); });
}

function route(){
  var h = location.hash.replace(/^#\/?/, ''), parts = h.split('/'), main = document.getElementById('main'), page;
  if (parts[0] === 'table') page = tablePage(parts[1]);
  else if (parts[0] === 'domain') page = domainPage(parts[1]);
  else page = home();
  main.textContent = ''; main.appendChild(page); main.scrollTop = 0;
  var t = parts[0] === 'table' ? byId[parts[1]] : null;
  document.title = (t ? t.name + ' — ' : '') + D.database.name;
  mark();
  var active = tree.querySelector('a.active');
  if (active) { for (var p = active.parentNode; p && p !== tree; p = p.parentNode) { if (p.classList && p.classList.contains('children')) { p.classList.add('open'); if (p.previousSibling) p.previousSibling.classList.add('open'); } } active.scrollIntoView({block: 'nearest'}); }
}
window.addEventListener('hashchange', route);
route();
})();
</script>
</body>
</html>
""";
}
