using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Options;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GranitDb.Tests;

/// <summary>Faux modèle : lit le prompt reçu et rédige une réponse JSON conforme, comme le ferait un petit modèle docile.</summary>
internal sealed class ScriptedAiServer : HttpMessageHandler
{
    public List<string> Prompts { get; } = [];
    public TimeSpan Delay { get; set; }
    public bool Fail { get; set; }
    public bool Invent { get; set; } = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
        var user = body.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
        lock (Prompts) Prompts.Add(user);
        if (Fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Answer(user) } } }, usage = new { prompt_tokens = 1, completion_tokens = 1 } }),
                Encoding.UTF8, "application/json")
        };
    }

    private string Answer(string user)
    {
        if (user.Contains("Décris à quoi sert cette base")) return """{"description":"La boutique en ligne.","uncertain":false}""";
        if (user.Contains("reprends son numéro dans index"))
        {
            var indexes = Regex.Matches(user, @"^\[(\d+)\]", RegexOptions.Multiline).Select(m => int.Parse(m.Groups[1].Value));
            return JsonSerializer.Serialize(new { relations = indexes.Select(i => new { index = i, description = "Une commande appartient à un client." }) });
        }

        var table = user.Contains("Décris la table (champ table)") ? "Table de test décrite par le modèle." : "";
        var section = user.Contains("Décris ces colonnes") ? user[user.IndexOf("Décris ces colonnes", StringComparison.Ordinal)..] : "";
        var names = Regex.Matches(section, @"^- (\S+) \(", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var columns = names.Select(n => new { name = n, description = $"« {n} » décrit par le modèle.", uncertain = false }).ToList();
        if (Invent && names.Count > 0) columns.Add(new { name = "colonne_fantome", description = "Inventée.", uncertain = false });
        return JsonSerializer.Serialize(new { table, columns });
    }
}

public sealed class DescriptionAssistantTests : IDisposable
{
    private readonly TestCatalog _env = new(o => o.AllowAi = true);
    private readonly ScriptedAiServer _server = new();

    public void Dispose() => _env.Dispose();

    private DescriptionAssistant Assistant(ScriptedAiServer? server = null, bool allowAi = true)
    {
        _env.Options.AllowAi = allowAi;
        var provider = new OpenAiCompatibleProvider(Microsoft.Extensions.Options.Options.Create(_env.Options),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()), NullLogger<OpenAiCompatibleProvider>.Instance, server ?? _server);
        return new DescriptionAssistant(_env.Repository, provider, Microsoft.Extensions.Options.Options.Create(_env.Options),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()), NullLogger<DescriptionAssistant>.Instance);
    }

    /// <summary>
    ///     <c>clients</c> (décrite à la main, avec <c>email</c> sensible), <c>commandes</c> (une relation déclarée, une colonne cryptique
    ///     <c>ZX_T01</c>), et un secret dans les données : il ne doit jamais partir.
    /// </summary>
    private async Task<(string Database, Dictionary<string, TableInfo> Tables)> ImportAsync(bool markEmailSensitive = true)
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, """
            CREATE TABLE clients (id INTEGER PRIMARY KEY, nom TEXT, email TEXT, pays TEXT);
            CREATE TABLE commandes (id INTEGER PRIMARY KEY, client_id INTEGER REFERENCES clients(id), total REAL, ZX_T01 TEXT);
            INSERT INTO clients VALUES (1, 'VALEUR-SECRETE-NOM', 'secret@example.com', 'FR');
            INSERT INTO commandes VALUES (1, 1, 10.5, 'VALEUR-SECRETE-CODE');
            """);
        await using var stream = File.OpenRead(path);
        var imported = await _env.Importer.ImportSqliteAsync(stream, "boutique.db", "Boutique", null, true);
        var database = imported.Database.Id;
        var tables = (await _env.Repository.GetAllAsync<TableInfo>([new GranitDB.API.Models.CatalogFilter("DatabaseId", database)])).ToDictionary(t => t.Name);
        await _env.Repository.ModifyAsync<TableInfo>(tables["clients"].Id, t => t.Description = "Les clients de la boutique (saisi à la main).");
        if (markEmailSensitive)
        {
            var email = (await _env.Repository.GetAllAsync<ColumnInfo>([new GranitDB.API.Models.CatalogFilter("TableId", tables["clients"].Id)])).Single(c => c.Name == "email");
            await _env.Repository.ModifyAsync<ColumnInfo>(email.Id, c => c.IsSensitive = true);
        }

        return (database, tables);
    }

    private static async Task<SuggestionJobView> WaitAsync(DescriptionAssistant assistant, string database)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = assistant.JobOf(database)!;
            if (job.State != "running") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException("la documentation en lot ne se termine pas");
    }

    [Fact]
    public async Task The_batch_proposes_only_missing_descriptions_and_ignores_columns_the_model_invented()
    {
        var (database, _) = await ImportAsync();
        var assistant = Assistant();

        await assistant.StartJobAsync(database);
        var job = await WaitAsync(assistant, database);
        var list = (await assistant.ListAsync(database, null))!;

        Assert.Equal("done", job.State);
        var labels = list.Select(s => s.TargetLabel).ToList();
        Assert.Contains("Boutique", labels); // la base
        Assert.Contains("commandes", labels); // la table sans description
        Assert.DoesNotContain("clients", labels); // décrite à la main : jamais proposée
        Assert.Contains("commandes.total", labels);
        Assert.Contains("clients.nom", labels);
        Assert.DoesNotContain("clients.email", labels); // sensible : jamais envoyée ni proposée
        Assert.DoesNotContain(labels, l => l.Contains("fantome"));
        Assert.Contains(labels, l => l.Contains("commandes.client_id → clients.id")); // la relation
        Assert.All(list, s => Assert.Equal("qwen3:4b-instruct", s.Model));
        Assert.Equal(list.Count, job.Suggested);
    }

    [Fact]
    public async Task A_cryptic_name_is_marked_uncertain_whatever_the_model_says()
    {
        var (database, _) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);

        var list = (await assistant.ListAsync(database, null))!.ToDictionary(s => s.TargetLabel);

        Assert.True(list["commandes.ZX_T01"].Uncertain);
        Assert.False(list["commandes.total"].Uncertain);
    }

    [Theory]
    [InlineData("ZX_T01", true)]
    [InlineData("cdcl", true)]
    [InlineData("t1", true)]
    [InlineData("A1", true)]
    [InlineData("customer_email", false)]
    [InlineData("client_id", false)]
    [InlineData("ClientId", false)]
    [InlineData("total", false)]
    [InlineData("created_at", false)]
    public void Cryptic_names_are_recognised(string name, bool cryptic) => Assert.Equal(cryptic, DescriptionAssistant.IsCryptic(name));

    [Fact]
    public async Task The_model_never_receives_a_sensitive_column_nor_a_value_of_the_source()
    {
        var (database, _) = await ImportAsync();
        var assistant = Assistant();

        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);

        var everything = string.Join("\n", _server.Prompts);
        Assert.NotEmpty(_server.Prompts);
        Assert.DoesNotContain("email", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VALEUR-SECRETE", everything);
        Assert.DoesNotContain("secret@example.com", everything);
        Assert.Contains("Colonnes de la table : id, nom, pays", everything); // la table décrite à la main sert de contexte
    }

    [Fact]
    public async Task Prompts_give_the_style_of_existing_descriptions_the_relations_and_the_key_of_each_column()
    {
        var (database, _) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);

        var forOrders = _server.Prompts.Single(p => p.Contains("Table : commandes"));

        Assert.Contains("Exemples de style", forOrders);
        Assert.Contains("clients : Les clients de la boutique", forOrders);
        Assert.Contains("commandes.client_id → clients.id", forOrders);
        Assert.Contains("id (INTEGER, clé primaire", forOrders);
    }

    [Fact]
    public async Task Columns_go_to_the_model_by_prompts_of_fifteen()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE large (" + string.Join(", ", Enumerable.Range(1, 40).Select(i => $"colonne_{i} TEXT")) + ");");
        await using var stream = File.OpenRead(path);
        var database = (await _env.Importer.ImportSqliteAsync(stream, "large.db", "Large", null, true)).Database.Id;
        var assistant = Assistant();

        await assistant.StartJobAsync(database);
        var job = await WaitAsync(assistant, database);

        Assert.Equal(3, _server.Prompts.Count(p => p.Contains("Table : large"))); // 15 + 15 + 10
        Assert.Equal(1 + 1 + 40, (await assistant.ListAsync(database, null))!.Count); // base + table + colonnes
        Assert.Equal("done", job.State);
    }

    [Fact]
    public async Task Accepting_writes_the_description_with_its_origin_in_the_history_and_never_twice()
    {
        var (database, tables) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);
        var suggestion = (await assistant.ListAsync(database, null))!.Single(s => s.TargetLabel == "commandes");

        var accepted = (await assistant.AcceptAsync(database, suggestion.Id, "Les commandes passées (retouché).", "alice"))!;

        Assert.Equal("accepted", accepted.Status);
        Assert.Equal("Les commandes passées (retouché).", (await _env.Repository.GetAsync<TableInfo>(tables["commandes"].Id))!.Description);
        var history = (await _env.Repository.GetHistoryAsync(tables["commandes"].Id, 0, 10)).Single(h => h.Field == "Description");
        Assert.Equal("alice (proposé par qwen3:4b-instruct)", history.ChangedBy);
        await Assert.ThrowsAsync<GranitExceptions>(() => assistant.AcceptAsync(database, suggestion.Id, null, "alice"));
        Assert.DoesNotContain(await assistant.ListAsync(database, null), s => s.Id == suggestion.Id);
    }

    [Fact]
    public async Task A_description_typed_meanwhile_is_never_overwritten_by_an_accepted_suggestion()
    {
        var (database, tables) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);
        var suggestion = (await assistant.ListAsync(database, null))!.Single(s => s.TargetLabel == "commandes");
        await _env.Repository.ModifyAsync<TableInfo>(tables["commandes"].Id, t => t.Description = "Saisi par une personne.");

        var error = await Assert.ThrowsAsync<GranitExceptions>(() => assistant.AcceptAsync(database, suggestion.Id, null, "alice"));

        Assert.Contains("déjà une description", error.Message);
        Assert.Equal("Saisi par une personne.", (await _env.Repository.GetAsync<TableInfo>(tables["commandes"].Id))!.Description);
    }

    [Fact]
    public async Task A_rejected_suggestion_is_not_proposed_again_by_the_batch_but_can_be_regenerated_on_demand()
    {
        var (database, tables) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);
        var suggestion = (await assistant.ListAsync(database, null))!.Single(s => s.TargetLabel == "commandes");
        await assistant.RejectAsync(database, suggestion.Id);
        _server.Prompts.Clear();

        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);

        Assert.DoesNotContain(await assistant.ListAsync(database, null), s => s.TargetLabel == "commandes");
        Assert.Single(await assistant.ListAsync(database, "rejected"));
        Assert.Empty(_server.Prompts); // tout était déjà proposé ou refusé : plus rien à demander
        var again = (await assistant.SuggestAsync(database, "table", tables["commandes"].Id))!;
        Assert.Equal("pending", again.Status);
    }

    [Fact]
    public async Task One_element_is_suggested_on_demand_but_never_when_it_already_has_a_description()
    {
        var (database, tables) = await ImportAsync();
        var assistant = Assistant();

        var column = (await _env.Repository.GetAllAsync<ColumnInfo>([new GranitDB.API.Models.CatalogFilter("TableId", tables["commandes"].Id)])).Single(c => c.Name == "total");
        var one = (await assistant.SuggestAsync(database, "column", column.Id))!;
        var described = await Assert.ThrowsAsync<GranitExceptions>(() => assistant.SuggestAsync(database, "table", tables["clients"].Id));
        var sensitive = (await _env.Repository.GetAllAsync<ColumnInfo>([new GranitDB.API.Models.CatalogFilter("TableId", tables["clients"].Id)])).Single(c => c.Name == "email");
        var refused = await Assert.ThrowsAsync<GranitExceptions>(() => assistant.SuggestAsync(database, "column", sensitive.Id));

        Assert.Equal("commandes.total", one.TargetLabel);
        Assert.Contains("déjà une description", described.Message);
        Assert.Contains("sensible", refused.Message);
        Assert.Single(_server.Prompts); // un seul appel : rien n'est parti pour les refus
        await Assert.ThrowsAsync<GranitExceptions>(() => assistant.SuggestAsync(database, "inconnu", null));
        Assert.Null(await assistant.SuggestAsync("absente", "table", null));
    }

    [Fact]
    public async Task Accepting_all_the_confident_ones_leaves_the_uncertain_ones_for_review()
    {
        var (database, _) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);

        var batch = (await assistant.AcceptConfidentAsync(database, "alice"))!;

        var left = (await assistant.ListAsync(database, null))!;
        Assert.Equal(0, batch.Skipped);
        Assert.True(batch.Accepted > 0);
        Assert.All(left, s => Assert.True(s.Uncertain));
        Assert.Contains(left, s => s.TargetLabel == "commandes.ZX_T01");
    }

    [Fact]
    public async Task Sensitive_columns_are_proposed_by_name_rules_without_any_model_and_accepting_marks_them()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE personnes (id INTEGER PRIMARY KEY, user_email TEXT, iban TEXT, date_naissance TEXT, pays TEXT, mdp TEXT);");
        await using var stream = File.OpenRead(path);
        var database = (await _env.Importer.ImportSqliteAsync(stream, "p.db", "Personnes", null, true)).Database.Id;
        var assistant = Assistant(allowAi: false);

        var found = await assistant.DetectSensitiveAsync(database);
        var list = (await assistant.ListAsync(database, null))!;

        Assert.Equal(4, found);
        Assert.Equal(["personnes.date_naissance", "personnes.iban", "personnes.mdp", "personnes.user_email"], list.Select(s => s.TargetLabel).OrderBy(l => l, StringComparer.Ordinal).ToArray());
        Assert.All(list, s => Assert.Equal(("sensitive", "règles"), (s.Kind, s.Model)));
        Assert.Empty(_server.Prompts);

        var iban = list.Single(s => s.TargetLabel == "personnes.iban");
        await assistant.AcceptAsync(database, iban.Id, null, "alice");
        var column = (await _env.Repository.GetAsync<ColumnInfo>(iban.TargetId))!;
        Assert.True(column.IsSensitive);
        Assert.Contains(await _env.Repository.GetHistoryAsync(column.Id, 0, 10), h => h.ChangedBy == "alice (proposé par règles)");

        await assistant.RejectAsync(database, list.Single(s => s.TargetLabel == "personnes.mdp").Id);
        Assert.Equal(2, await assistant.DetectSensitiveAsync(database)); // l'acceptée et la refusée ne reviennent pas
    }

    [Theory]
    [InlineData("email", true)]
    [InlineData("customer_email", true)]
    [InlineData("userEmail", true)]
    [InlineData("firstname", true)]
    [InlineData("prenom", true)]
    [InlineData("password_hash", true)]
    [InlineData("numero_telephone", true)]
    [InlineData("phone", true)]
    [InlineData("status", false)]
    [InlineData("total", false)]
    [InlineData("camera", false)]
    public void Sensitive_names_are_matched_by_whole_words(string name, bool sensitive) =>
        Assert.Equal(sensitive, SensitiveRules.Reason(name) != null);

    [Fact]
    public async Task The_assistant_refuses_to_start_when_disabled_and_one_job_runs_at_a_time_and_can_be_cancelled()
    {
        var (database, _) = await ImportAsync();
        await Assert.ThrowsAsync<GranitExceptions>(() => Assistant(allowAi: false).StartJobAsync(database));

        var slow = new ScriptedAiServer { Delay = TimeSpan.FromMilliseconds(400) };
        var assistant = Assistant(slow);
        await assistant.StartJobAsync(database);

        var second = await Assert.ThrowsAsync<GranitExceptions>(() => assistant.StartJobAsync(database));
        Assert.Contains("déjà en cours", second.Message);
        Assert.True(assistant.CancelJob(database));
        var job = await WaitAsync(assistant, database);

        Assert.Equal("cancelled", job.State);
        Assert.NotNull(job.FinishedAt);
        Assert.False(assistant.CancelJob(database));
        Assert.Null(await assistant.StartJobAsync("absente"));
        Assert.Null(assistant.JobOf("jamais-lance"));
    }

    [Fact]
    public async Task A_model_that_fails_stops_the_batch_with_the_reasons_instead_of_waiting_for_every_table()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, string.Join(" ", Enumerable.Range(1, 8).Select(i => $"CREATE TABLE t{i} (id INTEGER PRIMARY KEY, nom TEXT);")));
        await using var stream = File.OpenRead(path);
        var database = (await _env.Importer.ImportSqliteAsync(stream, "f.db", "Pannes", null, true)).Database.Id;
        var broken = new ScriptedAiServer { Fail = true };
        var assistant = Assistant(broken);

        await assistant.StartJobAsync(database);
        var job = await WaitAsync(assistant, database);

        Assert.Equal("failed", job.State);
        Assert.Equal(0, job.Suggested);
        Assert.NotEmpty(job.Errors);
        Assert.True(broken.Prompts.Count < 8, "le lot aurait dû s'arrêter après trois échecs de suite");
    }

    [Fact]
    public async Task Deleting_a_table_or_a_database_removes_its_suggestions()
    {
        var (database, tables) = await ImportAsync();
        var assistant = Assistant();
        await assistant.StartJobAsync(database);
        await WaitAsync(assistant, database);
        Assert.Contains(await _env.Repository.GetAllAsync<DescriptionSuggestionInfo>(), s => s.TargetType == "column");

        await _env.Repository.DeleteAsync<TableInfo>(tables["commandes"].Id);
        var left = await _env.Repository.GetAllAsync<DescriptionSuggestionInfo>();
        Assert.DoesNotContain(left, s => s.TargetId == tables["commandes"].Id);
        Assert.DoesNotContain(await assistant.ListAsync(database, null), s => s.TargetLabel.StartsWith("commandes"));

        await _env.Repository.DeleteAsync<DatabaseInfo>(database);
        Assert.Empty(await _env.Repository.GetAllAsync<DescriptionSuggestionInfo>());
    }
}

/// <summary>Routes de l'assistant : désactivé par défaut, détection des colonnes sensibles sans modèle.</summary>
public sealed class DescriptionSuggestionsApiTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Generation_answers_400_when_disabled_while_sensitive_detection_and_the_queue_work_without_a_model()
    {
        using var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-" + Guid.NewGuid().ToString("N")),
            ["Catalog:AllowAi"] = "false"
        });
        using var http = factory.CreateKestrelClient();
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, "CREATE TABLE personnes (id INTEGER PRIMARY KEY, email TEXT, pays TEXT);");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "p.db");
        var database = (await (await http.PostAsync("/api/v1/import/sqlite", content)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("database").GetProperty("id").GetString();

        var generate = await http.PostAsync($"/api/v1/DescriptionSuggestions/database/{database}/generate", null);
        var detect = await http.PostAsync($"/api/v1/DescriptionSuggestions/database/{database}/detect-sensitive", null);
        var list = await http.GetFromJsonAsync<JsonElement>($"/api/v1/DescriptionSuggestions/database/{database}");

        Assert.Equal(HttpStatusCode.BadRequest, generate.StatusCode);
        Assert.Contains("AllowAi", await generate.Content.ReadAsStringAsync());
        Assert.Equal(1, (await detect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        Assert.Equal("personnes.email", list[0].GetProperty("targetLabel").GetString());
        var id = list[0].GetProperty("id").GetString();

        var accepted = await http.PostAsJsonAsync($"/api/v1/DescriptionSuggestions/database/{database}/{id}/accept", new { });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/DescriptionSuggestions/database/{database}/job")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/DescriptionSuggestions/database/absente")).StatusCode);
        SqliteConnection.ClearAllPools();
    }
}
