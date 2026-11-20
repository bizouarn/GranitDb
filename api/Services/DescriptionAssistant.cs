using System.Text.Json;
using System.Text.RegularExpressions;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>Une proposition telle que l'API la montre : la cible nommée, le texte, le modèle, le doute, jamais une valeur de la source.</summary>
public record SuggestionView(string Id, string DatabaseId, string Kind, string TargetType, string TargetId, string TargetLabel,
    string Text, string Model, bool Uncertain, string Status, DateTime CreatedAt);

/// <summary>Avancement d'une documentation en lot : tables vues, propositions créées, échecs avec leurs raisons.</summary>
public record SuggestionJobView(string DatabaseId, string State, int TablesTotal, int TablesDone, int Suggested, int Failed,
    List<string> Errors, DateTime StartedAt, DateTime? FinishedAt);

/// <summary>Texte retouché à l'acceptation (facultatif).</summary>
public class AcceptSuggestionInput
{
    [System.ComponentModel.DataAnnotations.StringLength(2000)]
    public string? Text { get; set; }
}

/// <summary>Cible d'une proposition à la demande (bouton « Suggérer » de l'éditeur).</summary>
public class SuggestInput
{
    /// <summary><c>database</c>, <c>table</c>, <c>column</c> ou <c>relation</c>.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public string TargetType { get; set; } = "";

    /// <summary>Identifiant de la cible ; ignoré pour <c>database</c> (la base de la route).</summary>
    public string? TargetId { get; set; }
}

/// <summary>Colonnes dont le nom évoque une donnée personnelle ou secrète : proposées sensibles par des règles, jamais par un modèle.</summary>
public static class SensitiveRules
{
    private static readonly (string[] Words, string Reason)[] Rules =
    [
        (["email", "mail", "courriel"], "adresse e-mail"),
        (["iban", "bic", "rib"], "coordonnées bancaires"),
        (["phone", "tel", "telephone", "mobile", "portable", "gsm"], "numéro de téléphone"),
        (["birth", "birthday", "birthdate", "naissance", "dob"], "date de naissance"),
        (["ssn", "nir", "secu", "passport", "passeport"], "identifiant officiel"),
        (["password", "passwd", "pwd", "mdp", "secret", "token", "salt", "hash"], "secret ou mot de passe"),
        (["firstname", "lastname", "surname", "prenom", "fullname", "nom_famille"], "nom d'une personne"),
        (["address", "adresse", "street", "rue", "zip", "postcode", "cp"], "adresse postale"),
        (["card", "carte", "cvv", "cvc", "pan"], "moyen de paiement")
    ];

    /// <summary>La raison pour laquelle le nom évoque une donnée sensible, ou <c>null</c>.</summary>
    public static string? Reason(string columnName)
    {
        var words = RelationCandidates.Words(columnName);
        var joined = string.Join("", words);
        foreach (var (rule, reason) in Rules)
            // Un mot entier (« email », « user_email ») ; la forme collée (« firstname ») se retrouve aussi telle quelle.
            if (rule.Any(r => words.Contains(r) || (r.Length >= 5 && joined == r) || (r.Contains('_') && string.Join("_", words).Contains(r))))
                return $"Le nom `{columnName}` évoque : {reason}.";
        return null;
    }
}

/// <summary>
///     Assistant de documentation : propose des descriptions de bases, tables, colonnes et relations au moyen du modèle d'IA, et des
///     colonnes sensibles par règles. Les propositions vont dans une file de relecture ; accepter écrit dans le catalogue, avec
///     l'origine dans l'historique (« proposé par le modèle X »). Ne reçoit du catalogue que des noms, types, clés, descriptions
///     existantes et des comptes de profilage, jamais de valeur de ligne ni de colonne marquée sensible ; ne remplace jamais une
///     description déjà saisie.
/// </summary>
public partial class DescriptionAssistant(ICatalogRepository repository, IAiProvider ai, IOptions<CatalogOptions> catalog, IOptions<AiOptions> aiOptions,
    ILogger<DescriptionAssistant> log)
{
    /// <summary>Colonnes décrites par appel : un prompt court convient à un petit modèle.</summary>
    public const int ColumnsPerPrompt = 15;

    /// <summary>Relations décrites par appel.</summary>
    public const int RelationsPerPrompt = 10;

    private const int MaxText = 300;

    private const string TableSchema = """
        {"type":"object","properties":{"table":{"type":"string"},"columns":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"},"uncertain":{"type":"boolean"}},"required":["name","description","uncertain"],"additionalProperties":false}}},"required":["table","columns"],"additionalProperties":false}
        """;

    private const string TextSchema = """
        {"type":"object","properties":{"description":{"type":"string"},"uncertain":{"type":"boolean"}},"required":["description","uncertain"],"additionalProperties":false}
        """;

    private const string RelationsSchema = """
        {"type":"object","properties":{"relations":{"type":"array","items":{"type":"object","properties":{"index":{"type":"integer"},"description":{"type":"string"}},"required":["index","description"],"additionalProperties":false}}},"required":["relations"],"additionalProperties":false}
        """;

    private const string System =
        "Tu documentes une base de données pour des personnes du métier. Décris en une phrase courte (moins de 25 mots) ce que représente chaque élément, " +
        "d'après les seuls noms, types, clés et relations fournis. N'invente rien : si un nom est cryptique, mets uncertain à true et reste prudent.";

    private static readonly object JobsLock = new();
    private static readonly Dictionary<string, Job> Jobs = new();

    private string Model => aiOptions.Value.Model;

    private sealed class Job(string databaseId, int total)
    {
        public readonly CancellationTokenSource Cancel = new();
        public string State = "running";
        public int Total = total, Done, Suggested, Failed;
        public readonly List<string> Errors = [];
        public readonly DateTime StartedAt = DateTime.UtcNow;
        public DateTime? FinishedAt;
        public string DatabaseId = databaseId;

        public SuggestionJobView View()
        {
            lock (this) return new SuggestionJobView(DatabaseId, State, Total, Done, Suggested, Failed, [.. Errors], StartedAt, FinishedAt);
        }
    }

    // ---- Heuristique « nom cryptique » ----------------------------------------------------------------------------------------

    /// <summary>Un nom qui ne se lit pas (<c>ZX_T01</c>, <c>cdcl</c>, <c>t1</c>) : la proposition est marquée incertaine quoi que dise le modèle.</summary>
    public static bool IsCryptic(string name)
    {
        var words = RelationCandidates.Words(name);
        if (words.Count == 0) return true;
        foreach (var word in words)
        {
            if (word is "id" or "no" or "num") continue;
            if (CodeLike().IsMatch(word)) return true;
            if (word.Length >= 3 && word.All(char.IsLetter) && !word.Any(c => "aeiouy".Contains(c))) return true;
        }

        return words.All(w => w.Length <= 2) && !words.Contains("id");
    }

    [GeneratedRegex(@"^[a-z]{1,3}\d+$|^\d+[a-z]{0,3}$")]
    private static partial Regex CodeLike();

    // ---- Contexte du catalogue --------------------------------------------------------------------------------------------------

    private sealed class Context
    {
        public required DatabaseInfo Database;
        public required List<TableInfo> Tables;
        public required ILookup<string, ColumnInfo> Columns;
        public required Dictionary<string, ColumnInfo> ColumnById;
        public required Dictionary<string, TableInfo> TableById;
        public required List<RelationInfo> Relations;
        public required HashSet<string> Decided; // « type:id » déjà proposés (en attente ou refusés)
    }

    private async Task<Context> LoadAsync(string databaseId, CancellationToken ct)
    {
        var database = await repository.GetAsync<DatabaseInfo>(databaseId, ct) ?? throw new GranitExceptions("Cette base n'existe pas.");
        var byDatabase = new[] { new CatalogFilter("DatabaseId", databaseId) };
        var tables = (await repository.GetAllAsync<TableInfo>(byDatabase, ct)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var columns = (await repository.GetAllAsync<ColumnInfo>(byDatabase, ct)).OrderBy(c => c.Position).ToList();
        var relations = (await repository.GetRelationsOfDatabaseAsync(databaseId, ct)).ToList();
        var suggestions = await repository.GetAllAsync<DescriptionSuggestionInfo>(byDatabase, ct);
        return new Context
        {
            Database = database, Tables = tables, Columns = columns.ToLookup(c => c.TableId), ColumnById = columns.ToDictionary(c => c.Id),
            TableById = tables.ToDictionary(t => t.Id), Relations = relations,
            Decided = suggestions.Where(s => s.Kind == DescriptionSuggestionInfo.KindDescription && s.Status != DescriptionSuggestionInfo.Accepted)
                .Select(s => $"{s.TargetType}:{s.TargetId}").ToHashSet()
        };
    }

    private static string Cut(string? text, int max = 120)
    {
        var flat = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string Label(ColumnInfo column) =>
        $"{column.Name} ({(string.IsNullOrWhiteSpace(column.DataType) ? "type inconnu" : column.DataType)}" +
        $"{(column.IsPrimaryKey ? ", clé primaire" : "")}{(column.IsNullable ? "" : ", obligatoire")})";

    // ---- Appel du modèle --------------------------------------------------------------------------------------------------------

    private static string? ValidateTable(JsonElement e) =>
        e.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String && e.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
            ? null : "les champs table et columns sont attendus";

    private static string? ValidateText(JsonElement e) =>
        e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? null : "le champ description est attendu";

    private static string? ValidateRelations(JsonElement e) =>
        e.TryGetProperty("relations", out var r) && r.ValueKind == JsonValueKind.Array ? null : "le champ relations est attendu";

    /// <summary>Nettoie une description du modèle : une ligne, sans guillemets d'enrobage, bornée ; vide si elle ne dit rien.</summary>
    public static string Clean(string? text)
    {
        var s = Regex.Replace(text ?? "", @"\s+", " ").Trim().Trim('"', '«', '»', ' ');
        return s.Length <= MaxText ? s : s[..(MaxText - 1)].TrimEnd() + "…";
    }

    // ---- Génération pour une table ----------------------------------------------------------------------------------------------

    /// <summary>
    ///     Propose la description de la table et celles de ses colonnes qui n'en ont pas (hors colonnes sensibles). <paramref name="explicitRun" />
    ///     ignore les propositions déjà faites ou refusées (« régénérer »). Renvoie le nombre de propositions créées.
    /// </summary>
    private async Task<int> SuggestTableAsync(Context ctx, TableInfo table, bool wantTable, ISet<string>? onlyColumns, bool explicitRun, CancellationToken ct)
    {
        bool Open(string type, string id, string? description) =>
            string.IsNullOrWhiteSpace(description) && (explicitRun || !ctx.Decided.Contains($"{type}:{id}"));

        var all = ctx.Columns[table.Id].ToList();
        var visible = all.Where(c => !c.IsSensitive).ToList(); // une colonne sensible n'est jamais envoyée
        var wanted = visible.Where(c => (onlyColumns == null || onlyColumns.Contains(c.Id)) && Open("column", c.Id, c.Description)).ToList();
        wantTable = wantTable && Open("table", table.Id, table.Description);
        if (!wantTable && wanted.Count == 0) return 0;

        var profiles = (await repository.FindAsync<ColumnProfileInfo>(nameof(ColumnProfileInfo.TableId), table.Id, ct)).ToDictionary(p => p.ColumnId);
        var neighbours = Neighbours(ctx, table, visible);
        var styles = ctx.Tables.Where(t => t.Id != table.Id && !string.IsNullOrWhiteSpace(t.Description)).Take(3)
            .Select(t => $"- {t.Name} : {Cut(t.Description)}").ToList();

        var created = new List<DescriptionSuggestionInfo>();
        var batches = wanted.Chunk(ColumnsPerPrompt).Select(b => b.ToList()).ToList();
        if (batches.Count == 0) batches.Add([]);
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var askTable = wantTable && i == 0;
            var user = new System.Text.StringBuilder();
            user.AppendLine($"Base : {ctx.Database.Name}{(string.IsNullOrWhiteSpace(ctx.Database.Description) ? "" : " — " + Cut(ctx.Database.Description))}");
            user.AppendLine($"Table : {table.Name} ({(table.TableType == "View" ? "vue" : "table")})" +
                            (string.IsNullOrWhiteSpace(table.Description) ? "" : " — " + Cut(table.Description)));
            user.AppendLine("Colonnes de la table : " + string.Join(", ", visible.Select(c => c.Name)));
            if (neighbours.Count > 0) user.AppendLine("Relations :\n" + string.Join("\n", neighbours));
            if (styles.Count > 0) user.AppendLine("Exemples de style (descriptions déjà saisies) :\n" + string.Join("\n", styles));
            user.AppendLine(askTable ? "Décris la table (champ table)." : "Laisse le champ table vide.");
            if (batch.Count > 0)
            {
                user.AppendLine("Décris ces colonnes (champ columns, un élément par colonne, avec son nom exact) :");
                foreach (var column in batch)
                {
                    var profile = profiles.TryGetValue(column.Id, out var p) ? $" [profil : {ProfileKind.Of(p.RowCount, p.NullCount, p.DistinctCount)}]" : "";
                    user.AppendLine($"- {Label(column)}{profile}");
                }
            }
            else user.AppendLine("Laisse le tableau columns vide.");

            var response = await ai.CompleteAsync(new AiRequest(System, user.ToString(), TableSchema, ValidateTable), ct);
            var json = response.Json!.Value;
            var now = DateTime.UtcNow;
            if (askTable)
            {
                var text = Clean(json.GetProperty("table").GetString());
                if (text.Length > 0) created.Add(New(ctx, "table", table.Id, text, IsCryptic(table.Name), now));
            }

            var byName = batch.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in json.GetProperty("columns").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var n) || n.GetString() is not { } name) continue;
                // Un nom qui n'existe pas dans le lot est une invention du modèle : on l'ignore.
                if (!byName.TryGetValue(name, out var column) || !seen.Add(column.Name)) continue;
                var text = Clean(item.TryGetProperty("description", out var d) ? d.GetString() : null);
                if (text.Length == 0) continue;
                var doubt = item.TryGetProperty("uncertain", out var u) && u.ValueKind == JsonValueKind.True;
                created.Add(New(ctx, "column", column.Id, text, doubt || IsCryptic(column.Name), now));
            }
        }

        await StoreAsync(ctx.Database.Id, created, ct);
        return created.Count;
    }

    private DescriptionSuggestionInfo New(Context ctx, string targetType, string targetId, string text, bool uncertain, DateTime now) => new()
    {
        Id = Guid.NewGuid().ToString(), DatabaseId = ctx.Database.Id, Kind = DescriptionSuggestionInfo.KindDescription, TargetType = targetType,
        TargetId = targetId, Text = text, Model = Model, Uncertain = uncertain, Status = DescriptionSuggestionInfo.Pending, CreatedAt = now
    };

    /// <summary>Les relations de la table, en clair, avec la description de la table voisine : de quoi comprendre le rôle de ses colonnes.</summary>
    private static List<string> Neighbours(Context ctx, TableInfo table, List<ColumnInfo> visible)
    {
        var lines = new List<string>();
        var own = visible.Select(c => c.Id).ToHashSet();
        foreach (var relation in ctx.Relations)
        {
            if (!ctx.ColumnById.TryGetValue(relation.FirstId, out var parent) || !ctx.ColumnById.TryGetValue(relation.SecondId, out var child)) continue;
            if (!own.Contains(parent.Id) && !own.Contains(child.Id)) continue;
            // Une relation qui touche une colonne sensible n'est pas décrite : le nom de la colonne ne doit pas partir.
            if (parent.IsSensitive || child.IsSensitive) continue;
            var other = ctx.TableById[own.Contains(child.Id) ? parent.TableId : child.TableId];
            if (other.Id == table.Id) continue;
            lines.Add($"- {ctx.TableById[child.TableId].Name}.{child.Name} → {ctx.TableById[parent.TableId].Name}.{parent.Name}" +
                      (string.IsNullOrWhiteSpace(other.Description) ? "" : $" (« {other.Name} » : {Cut(other.Description, 80)})"));
            if (lines.Count >= 8) break;
        }

        return lines;
    }

    private async Task StoreAsync(string databaseId, List<DescriptionSuggestionInfo> created, CancellationToken ct)
    {
        if (created.Count == 0) return;
        await repository.InTransactionAsync(async repo =>
        {
            var pending = await repo.GetAllAsync<DescriptionSuggestionInfo>([new CatalogFilter("DatabaseId", databaseId)], ct);
            foreach (var old in pending.Where(p => p.Status == DescriptionSuggestionInfo.Pending && p.Kind == DescriptionSuggestionInfo.KindDescription &&
                                                    created.Any(c => c.TargetType == p.TargetType && c.TargetId == p.TargetId)))
                await repo.DeleteAsync<DescriptionSuggestionInfo>(old.Id, ct);
            foreach (var suggestion in created) await repo.CreateAsync(suggestion, ct);
            return 0;
        }, ct);
    }

    // ---- Base et relations ------------------------------------------------------------------------------------------------------

    private async Task<int> SuggestDatabaseAsync(Context ctx, bool explicitRun, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Database.Description) || (!explicitRun && ctx.Decided.Contains($"database:{ctx.Database.Id}"))) return 0;
        var user = new System.Text.StringBuilder();
        user.AppendLine($"Base : {ctx.Database.Name}");
        user.AppendLine("Tables :");
        foreach (var table in ctx.Tables.Take(60))
            user.AppendLine($"- {table.Name}" + (string.IsNullOrWhiteSpace(table.Description) ? "" : " : " + Cut(table.Description, 80)));
        user.AppendLine("Décris à quoi sert cette base (champ description).");
        var response = await ai.CompleteAsync(new AiRequest(System, user.ToString(), TextSchema, ValidateText), ct);
        var text = Clean(response.Json!.Value.GetProperty("description").GetString());
        if (text.Length == 0) return 0;
        var doubt = response.Json.Value.TryGetProperty("uncertain", out var u) && u.ValueKind == JsonValueKind.True;
        var created = new List<DescriptionSuggestionInfo> { New(ctx, "database", ctx.Database.Id, text, doubt, DateTime.UtcNow) };
        await StoreAsync(ctx.Database.Id, created, ct);
        return 1;
    }

    private async Task<int> SuggestRelationsAsync(Context ctx, ISet<string>? only, bool explicitRun, CancellationToken ct)
    {
        var wanted = ctx.Relations.Where(r => string.IsNullOrWhiteSpace(r.Description) && (only == null || only.Contains(r.Id)) &&
                                              (explicitRun || !ctx.Decided.Contains($"relation:{r.Id}")) &&
                                              ctx.ColumnById.TryGetValue(r.FirstId, out var p) && ctx.ColumnById.TryGetValue(r.SecondId, out var c) &&
                                              !p.IsSensitive && !c.IsSensitive).ToList();
        var created = new List<DescriptionSuggestionInfo>();
        foreach (var batch in wanted.Chunk(RelationsPerPrompt))
        {
            var user = new System.Text.StringBuilder();
            user.AppendLine($"Base : {ctx.Database.Name}");
            user.AppendLine("Décris chaque relation en une phrase (ex. « Une commande appartient à un client ») ; reprends son numéro dans index :");
            for (var i = 0; i < batch.Length; i++)
            {
                var parent = ctx.ColumnById[batch[i].FirstId];
                var child = ctx.ColumnById[batch[i].SecondId];
                var parentTable = ctx.TableById[parent.TableId];
                user.AppendLine($"[{i}] {ctx.TableById[child.TableId].Name}.{child.Name} → {parentTable.Name}.{parent.Name}" +
                                (string.IsNullOrWhiteSpace(parentTable.Description) ? "" : $" (« {parentTable.Name} » : {Cut(parentTable.Description, 80)})"));
            }

            var response = await ai.CompleteAsync(new AiRequest(System, user.ToString(), RelationsSchema, ValidateRelations), ct);
            var seen = new HashSet<int>();
            foreach (var item in response.Json!.Value.GetProperty("relations").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out var index) ||
                    index < 0 || index >= batch.Length || !seen.Add(index)) continue;
                var text = Clean(item.TryGetProperty("description", out var d) ? d.GetString() : null);
                if (text.Length > 0) created.Add(New(ctx, "relation", batch[index].Id, text, false, DateTime.UtcNow));
            }
        }

        await StoreAsync(ctx.Database.Id, created, ct);
        return created.Count;
    }

    // ---- À la demande -----------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Propose la description d'un seul élément (bouton « Suggérer ») et la met en file ; elle remplace une proposition en attente.
    ///     Un élément déjà décrit n'est pas proposé : la description saisie n'est jamais remplacée.
    /// </summary>
    public async Task<SuggestionView?> SuggestAsync(string databaseId, string targetType, string? targetId, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var ctx = await LoadAsync(databaseId, ct);
        string id;
        int made;
        switch (targetType)
        {
            case DescriptionSuggestionInfo.TargetDatabase:
                id = databaseId;
                EnsureBlank(ctx.Database.Description, ctx.Database.Name);
                made = await SuggestDatabaseAsync(ctx, true, ct);
                break;
            case DescriptionSuggestionInfo.TargetTable:
                var table = ctx.TableById.GetValueOrDefault(targetId ?? "") ?? throw new GranitExceptions("Cette table n'existe pas dans cette base.");
                id = table.Id;
                EnsureBlank(table.Description, table.Name);
                made = await SuggestTableAsync(ctx, table, true, new HashSet<string>(), true, ct);
                break;
            case DescriptionSuggestionInfo.TargetColumn:
                var column = ctx.ColumnById.GetValueOrDefault(targetId ?? "") ?? throw new GranitExceptions("Cette colonne n'existe pas dans cette base.");
                if (column.IsSensitive) throw new GranitExceptions("Cette colonne est marquée sensible : elle n'est jamais envoyée au modèle.");
                id = column.Id;
                EnsureBlank(column.Description, column.Name);
                made = await SuggestTableAsync(ctx, ctx.TableById[column.TableId], false, new HashSet<string> { column.Id }, true, ct);
                break;
            case DescriptionSuggestionInfo.TargetRelation:
                var relation = ctx.Relations.FirstOrDefault(r => r.Id == targetId) ?? throw new GranitExceptions("Cette relation n'existe pas dans cette base.");
                id = relation.Id;
                EnsureBlank(relation.Description, "Cette relation");
                made = await SuggestRelationsAsync(ctx, new HashSet<string> { relation.Id }, true, ct);
                break;
            default:
                throw new GranitExceptions("TargetType doit valoir database, table, column ou relation.");
        }

        if (made == 0) throw new GranitExceptions("Le modèle n'a rien proposé d'utilisable pour cet élément.");
        return (await ListAsync(databaseId, DescriptionSuggestionInfo.Pending, ct))!
            .First(s => s.TargetType == targetType && s.TargetId == id && s.Kind == DescriptionSuggestionInfo.KindDescription);
    }

    private static void EnsureBlank(string? description, string name)
    {
        if (!string.IsNullOrWhiteSpace(description))
            throw new GranitExceptions($"{name} a déjà une description : l'assistant ne remplace jamais un texte saisi.");
    }

    // ---- Tâche de fond ----------------------------------------------------------------------------------------------------------

    /// <summary>L'état de la dernière documentation en lot de la base, ou <c>null</c> si aucune n'a été lancée depuis le démarrage.</summary>
    public SuggestionJobView? JobOf(string databaseId)
    {
        lock (JobsLock) return Jobs.TryGetValue(databaseId, out var job) ? job.View() : null;
    }

    /// <summary>Demande l'arrêt de la documentation en lot ; les propositions déjà faites restent. <c>false</c> si rien ne tourne.</summary>
    public bool CancelJob(string databaseId)
    {
        lock (JobsLock)
        {
            if (!Jobs.TryGetValue(databaseId, out var job) || job.State != "running") return false;
            job.Cancel.Cancel();
            return true;
        }
    }

    /// <summary>
    ///     Lance en arrière-plan la documentation des éléments sans description d'une base : la base, puis chaque table avec ses colonnes,
    ///     puis les relations. Annulable ; reprenable (un élément déjà proposé, accepté ou refusé n'est pas repris). <c>null</c> si la base n'existe pas.
    /// </summary>
    public async Task<SuggestionJobView?> StartJobAsync(string databaseId, CancellationToken ct = default)
    {
        if (!catalog.Value.AllowAi)
            throw new GranitExceptions("L'assistant d'IA est désactivé (Catalog:AllowAi = false) : il envoie des noms et descriptions du catalogue à un modèle.");
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var ctx = await LoadAsync(databaseId, ct);
        var todo = ctx.Tables.Where(t => t.TableType != "Virtual").ToList();

        Job job;
        lock (JobsLock)
        {
            if (Jobs.TryGetValue(databaseId, out var running) && running.State == "running")
                throw new GranitExceptions("Une documentation est déjà en cours pour cette base.");
            Jobs[databaseId] = job = new Job(databaseId, todo.Count);
        }

        _ = Task.Run(() => RunAsync(job, ctx, todo));
        return job.View();
    }

    private async Task RunAsync(Job job, Context ctx, List<TableInfo> todo)
    {
        var ct = job.Cancel.Token;
        var consecutive = 0;
        try
        {
            var database = await Guard(job, () => SuggestDatabaseAsync(ctx, false, ct), ct, "base");
            if (database > 0) lock (job) job.Suggested += database;
            foreach (var table in todo)
            {
                ct.ThrowIfCancellationRequested();
                var made = await Guard(job, () => SuggestTableAsync(ctx, table, true, null, false, ct), ct, table.Name);
                lock (job)
                {
                    job.Done++;
                    if (made < 0) consecutive++;
                    else
                    {
                        consecutive = 0;
                        job.Suggested += made;
                    }
                }

                // Un modèle injoignable ou désactivé échouerait pour chaque table : on s'arrête plutôt que d'attendre N délais.
                if (consecutive >= 3) throw new GranitExceptions("Trois échecs de suite : arrêt (voir les erreurs ci-dessous).");
            }

            var relations = await Guard(job, () => SuggestRelationsAsync(ctx, null, false, ct), ct, "relations");
            if (relations > 0) lock (job) job.Suggested += relations;
            lock (job) job.State = job.Failed > 0 && job.Suggested == 0 ? "failed" : "done";
        }
        catch (OperationCanceledException)
        {
            lock (job) job.State = "cancelled";
        }
        catch (GranitExceptions e)
        {
            lock (job)
            {
                job.Errors.Add(e.Message);
                job.State = "failed";
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Documentation en lot interrompue");
            lock (job)
            {
                job.Errors.Add("Erreur inattendue (voir le journal du serveur).");
                job.State = "failed";
            }
        }
        finally
        {
            lock (job) job.FinishedAt = DateTime.UtcNow;
        }
    }


    /// <summary>Exécute une étape ; une erreur du modèle est comptée (résultat -1) sans arrêter les autres tables.</summary>
    private static async Task<int> Guard(Job job, Func<Task<int>> step, CancellationToken ct, string? what = null)
    {
        try
        {
            return await step();
        }
        catch (GranitExceptions e)
        {
            ct.ThrowIfCancellationRequested();
            lock (job)
            {
                job.Failed++;
                if (job.Errors.Count < 20) job.Errors.Add(what == null ? e.Message : $"{what} : {e.Message}");
            }

            return -1;
        }
    }

    // ---- Colonnes sensibles (règles) --------------------------------------------------------------------------------------------

    /// <summary>Propose les colonnes dont le nom évoque une donnée sensible, par règles (sans modèle). Remplace les propositions en attente. <c>null</c> si la base n'existe pas.</summary>
    public async Task<int?> DetectSensitiveAsync(string databaseId, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var byDatabase = new[] { new CatalogFilter("DatabaseId", databaseId) };
        var columns = await repository.GetAllAsync<ColumnInfo>(byDatabase, ct);
        var existing = (await repository.GetAllAsync<DescriptionSuggestionInfo>(byDatabase, ct)).Where(s => s.Kind == DescriptionSuggestionInfo.KindSensitive).ToList();
        var decided = existing.Where(s => s.Status != DescriptionSuggestionInfo.Pending).Select(s => s.TargetId).ToHashSet();
        var now = DateTime.UtcNow;
        var created = new List<DescriptionSuggestionInfo>();
        foreach (var column in columns.Where(c => !c.IsSensitive && !decided.Contains(c.Id)))
            if (SensitiveRules.Reason(column.Name) is { } reason)
                created.Add(new DescriptionSuggestionInfo
                {
                    Id = Guid.NewGuid().ToString(), DatabaseId = databaseId, Kind = DescriptionSuggestionInfo.KindSensitive,
                    TargetType = DescriptionSuggestionInfo.TargetColumn, TargetId = column.Id, Text = reason, Model = "règles",
                    Uncertain = false, Status = DescriptionSuggestionInfo.Pending, CreatedAt = now
                });
        await repository.InTransactionAsync(async repo =>
        {
            foreach (var old in existing.Where(s => s.Status == DescriptionSuggestionInfo.Pending)) await repo.DeleteAsync<DescriptionSuggestionInfo>(old.Id, ct);
            foreach (var suggestion in created) await repo.CreateAsync(suggestion, ct);
            return 0;
        }, ct);
        return created.Count;
    }

    // ---- File de relecture ------------------------------------------------------------------------------------------------------

    /// <summary>Les propositions d'une base (<c>status</c> : en attente par défaut), celles dont la cible existe encore, par table puis par nom.</summary>
    public async Task<List<SuggestionView>?> ListAsync(string databaseId, string? status, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var wanted = string.IsNullOrWhiteSpace(status) ? DescriptionSuggestionInfo.Pending : status.Trim().ToLowerInvariant();
        var rows = (await repository.GetAllAsync<DescriptionSuggestionInfo>([new CatalogFilter("DatabaseId", databaseId)], ct)).Where(s => s.Status == wanted).ToList();
        if (rows.Count == 0) return [];
        var ctx = await LoadAsync(databaseId, ct);
        var views = new List<SuggestionView>();
        foreach (var s in rows)
        {
            var label = LabelOf(ctx, s.TargetType, s.TargetId);
            if (label == null) continue;
            views.Add(new SuggestionView(s.Id, s.DatabaseId, s.Kind, s.TargetType, s.TargetId, label, s.Text, s.Model, s.Uncertain, s.Status, s.CreatedAt));
        }

        return views.OrderBy(v => v.TargetType == "database" ? 0 : 1).ThenBy(v => v.TargetLabel, StringComparer.Ordinal).ToList();
    }

    private static string? LabelOf(Context ctx, string type, string id) => type switch
    {
        "database" => ctx.Database.Id == id ? ctx.Database.Name : null,
        "table" => ctx.TableById.GetValueOrDefault(id)?.Name,
        "column" => ctx.ColumnById.TryGetValue(id, out var c) ? $"{ctx.TableById[c.TableId].Name}.{c.Name}" : null,
        "relation" => ctx.Relations.FirstOrDefault(r => r.Id == id) is { } r && ctx.ColumnById.TryGetValue(r.FirstId, out var p) && ctx.ColumnById.TryGetValue(r.SecondId, out var ch)
            ? $"{ctx.TableById[ch.TableId].Name}.{ch.Name} → {ctx.TableById[p.TableId].Name}.{p.Name}" : null,
        _ => null
    };

    private async Task<DescriptionSuggestionInfo?> FindAsync(string databaseId, string id, CancellationToken ct)
    {
        var suggestion = await repository.GetAsync<DescriptionSuggestionInfo>(id, ct);
        return suggestion?.DatabaseId == databaseId ? suggestion : null;
    }

    /// <summary>
    ///     Accepte une proposition (avec le texte retouché si <paramref name="text" /> est donné) : écrit la description, ou marque la colonne
    ///     sensible, avec l'origine dans l'historique. Refusé si l'élément a reçu une description entre-temps. <c>null</c> si la proposition n'existe pas.
    /// </summary>
    public async Task<SuggestionView?> AcceptAsync(string databaseId, string id, string? text, string? actor, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(databaseId, id, ct);
        if (suggestion == null) return null;
        if (suggestion.Status != DescriptionSuggestionInfo.Pending) throw new GranitExceptions("Cette proposition a déjà été traitée.");
        var by = $"{actor ?? "anonyme"} (proposé par {suggestion.Model})";

        if (suggestion.Kind == DescriptionSuggestionInfo.KindSensitive)
        {
            var column = await repository.ModifyAsync<ColumnInfo>(suggestion.TargetId, c => c.IsSensitive = true, ct, by)
                         ?? throw new GranitExceptions("Cette colonne n'existe plus.");
            _ = column;
        }
        else
        {
            var final = Clean(string.IsNullOrWhiteSpace(text) ? suggestion.Text : text);
            if (final.Length == 0) throw new GranitExceptions("Le texte de la description est vide.");
            var written = suggestion.TargetType switch
            {
                "database" => await Write<DatabaseInfo>(suggestion.TargetId, final, by, ct),
                "table" => await Write<TableInfo>(suggestion.TargetId, final, by, ct),
                "column" => await Write<ColumnInfo>(suggestion.TargetId, final, by, ct),
                "relation" => await Write<RelationInfo>(suggestion.TargetId, final, by, ct),
                _ => throw new GranitExceptions("Cible inconnue.")
            };
            if (!written) throw new GranitExceptions("Cet élément a déjà une description (ou n'existe plus) : l'assistant ne la remplace jamais.");
            suggestion.Text = final;
        }

        suggestion.Status = DescriptionSuggestionInfo.Accepted;
        await repository.UpdateAsync(suggestion.Id, suggestion, ct);
        return (await ListAsync(databaseId, DescriptionSuggestionInfo.Accepted, ct))!.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>Écrit la description si, et seulement si, elle est encore vide (relu dans la même transaction que la modification).</summary>
    private async Task<bool> Write<T>(string id, string text, string by, CancellationToken ct) where T : InfoBase
    {
        var wrote = false;
        var result = await repository.ModifyAsync<T>(id, entity =>
        {
            if (!string.IsNullOrWhiteSpace(entity.Description)) return;
            entity.Description = text;
            wrote = true;
        }, ct, by);
        return result != null && wrote;
    }

    /// <summary>Refuse une proposition : la documentation en lot ne la repropose plus (« régénérer » le peut). <c>null</c> si elle n'existe pas.</summary>
    public async Task<bool?> RejectAsync(string databaseId, string id, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(databaseId, id, ct);
        if (suggestion == null) return null;
        if (suggestion.Status == DescriptionSuggestionInfo.Accepted) throw new GranitExceptions("Cette proposition a déjà été acceptée.");
        suggestion.Status = DescriptionSuggestionInfo.Rejected;
        await repository.UpdateAsync(suggestion.Id, suggestion, ct);
        return true;
    }

    /// <summary>Accepte d'un coup les propositions de description en attente qui ne sont pas incertaines ; les autres restent à relire. <c>null</c> si la base n'existe pas.</summary>
    public async Task<AcceptBatchView?> AcceptConfidentAsync(string databaseId, string? actor, CancellationToken ct = default)
    {
        if (await repository.GetAsync<DatabaseInfo>(databaseId, ct) == null) return null;
        var rows = (await repository.GetAllAsync<DescriptionSuggestionInfo>([new CatalogFilter("DatabaseId", databaseId)], ct))
            .Where(s => s.Status == DescriptionSuggestionInfo.Pending && s.Kind == DescriptionSuggestionInfo.KindDescription && !s.Uncertain).ToList();
        int accepted = 0, skipped = 0;
        foreach (var suggestion in rows)
        {
            try
            {
                await AcceptAsync(databaseId, suggestion.Id, null, actor, ct);
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
