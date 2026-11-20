using System.Text.RegularExpressions;
using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>Une relation suggérée telle que l'API la montre : les deux colonnes nommées, le score, ses raisons, jamais une valeur.</summary>
public record RelationSuggestionView(string Id, string DatabaseId, string ChildTable, string ChildColumn, string ChildColumnId,
    string ParentTable, string ParentColumn, string ParentColumnId, int Score, string Level, List<string> Reasons,
    long NonNullCount, long OrphanCount, string Status, DateTime DetectedAt);

/// <summary>Bilan d'une détection : candidats trouvés par le nom, vérifiés à la source, suggestions retenues, vérifications en échec.</summary>
public record DetectionRunView(int Candidates, int Checked, int Suggested, int Failed, int Skipped, List<string> Errors);

/// <summary>Bilan d'une acceptation en lot.</summary>
public record AcceptBatchView(int Accepted, int Skipped);

/// <summary>Un rapprochement par le nom et le type, avant toute vérification à la source.</summary>
public record RelationCandidate(ColumnInfo Child, TableInfo ChildTable, ColumnInfo Parent, TableInfo ParentTable, int NameScore,
    int TypeScore, int TargetScore, List<string> Reasons);

/// <summary>
///     Détection de relations non déclarées, sans modèle : une colonne <c>client_id</c> / <c>id_client</c> / <c>clientId</c> est
///     rapprochée de la table <c>client(s)</c> quand les types sont compatibles et que la cible est une clé primaire ou une colonne
///     unique. Ne lit que le catalogue : la vérification par les données est faite ensuite par <see cref="RelationDetectionService" />.
/// </summary>
public static partial class RelationCandidates
{
    /// <summary>Mots qui marquent « identifiant » dans un nom de colonne : ils ne disent pas quelle table est visée.</summary>
    private static readonly HashSet<string> Markers = new(StringComparer.Ordinal) { "id", "fk", "code", "cod", "ref", "key", "no", "num" };

    private static readonly HashSet<string> TablePrefixes = new(StringComparer.Ordinal) { "tbl", "t", "tb", "dim", "ref", "tab" };

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex Camel();

    /// <summary>Découpe un nom en mots minuscules : <c>ClientId</c>, <c>client_id</c>, <c>client-id</c> → [client, id].</summary>
    public static List<string> Words(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        var spaced = Camel().Replace(name, "_");
        return spaced.ToLowerInvariant().Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Le pluriel retiré du dernier mot (<c>clients</c> → <c>client</c>, <c>categories</c> → <c>category</c>).</summary>
    public static string Singular(string word)
    {
        if (word.Length <= 3) return word;
        if (word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.EndsWith("ses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal) ||
            word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal))
            return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    /// <summary>Les mots d'un nom de table, sans préfixe technique (<c>tbl_</c>…) et au singulier.</summary>
    public static List<string> TableWords(string tableName)
    {
        var words = Words(tableName);
        while (words.Count > 1 && TablePrefixes.Contains(words[0])) words.RemoveAt(0);
        words[^1] = Singular(words[^1]);
        return words;
    }

    /// <summary>
    ///     La table que vise le nom d'une colonne : les mots restants une fois les marqueurs d'identifiant retirés aux deux bouts.
    ///     Vide si le nom n'est qu'un marqueur (<c>id</c>) ou n'en porte aucun (<c>nom</c>).
    /// </summary>
    public static List<string> Stem(string columnName)
    {
        var words = Words(columnName);
        if (words.Count < 2 || !(Markers.Contains(words[0]) || Markers.Contains(words[^1]))) return [];
        while (words.Count > 0 && Markers.Contains(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && Markers.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count > 0) words[^1] = Singular(words[^1]);
        return words;
    }

    /// <summary>Famille de type : deux colonnes de familles différentes ne se rapprochent pas. Vide : type inconnu (SQLite sans type).</summary>
    public static string TypeFamily(string dataType)
    {
        var t = dataType.ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren > 0) t = t[..paren];
        t = t.Trim();
        if (t.Length == 0) return "";
        if (t.Contains("uuid") || t.Contains("uniqueidentifier") || t == "guid") return "guid";
        if (t.Contains("char") || t.Contains("text") || t.Contains("clob") || t.Contains("string") || t == "citext") return "text";
        if (t.Contains("int") || t.Contains("serial") || t is "number" or "decimal" or "numeric" or "bigint" or "smallint") return "number";
        if (t.Contains("date") || t.Contains("time")) return "date";
        return t;
    }

    private static bool Compatible(string a, string b) => a.Length == 0 || b.Length == 0 || a == b;

    /// <summary>Colonnes qu'une colonne unique ou une clé primaire simple désigne : identifiant de la colonne → origine de l'unicité.</summary>
    private static Dictionary<string, string> UniqueColumns(TableInfo table, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<IndexInfo> indexes)
    {
        var found = new Dictionary<string, string>();
        var keys = columns.Where(c => c.IsPrimaryKey).ToList();
        if (keys.Count == 1) found[keys[0].Id] = "la clé primaire";
        foreach (var index in indexes.Where(i => i.IsUnique))
        {
            var names = index.ColumnNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length != 1) continue;
            var column = columns.FirstOrDefault(c => c.Name.Equals(names[0], StringComparison.OrdinalIgnoreCase));
            if (column != null) found.TryAdd(column.Id, "un index unique");
        }

        return found;
    }

    /// <summary>
    ///     Candidats d'une base : pour chaque colonne dont le nom vise une autre table, la colonne unique de cette table. Les paires déjà
    ///     reliées (dans un sens ou dans l'autre) et celles que <paramref name="excluded" /> écarte (refusées) ne reviennent pas.
    /// </summary>
    public static List<RelationCandidate> Find(IReadOnlyList<TableInfo> tables, IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<IndexInfo> indexes, IEnumerable<RelationInfo> relations, Func<string, string, bool> excluded)
    {
        var realTables = tables.Where(t => t.TableType == "Table").ToList();
        var columnsByTable = columns.ToLookup(c => c.TableId);
        var indexesByTable = indexes.ToLookup(i => i.TableId);

        var linked = new HashSet<(string, string)>();
        var referencing = new HashSet<(string Column, string ParentTable)>();
        var tableOfColumn = columns.ToDictionary(c => c.Id, c => c.TableId);
        foreach (var relation in relations)
            foreach (var (first, second) in relation.Pairs())
            {
                linked.Add((first, second));
                linked.Add((second, first));
                if (tableOfColumn.TryGetValue(first, out var parentTable)) referencing.Add((second, parentTable));
            }

        // La table visée se cherche par ses mots : « client » → client, clients, tbl_client ; « billing_customer » → customer (rôle).
        var targets = realTables.Select(t => (Table: t, Words: TableWords(t.Name), Uniques: UniqueColumns(t, columnsByTable[t.Id].ToList(), indexesByTable[t.Id].ToList())))
            .Where(t => t.Uniques.Count > 0).ToList();

        var found = new List<RelationCandidate>();
        foreach (var childTable in realTables)
        foreach (var child in columnsByTable[childTable.Id])
        {
            var stem = Stem(child.Name);
            if (stem.Count == 0) continue;
            foreach (var target in targets)
            {
                if (target.Table.Id == childTable.Id) continue;
                int nameScore;
                string nameReason;
                if (stem.SequenceEqual(target.Words))
                {
                    nameScore = 40;
                    nameReason = $"Le nom `{child.Name}` désigne la table `{target.Table.Name}`.";
                }
                else if (stem.Count > target.Words.Count && stem.TakeLast(target.Words.Count).SequenceEqual(target.Words))
                {
                    nameScore = 28;
                    nameReason = $"Le nom `{child.Name}` se termine par celui de la table `{target.Table.Name}` (rôle : {string.Join(' ', stem.Take(stem.Count - target.Words.Count))}).";
                }
                else continue;

                var childFamily = TypeFamily(child.DataType);
                // La cible : la colonne unique dont le type convient ; à égalité, celle dont le nom est un identifiant.
                var parent = target.Uniques
                    .Select(u => (Column: columnsByTable[target.Table.Id].First(c => c.Id == u.Key), Origin: u.Value))
                    .Where(u => Compatible(childFamily, TypeFamily(u.Column.DataType)))
                    .OrderByDescending(u => Markers.Contains(Words(u.Column.Name).LastOrDefault()!) ? 1 : 0)
                    .ThenBy(u => u.Origin == "la clé primaire" ? 0 : 1)
                    .Cast<(ColumnInfo Column, string Origin)?>().FirstOrDefault();
                if (parent is not { } p) continue;
                if (linked.Contains((child.Id, p.Column.Id)) || referencing.Contains((child.Id, target.Table.Id))) continue;
                if (excluded(child.Id, p.Column.Id)) continue;

                var known = childFamily.Length > 0 && TypeFamily(p.Column.DataType).Length > 0;
                var reasons = new List<string>
                {
                    nameReason,
                    known
                        ? $"Types compatibles ({child.DataType} / {p.Column.DataType})."
                        : "Type d'une des colonnes inconnu : compatibilité non établie.",
                    $"`{target.Table.Name}.{p.Column.Name}` est {p.Origin}."
                };
                found.Add(new RelationCandidate(child, childTable, p.Column, target.Table, nameScore, known ? 10 : 5,
                    p.Origin == "la clé primaire" ? 15 : 12, reasons));
            }
        }

        return found;
    }
}

/// <summary>
///     Détection assistée des relations : les candidats du nom et du type (<see cref="RelationCandidates" />) sont vérifiés à la source
///     par un <c>NOT EXISTS</c> en lecture seule (des comptes, jamais une valeur), puis notés et proposés à la relecture. Rien n'est
///     créé sans acceptation : accepter fait une relation manuelle marquée <c>detected</c>. Désactivé avec le profilage
///     (<c>Catalog:AllowProfiling</c>), dont elle partage les garde-fous.
/// </summary>
public class RelationDetectionService(ICatalogRepository repository, DatabaseImportService import,
    IEnumerable<IColumnProfiler> profilers, IOptions<CatalogOptions> options)
{
    /// <summary>Marque les relations créées par l'acceptation d'une suggestion.</summary>
    public const string DetectedMarker = "detected";

    /// <summary>En dessous, la table cible est trop petite pour que l'inclusion prouve quelque chose (codes, statuts).</summary>
    public const int SmallTarget = 20;

    private CatalogOptions Options => options.Value;

    public static string LevelOf(int score) => score >= 75 ? "probable" : score >= 55 ? "possible" : "faible";

    /// <summary>Score d'inclusion : aucune orpheline vaut le maximum, au-delà de 5 % la relation est écartée (<c>null</c>).</summary>
    public static int? InclusionScore(long nonNull, long orphans)
    {
        if (nonNull <= 0) return null;
        var ratio = (double)orphans / nonNull;
        return ratio == 0 ? 30 : ratio <= 0.01 ? 20 : ratio <= 0.05 ? 10 : null;
    }

    private void EnsureAllowed()
    {
        if (!Options.AllowProfiling)
            throw new GranitExceptions("La détection des relations est désactivée (Catalog:AllowProfiling = false) : elle vérifie les candidats en lisant des comptes à la source, pas seulement son schéma.");
    }

    private IColumnProfiler ProfilerOf(string type) => profilers.For(type, "la détection");

    /// <summary>
    ///     Détecte les relations d'une base et remplace ses suggestions en attente (les refusées et les acceptées restent).
    ///     <c>null</c> si la base n'existe pas.
    /// </summary>
    public async Task<DetectionRunView?> DetectAsync(string databaseId, CancellationToken ct = default)
    {
        EnsureAllowed();
        var database = await repository.GetAsync<DatabaseInfo>(databaseId, ct);
        if (database == null) return null;
        var source = (await import.ResolveSourceAsync(database))!.Value;
        var profiler = ProfilerOf(source.Type);

        var byDatabase = CatalogFilter.ForDatabase(databaseId);
        var tables = (await repository.GetAllAsync<TableInfo>(byDatabase, ct)).ToList();
        var columns = (await repository.GetAllAsync<ColumnInfo>(byDatabase, ct)).ToList();
        var indexes = (await repository.GetAllAsync<IndexInfo>(byDatabase, ct)).ToList();
        var relations = await repository.GetRelationsOfDatabaseAsync(databaseId, ct);
        var previous = (await repository.GetAllAsync<RelationSuggestionInfo>(byDatabase, ct)).ToList();
        var decided = previous.Where(s => s.Status != RelationSuggestionInfo.Pending)
            .Select(s => (s.ChildColumnId, s.ParentColumnId)).ToHashSet();

        var candidates = RelationCandidates.Find(tables, columns, indexes, relations, (child, parent) => decided.Contains((child, parent)))
            .OrderByDescending(c => c.NameScore).ThenBy(c => c.ChildTable.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Child.Name, StringComparer.Ordinal).ToList();
        var take = Math.Max(1, Options.RelationDetectionMaxChecks);
        var skipped = Math.Max(0, candidates.Count - take);

        var suggestions = new List<RelationSuggestionInfo>();
        var errors = new List<string>();
        int checkedCount = 0, failed = 0;
        var now = DateTime.UtcNow;
        foreach (var candidate in candidates.Take(take))
        {
            ct.ThrowIfCancellationRequested();
            InclusionResult result;
            try
            {
                result = await profiler.CheckInclusionAsync(source.Source,
                    new InclusionCheck(candidate.ChildTable.Name, candidate.Child.Name, candidate.ParentTable.Name, candidate.Parent.Name),
                    Options.ProfilingTimeoutSeconds, ct);
                checkedCount++;
            }
            catch (GranitExceptions e)
            {
                failed++;
                if (errors.Count < 20) errors.Add($"{candidate.ChildTable.Name}.{candidate.Child.Name} : {e.Message}");
                continue;
            }

            if (InclusionScore(result.ChildNonNull, result.Orphans) is not { } inclusion) continue;
            var score = candidate.NameScore + candidate.TypeScore + candidate.TargetScore + inclusion;
            var reasons = new List<string>(candidate.Reasons);
            var found = result.ChildNonNull - result.Orphans;
            reasons.Add(result.Orphans == 0
                ? $"100 % des {result.ChildNonNull:N0} valeurs existent dans `{candidate.ParentTable.Name}.{candidate.Parent.Name}`."
                : $"{found:N0} valeurs sur {result.ChildNonNull:N0} ({100.0 * found / result.ChildNonNull:0.#} %) existent dans `{candidate.ParentTable.Name}.{candidate.Parent.Name}` ; {result.Orphans:N0} sans équivalent.");
            if (result.ParentRows < SmallTarget)
            {
                // Une petite table de codes (statuts, types) se retrouve par hasard dans n'importe quelle colonne d'entiers.
                score -= 25;
                reasons.Add($"La table cible ne compte que {result.ParentRows:N0} lignes : l'inclusion prouve peu.");
            }

            score = Math.Clamp(score, 0, 100);
            if (score < 40) continue;
            suggestions.Add(new RelationSuggestionInfo
            {
                Id = Guid.NewGuid().ToString(), DatabaseId = databaseId, ChildColumnId = candidate.Child.Id,
                ChildTableId = candidate.ChildTable.Id, ParentColumnId = candidate.Parent.Id, ParentTableId = candidate.ParentTable.Id,
                Score = score, Reasons = string.Join("\n", reasons), NonNullCount = result.ChildNonNull, OrphanCount = result.Orphans,
                Status = RelationSuggestionInfo.Pending, DetectedAt = now
            });
        }

        await repository.InTransactionAsync(async repo =>
        {
            foreach (var old in previous.Where(s => s.Status == RelationSuggestionInfo.Pending))
                await repo.DeleteAsync<RelationSuggestionInfo>(old.Id, ct);
            foreach (var suggestion in suggestions) await repo.CreateAsync(suggestion, ct);
            return 0;
        }, ct);

        return new DetectionRunView(candidates.Count, checkedCount, suggestions.Count, failed, skipped, errors);
    }

    /// <summary>Les suggestions d'une base, les mieux notées d'abord ; <c>status</c> filtre (pending par défaut). <c>null</c> si la base n'existe pas.</summary>
    public async Task<List<RelationSuggestionView>?> ListAsync(string databaseId, string? status, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var wanted = string.IsNullOrWhiteSpace(status) ? RelationSuggestionInfo.Pending : status.Trim().ToLowerInvariant();
        var rows = (await repository.GetByDatabaseAsync<RelationSuggestionInfo>(databaseId, ct))
            .Where(s => s.Status == wanted).ToList();
        var columns = (await repository.GetByDatabaseAsync<ColumnInfo>(databaseId, ct)).ToDictionary(c => c.Id);
        var tables = (await repository.GetByDatabaseAsync<TableInfo>(databaseId, ct)).ToDictionary(t => t.Id);
        return rows.Where(s => columns.ContainsKey(s.ChildColumnId) && columns.ContainsKey(s.ParentColumnId))
            .OrderByDescending(s => s.Score).ThenBy(s => tables[s.ChildTableId].Name, StringComparer.Ordinal)
            .Select(s => ViewOf(s, columns, tables)).ToList();
    }

    private static RelationSuggestionView ViewOf(RelationSuggestionInfo s, IReadOnlyDictionary<string, ColumnInfo> columns,
        IReadOnlyDictionary<string, TableInfo> tables) =>
        new(s.Id, s.DatabaseId, tables[s.ChildTableId].Name, columns[s.ChildColumnId].Name, s.ChildColumnId,
            tables[s.ParentTableId].Name, columns[s.ParentColumnId].Name, s.ParentColumnId, s.Score, LevelOf(s.Score),
            s.Reasons.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(), s.NonNullCount, s.OrphanCount, s.Status, s.DetectedAt);

    private async Task<RelationSuggestionInfo?> FindAsync(string databaseId, string suggestionId, CancellationToken ct)
    {
        var suggestion = await repository.GetAsync<RelationSuggestionInfo>(suggestionId, ct);
        return suggestion?.DatabaseId == databaseId ? suggestion : null;
    }

    /// <summary>
    ///     Accepte une suggestion : crée la relation manuelle (marquée <c>detected</c>, la colonne référencée en premier). Refusée si
    ///     les colonnes n'existent plus ou sont déjà reliées. <c>null</c> si la suggestion n'existe pas dans cette base.
    /// </summary>
    public async Task<RelationInfo?> AcceptAsync(string databaseId, string suggestionId, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(databaseId, suggestionId, ct);
        if (suggestion == null) return null;
        if (suggestion.Status != RelationSuggestionInfo.Pending)
            throw new GranitExceptions("Cette suggestion a déjà été traitée.");
        return await CreateRelationAsync(suggestion, ct);
    }

    private async Task<RelationInfo> CreateRelationAsync(RelationSuggestionInfo suggestion, CancellationToken ct)
    {
        var parent = await repository.GetAsync<ColumnInfo>(suggestion.ParentColumnId, ct);
        var child = await repository.GetAsync<ColumnInfo>(suggestion.ChildColumnId, ct);
        if (parent == null || child == null) throw new GranitExceptions("Une des colonnes de la suggestion n'existe plus : relancez la détection.");
        var existing = await repository.GetRelationsOfDatabaseAsync(suggestion.DatabaseId, ct);
        if (existing.Any(r => (r.FirstId == parent.Id && r.SecondId == child.Id) || (r.FirstId == child.Id && r.SecondId == parent.Id)))
            throw new GranitExceptions("Ces deux colonnes sont déjà reliées.");

        var relation = new RelationInfo
        {
            Id = Guid.NewGuid().ToString(), FirstId = parent.Id, SecondId = child.Id, RelationType = "One-to-Many", MetaInfo = DetectedMarker
        };
        await repository.InTransactionAsync(async repo =>
        {
            await repo.CreateAsync(relation, ct);
            suggestion.Status = RelationSuggestionInfo.Accepted;
            await repo.UpdateAsync(suggestion.Id, suggestion, ct);
            return 0;
        }, ct);
        return relation;
    }

    /// <summary>Refuse une suggestion : elle n'est plus proposée par les détections suivantes. <c>null</c> si elle n'existe pas dans cette base.</summary>
    public async Task<RelationSuggestionView?> RejectAsync(string databaseId, string suggestionId, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(databaseId, suggestionId, ct);
        if (suggestion == null) return null;
        if (suggestion.Status == RelationSuggestionInfo.Accepted)
            throw new GranitExceptions("Cette suggestion a déjà été acceptée : supprimez la relation pour la retirer.");
        suggestion.Status = RelationSuggestionInfo.Rejected;
        await repository.UpdateAsync(suggestion.Id, suggestion, ct);
        return (await ListAsync(databaseId, RelationSuggestionInfo.Rejected, ct))!.First(s => s.Id == suggestionId);
    }

    /// <summary>Accepte toutes les suggestions en attente dont le score atteint <paramref name="minScore" /> ; celles qui échouent sont comptées à part.</summary>
    public async Task<AcceptBatchView?> AcceptAboveAsync(string databaseId, int minScore, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var rows = (await repository.GetByDatabaseAsync<RelationSuggestionInfo>(databaseId, ct))
            .Where(s => s.Status == RelationSuggestionInfo.Pending && s.Score >= minScore).OrderByDescending(s => s.Score).ToList();
        int accepted = 0, skipped = 0;
        foreach (var suggestion in rows)
        {
            try
            {
                await CreateRelationAsync(suggestion, ct);
                accepted++;
            }
            catch (GranitExceptions)
            {
                skipped++;
            }
        }

        return new AcceptBatchView(accepted, skipped);
    }
}
