// Convertit un export HTML Dataedo (dossier data/) en JSON pour POST /import/documentation/{databaseId}.
// Usage : node convert-dataedo.mjs <dossier-export> <fichier-sortie.json>
import fs from 'fs';
import path from 'path';

const [src = '../herakles', out = '../herakles/herakles-doc.json', xs = '1.45', ys = '1.6'] = process.argv.slice(2);
// Les tables du catalogue sont un peu plus larges/hautes que celles de Dataedo : on écarte légèrement le plan.
const SX = +xs, SY = +ys;
const dir = path.join(src, 'data') + path.sep;
const load = f => {
  const s = fs.readFileSync(dir + f, 'utf8');
  return JSON.parse(s.slice(s.indexOf('=') + 1).replace(/;\s*$/, ''));
};

const ENT = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ', eacute: 'é', egrave: 'è', ecirc: 'ê', euml: 'ë',
  agrave: 'à', acirc: 'â', ccedil: 'ç', icirc: 'î', iuml: 'ï', ocirc: 'ô', ugrave: 'ù', ucirc: 'û', uuml: 'ü',
  Eacute: 'É', Egrave: 'È', Agrave: 'À', Ccedil: 'Ç', laquo: '«', raquo: '»', deg: '°', euro: '€', hellip: '…',
  rsquo: '’', lsquo: '‘', ndash: '–', mdash: '—', oelig: 'œ' };
const text = html => {
  if (!html) return null;
  let s = html.replace(/<head[\s\S]*?<\/head>/i, '').replace(/<style[\s\S]*?<\/style>/gi, '')
    .replace(/<br\s*\/?>/gi, '\n').replace(/<\/(p|div|li|tr)>/gi, '\n').replace(/<[^>]+>/g, '')
    .replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(+n)).replace(/&#x([0-9a-f]+);/gi, (_, n) => String.fromCodePoint(parseInt(n, 16)))
    .replace(/&([a-zA-Z]+);/g, (m, n) => ENT[n] ?? m)
    .replace(/[ \t\r\f\v ]+/g, ' ').replace(/ ?\n ?/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
  return s ? s.slice(0, 4000) : null;
};

const tables = [], relations = [], byId = new Map();
for (const t of load('d4t.json.js').tables) {
  const o = load(t.id + '.json.js');
  // Dataedo nomme parfois « table (libellé) » : le vrai nom est dans le résumé, le libellé sert de description de secours.
  const real = o.summary?.find(x => x.field === 'Name')?.value ?? o.name;
  const caption = real !== o.name && o.name.startsWith(real) ? o.name.slice(real.length).replace(/^s*(|)s*$/g, '') : null;
  byId.set(t.id, real);
  tables.push({
    name: real,
    description: text(o.description) ?? caption,
    columns: (o.columns || []).map(c => ({ name: c.name, description: text(c.description) })).filter(c => c.description)
  });
  for (const r of o.relations || [])
    for (const k of (r.constraints || []).filter(k => k.foreign_column && k.primary_column))
      relations.push({
        table: r.foreign_table, column: k.foreign_column, refTable: r.primary_table, refColumn: k.primary_column,
        relationType: r.pk_cardinality === 'mx' && r.fk_cardinality === 'mx' ? 'Many-to-Many' : 'One-to-Many',
        description: text(r.title) || text(r.description)
      });
}

// Le plan Dataedo est un SVG : chaque table est <g data-table-id="123" ... transform="translate(x,y)">, le tout décalé
// par un <g transform="translate(dx,dy)"> global.
function placements(svg) {
  if (!svg) return [];
  const shift = /<\/defs><g transform="translate\((-?[\d.]+),\s*(-?[\d.]+)\)"/.exec(svg);
  const [dx, dy] = shift ? [+shift[1], +shift[2]] : [0, 0];
  const out = [];
  for (const m of svg.matchAll(/<g data-table="" data-table-id="(\d+)"[^>]*?class="node" transform="translate\((-?[\d.]+),\s*(-?[\d.]+)\)"/g)) {
    const name = byId.get('t' + m[1]);
    if (name) out.push({ table: name, x: (+m[2] + dx) * SX, y: (+m[3] + dy) * SY });
  }
  // Recadré en haut à gauche, marge comprise.
  const minX = Math.min(...out.map(p => p.x)), minY = Math.min(...out.map(p => p.y));
  return out.map(p => ({ table: p.table, x: Math.round(p.x - minX) + 24, y: Math.round(p.y - minY) + 24 }));
}

const domains = [];
for (const f of fs.readdirSync(dir).filter(f => /^data_area\d+\.json\.js$/.test(f))) {
  const a = load(f);
  domains.push({
    name: a.title.replace(/\s*\(\*\)\s*$/, ''),
    description: text(a.description),
    tables: a.area_links.filter(l => l.type === 'TABLE').map(l => l.object_name),
    layout: placements(a.erd)
  });
}

fs.writeFileSync(out, JSON.stringify({ tables: tables.filter(t => t.description || t.columns.length), domains, relations }));
console.log({ tables: tables.length, described: tables.filter(t => t.description).length,
  columns: tables.reduce((n, t) => n + t.columns.length, 0), domains: domains.length, relations: relations.length });
