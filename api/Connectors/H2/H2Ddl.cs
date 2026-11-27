using System.Globalization;
using System.Text;

namespace GranitDB.API.Connectors.H2;

/// <summary>
///     Construit le schéma d'une base à partir des instructions que H2 conserve. Leur forme est celle que H2 écrit lui-même
///     (identifiants entre guillemets, une instruction par objet) ; les instructions sans rapport avec le schéma documenté
///     (utilisateurs, droits, séquences, déclencheurs, alias, tables liées…) sont ignorées.
/// </summary>
internal static class H2Ddl
{
    private const string DefaultSchema = "PUBLIC";

    /// <summary>Les mots qui terminent le type d'une colonne ou sa valeur par défaut.</summary>
    private static readonly HashSet<string> ColumnClauses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFAULT", "GENERATED", "NOT", "NULL", "SEQUENCE", "SELECTIVITY", "COMMENT", "NULL_TO_DEFAULT", "INVISIBLE", "VISIBLE",
        "AUTO_INCREMENT", "IDENTITY", "ON", "CHECK", "PRIMARY", "UNIQUE", "REFERENCES", "AS"
    };

    private sealed record Column(string Name, string Type, bool IsNullable, string? Default);

    private sealed record Table(string Kind, List<Column> Columns);

    private sealed record ForeignKey(string Table, string Name, List<string> Columns, string RefTable, List<string> RefColumns);

    public static SourceSchema Build(string databaseName, string type, IEnumerable<string> statements)
    {
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        var primaryKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var foreignKeys = new List<ForeignKey>();
        var indexes = new List<SourceIndex>();
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sql in statements)
        {
            var reader = new Reader(sql);
            if (reader.Take("CREATE")) Create(reader, tables, indexes, domains);
            else if (reader.Take("ALTER") && reader.Take("TABLE")) Constraint(reader, primaryKeys, foreignKeys);
        }

        var sourceTables = tables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t =>
        {
            var keys = primaryKeys.GetValueOrDefault(t.Key);
            return new SourceTable(t.Key, t.Value.Kind, t.Value.Columns.Select((c, i) => new SourceColumn(c.Name, Resolve(c.Type, domains),
                c.IsNullable && keys?.Contains(c.Name) != true, keys?.Contains(c.Name) == true, c.Default, i)).ToList());
        }).ToList();

        var rows = new List<(string, string, string, string, string)>();
        foreach (var key in foreignKeys.OrderBy(k => k.Table, StringComparer.Ordinal).ThenBy(k => k.Name, StringComparer.Ordinal))
        {
            // « REFERENCES parent » sans colonnes : la clé primaire du parent.
            var references = key.RefColumns.Count > 0 ? key.RefColumns : primaryKeys.GetValueOrDefault(key.RefTable) ?? [];
            if (!tables.ContainsKey(key.Table) || !tables.ContainsKey(key.RefTable)) continue;
            for (var i = 0; i < Math.Min(key.Columns.Count, references.Count); i++)
                rows.Add((key.Table, key.Name, key.Columns[i], key.RefTable, references[i]));
        }

        var kept = indexes.Where(i => tables.ContainsKey(i.Table))
            // L'index d'une clé primaire n'est pas répété.
            .Where(i => !(i.IsUnique && primaryKeys.TryGetValue(i.Table, out var keys) && i.Name.StartsWith("PRIMARY_KEY", StringComparison.Ordinal) &&
                          keys.ToHashSet().SetEquals(i.Columns)))
            .OrderBy(i => i.Table, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();

        return new SourceSchema(databaseName, type, sourceTables, SchemaReading.GroupForeignKeys(rows), kept);
    }

    /// <summary>Le type de base d'un domaine (un domaine peut en désigner un autre).</summary>
    private static string Resolve(string type, Dictionary<string, string> domains)
    {
        for (var depth = 0; depth < 10 && type.StartsWith('"'); depth++)
        {
            var reader = new Reader(type);
            var name = reader.QualifiedName();
            if (name == null || !reader.End || !domains.TryGetValue(name, out var target)) break;
            type = target;
        }

        return type;
    }

    private static void Create(Reader reader, Dictionary<string, Table> tables, List<SourceIndex> indexes,
        Dictionary<string, string> domains)
    {
        var temporary = false;
        var linked = false;
        var unique = false;
        var primary = false;
        while (true)
        {
            if (reader.Take("OR") || reader.Take("REPLACE") || reader.Take("FORCE") || reader.Take("CACHED") ||
                reader.Take("MEMORY") || reader.Take("GLOBAL") || reader.Take("LOCAL") || reader.Take("HASH") ||
                reader.Take("SPATIAL") || reader.Take("NULLS") || reader.Take("DISTINCT") || reader.Take("ALL") ||
                reader.Take("MATERIALIZED")) continue;
            if (reader.Take("TEMPORARY") || reader.Take("TEMP")) temporary = true;
            else if (reader.Take("LINKED")) linked = true;
            else if (reader.Take("UNIQUE")) unique = true;
            else if (reader.Take("PRIMARY"))
            {
                reader.Take("KEY");
                primary = true;
            }
            else break;
        }

        if (reader.Take("DOMAIN"))
        {
            reader.IfNotExists();
            var name = reader.QualifiedName();
            reader.SkipComment();
            if (name != null && reader.Take("AS")) domains[name] = Compact(reader.Until(ColumnClauses, atLeastOne: true));
        }
        else if (reader.Take("TABLE"))
        {
            if (temporary || linked) return;
            reader.IfNotExists();
            var name = reader.QualifiedName();
            reader.SkipComment();
            if (name == null || !reader.Take("(")) return;
            tables[name] = new Table("Table", Columns(reader));
        }
        else if (reader.Take("VIEW"))
        {
            reader.IfNotExists();
            var name = reader.QualifiedName();
            reader.SkipComment();
            if (name == null) return;
            // H2 ne garde que le nom des colonnes d'une vue : leur type se déduit de la requête, qui n'est pas exécutée.
            var columns = new List<Column>();
            if (reader.Take("("))
                foreach (var part in reader.List())
                    columns.Add(new Column(new Reader(part).Identifier() ?? part, "ANY", true, null));
            tables[name] = new Table("View", columns);
        }
        else if (reader.Take("INDEX") || primary)
        {
            // « CREATE PRIMARY KEY » est l'index d'une clé primaire, déjà décrite par sa contrainte.
            if (primary) return;
            reader.IfNotExists();
            var name = reader.Peek("ON") ? null : reader.QualifiedName();
            if (name == null || !reader.Take("ON")) return;
            var table = reader.QualifiedName();
            if (table == null || !reader.Take("(")) return;
            var columns = reader.List().Select(p => new Reader(p).Identifier() ?? p).ToList();
            indexes.Add(new SourceIndex(table, name[(name.LastIndexOf('.') + 1)..], unique, columns));
        }
    }

    private static List<Column> Columns(Reader reader)
    {
        var columns = new List<Column>();
        foreach (var definition in reader.List())
        {
            var column = new Reader(definition);
            // Une contrainte écrite dans la table n'est pas une colonne.
            if (column.Peek("CONSTRAINT") || column.Peek("PRIMARY") || column.Peek("FOREIGN") || column.Peek("CHECK") ||
                (column.Peek("UNIQUE"))) continue;
            var name = column.Identifier();
            if (name == null) continue;

            var type = column.Until(ColumnClauses, atLeastOne: false);
            var nullable = true;
            string? defaultValue = null;
            while (!column.End)
            {
                if (column.Take("GENERATED"))
                {
                    // « GENERATED … AS IDENTITY(…) » ou colonne calculée : pas une valeur par défaut.
                    column.Take("ALWAYS");
                    if (column.Take("BY")) column.Take("DEFAULT");
                    if (column.Take("AS")) column.Take("IDENTITY");
                }
                else if (column.Take("NOT"))
                {
                    if (column.Take("NULL")) nullable = false;
                }
                else if (column.Take("DEFAULT"))
                {
                    if (column.Take("ON")) column.Take("NULL");
                    else defaultValue = column.Until(ColumnClauses, atLeastOne: true);
                }
                else column.Skip();
            }

            columns.Add(new Column(name, Compact(type), nullable, defaultValue));
        }

        return columns;
    }

    /// <summary><c>DECIMAL(10, 2)</c> → <c>DECIMAL(10,2)</c>.</summary>
    private static string Compact(string type) => type.Length == 0 ? "ANY" : type.Replace(", ", ",");

    private static void Constraint(Reader reader, Dictionary<string, List<string>> primaryKeys, List<ForeignKey> foreignKeys)
    {
        reader.IfExists();
        var table = reader.QualifiedName();
        if (table == null || !reader.Take("ADD") || !reader.Take("CONSTRAINT")) return;
        reader.IfNotExists();
        var name = reader.QualifiedName();
        reader.SkipComment();
        if (name == null) return;

        if (reader.Take("PRIMARY"))
        {
            reader.Take("KEY");
            reader.Take("HASH");
            if (reader.Take("(")) primaryKeys[table] = Names(reader);
        }
        else if (reader.Take("FOREIGN"))
        {
            if (!reader.Take("KEY") || !reader.Take("(")) return;
            var columns = Names(reader);
            if (reader.Take("INDEX")) reader.QualifiedName();
            if (!reader.Take("REFERENCES")) return;
            var target = reader.QualifiedName() ?? table;
            var references = reader.Take("(") ? Names(reader) : [];
            foreignKeys.Add(new ForeignKey(table, name, columns, target, references));
        }
    }

    private static List<string> Names(Reader reader) => reader.List().Select(p => new Reader(p).Identifier() ?? p).ToList();

    private enum Kind
    {
        Word,
        Quoted,
        Literal,
        Symbol
    }

    private readonly record struct Token(Kind Kind, string Value, int Start, int End);

    /// <summary>Découpe une instruction en mots, identifiants, littéraux et symboles ; les commentaires sont sautés.</summary>
    private sealed class Reader
    {
        private readonly string _sql;
        private readonly List<Token> _tokens;
        private int _index;

        public Reader(string sql)
        {
            _sql = sql;
            _tokens = Tokenize(sql);
        }

        public bool End => _index >= _tokens.Count;

        public void Skip() => _index++;

        public bool Peek(string word) => !End && _tokens[_index].Kind is Kind.Word or Kind.Symbol &&
                                         _tokens[_index].Value.Equals(word, StringComparison.OrdinalIgnoreCase);

        public bool Take(string word)
        {
            if (!Peek(word)) return false;
            _index++;
            return true;
        }

        public void IfNotExists()
        {
            if (!Take("IF")) return;
            Take("NOT");
            Take("EXISTS");
        }

        public void IfExists()
        {
            if (Take("IF")) Take("EXISTS");
        }

        public void SkipComment()
        {
            if (Take("COMMENT") && !End) _index++;
        }

        public string? Identifier()
        {
            if (End || _tokens[_index].Kind is not (Kind.Quoted or Kind.Word)) return null;
            return _tokens[_index++].Value;
        }

        /// <summary><c>"SCHEMA"."NOM"</c> : le schéma <c>PUBLIC</c> reste implicite, comme pour les autres moteurs.</summary>
        public string? QualifiedName()
        {
            var first = Identifier();
            if (first == null) return null;
            var parts = new List<string> { first };
            while (Take("."))
            {
                var next = Identifier();
                if (next == null) break;
                parts.Add(next);
            }

            if (parts.Count == 1) return parts[0];
            return parts[^2] == DefaultSchema ? parts[^1] : $"{parts[^2]}.{parts[^1]}";
        }

        /// <summary>Après une parenthèse ouvrante : le texte de chaque élément, jusqu'à la parenthèse fermante comprise.</summary>
        public List<string> List()
        {
            var items = new List<string>();
            var depth = 0;
            var first = _index;
            for (; !End; _index++)
            {
                var token = _tokens[_index];
                if (token.Kind != Kind.Symbol) continue;
                if (token.Value is "(" or "[") depth++;
                else if (token.Value is ")" or "]" && depth > 0) depth--;
                else if (depth == 0 && token.Value is ")" or ",")
                {
                    if (_index > first) items.Add(Text(first, _index - 1));
                    first = _index + 1;
                    if (token.Value == ")")
                    {
                        _index++;
                        break;
                    }
                }
            }

            return items;
        }

        /// <summary>Le texte jusqu'au prochain mot de <paramref name="stops" /> hors parenthèses.</summary>
        public string Until(HashSet<string> stops, bool atLeastOne)
        {
            var first = _index;
            var depth = 0;
            for (; !End; _index++)
            {
                var token = _tokens[_index];
                if (depth == 0 && token.Kind == Kind.Word && stops.Contains(token.Value) && !(atLeastOne && _index == first)) break;
                if (token.Kind != Kind.Symbol) continue;
                if (token.Value is "(" or "[") depth++;
                else if (token.Value is ")" or "]") depth--;
            }

            return _index > first ? Text(first, _index - 1) : "";
        }

        private string Text(int first, int last) => _sql[_tokens[first].Start.._tokens[last].End];

        private static List<Token> Tokenize(string sql)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                var start = i;
                if (char.IsWhiteSpace(c)) i++;
                else if (c == '-' && Next(sql, i) == '-')
                {
                    var end = sql.IndexOf('\n', i);
                    i = end < 0 ? sql.Length : end + 1;
                }
                else if (c == '/' && Next(sql, i) == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                }
                else if (c is 'U' or 'u' && Next(sql, i) == '&' && i + 2 < sql.Length && sql[i + 2] is '"' or '\'')
                {
                    // U&"…" : texte avec des caractères échappés (\XXXX, \+XXXXXX).
                    var quote = sql[i + 2];
                    i += 2;
                    var value = Unescape(Quoted(sql, ref i, quote));
                    tokens.Add(new Token(quote == '"' ? Kind.Quoted : Kind.Literal, value, start, i));
                }
                else if (c is '"' or '\'')
                {
                    var value = Quoted(sql, ref i, c);
                    tokens.Add(new Token(c == '"' ? Kind.Quoted : Kind.Literal, value, start, i));
                }
                else if (c == '$' && Next(sql, i) == '$')
                {
                    var end = sql.IndexOf("$$", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                    tokens.Add(new Token(Kind.Literal, "", start, i));
                }
                else if (char.IsLetterOrDigit(c) || c == '_')
                {
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    tokens.Add(new Token(Kind.Word, sql[start..i], start, i));
                }
                else tokens.Add(new Token(Kind.Symbol, c.ToString(), start, ++i));
            }

            return tokens;
        }

        private static char Next(string sql, int i) => i + 1 < sql.Length ? sql[i + 1] : '\0';

        /// <summary>Texte entre guillemets ; un guillemet doublé représente le caractère lui-même.</summary>
        private static string Quoted(string sql, ref int i, char quote)
        {
            var value = new StringBuilder();
            for (i++; i < sql.Length; i++)
            {
                if (sql[i] != quote)
                {
                    value.Append(sql[i]);
                    continue;
                }

                if (Next(sql, i) != quote)
                {
                    i++;
                    break;
                }

                value.Append(quote);
                i++;
            }

            return value.ToString();
        }

        private static string Unescape(string value)
        {
            var result = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    result.Append(value[i]);
                    continue;
                }

                if (value[i + 1] == '\\')
                {
                    result.Append('\\');
                    i++;
                    continue;
                }

                var extended = value[i + 1] == '+';
                var from = i + (extended ? 2 : 1);
                var length = extended ? 6 : 4;
                if (from + length <= value.Length && int.TryParse(value.AsSpan(from, length), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var code))
                {
                    if (extended && code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF) result.Append(char.ConvertFromUtf32(code));
                    else result.Append((char)code);
                    i = from + length - 1;
                }
                else result.Append(value[i]);
            }

            return result.ToString();
        }
    }
}
