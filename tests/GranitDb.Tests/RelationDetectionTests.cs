using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Services;
using Microsoft.Data.Sqlite;

namespace GranitDb.Tests;

/// <summary>Détection assistée des relations : candidats par le nom, vérification par des comptes, file de relecture.</summary>
public sealed class RelationDetectionTests : IDisposable
{
    private readonly TestCatalog _env = new(o => o.AllowProfiling = true);

    public void Dispose() => _env.Dispose();

    private RelationDetectionService Service(bool allowed = true)
    {
        _env.Options.AllowProfiling = allowed;
        return new RelationDetectionService(_env.Repository, _env.Importer, [new SqliteColumnProfiler()],
            Microsoft.Extensions.Options.Options.Create(_env.Options));
    }

    /// <summary>
    ///     Une base sans clé déclarée (sauf <c>lignes</c>) : <c>commandes.client_id</c> et <c>billing_client_id</c> pointent vers
    ///     <c>clients</c> (30 lignes), <c>statut_id</c> vers <c>statuts</c> (3 lignes), <c>produit_id</c> vers rien (orphelines).
    /// </summary>
    private async Task<string> ImportAsync()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, """
            CREATE TABLE clients (id INTEGER PRIMARY KEY, nom TEXT);
            CREATE TABLE statuts (id INTEGER PRIMARY KEY, libelle TEXT);
            CREATE TABLE produits (id INTEGER PRIMARY KEY, libelle TEXT);
            CREATE TABLE commandes (id INTEGER PRIMARY KEY, client_id INTEGER, billing_client_id INTEGER, statut_id INTEGER, produit_id INTEGER);
            CREATE TABLE lignes (id INTEGER PRIMARY KEY, commande_id INTEGER REFERENCES commandes(id));
            CREATE TABLE tickets (id INTEGER PRIMARY KEY, pays_id INTEGER, nom TEXT);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 30)
                INSERT INTO clients SELECT i, 'SECRET-CLIENT-' || i FROM n;
            INSERT INTO statuts VALUES (1, 'nouvelle'), (2, 'payée'), (3, 'expédiée');
            INSERT INTO produits VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'd'), (5, 'e');
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
                INSERT INTO commandes SELECT i, (i % 30) + 1, ((i * 7) % 30) + 1, (i % 3) + 1, 1000 + i FROM n;
            INSERT INTO lignes VALUES (1, 1), (2, 2);
            INSERT INTO tickets VALUES (1, 5, 'x');
            """);
        await using var stream = File.OpenRead(path);
        var imported = await _env.Importer.ImportSqliteAsync(stream, "boutique.db", "Boutique", null, true);
        return imported.Database.Id;
    }

    [Fact]
    public void Column_names_are_cut_into_words_and_reduced_to_the_table_they_point_at()
    {
        Assert.Equal(["client"], RelationCandidates.Stem("client_id"));
        Assert.Equal(["client"], RelationCandidates.Stem("id_client"));
        Assert.Equal(["client"], RelationCandidates.Stem("clientId"));
        Assert.Equal(["client"], RelationCandidates.Stem("ClientID"));
        Assert.Equal(["client"], RelationCandidates.Stem("fk_client"));
        Assert.Equal(["client"], RelationCandidates.Stem("client_code"));
        Assert.Equal(["billing", "client"], RelationCandidates.Stem("billing_client_id"));
        Assert.Empty(RelationCandidates.Stem("id"));
        Assert.Empty(RelationCandidates.Stem("nom"));
        Assert.Empty(RelationCandidates.Stem("client_nom"));
    }

    [Fact]
    public void Table_names_lose_their_technical_prefix_and_their_plural()
    {
        Assert.Equal(["client"], RelationCandidates.TableWords("clients"));
        Assert.Equal(["client"], RelationCandidates.TableWords("tbl_client"));
        Assert.Equal(["category"], RelationCandidates.TableWords("categories"));
        Assert.Equal(["address"], RelationCandidates.TableWords("addresses"));
        Assert.Equal(["order", "line"], RelationCandidates.TableWords("sales.OrderLines"));
        Assert.Equal(["status"], RelationCandidates.TableWords("status"));
    }

    [Fact]
    public void Types_are_grouped_in_families_and_an_unknown_type_goes_with_anything()
    {
        Assert.Equal(RelationCandidates.TypeFamily("INTEGER"), RelationCandidates.TypeFamily("bigint"));
        Assert.Equal(RelationCandidates.TypeFamily("varchar(20)"), RelationCandidates.TypeFamily("TEXT"));
        Assert.NotEqual(RelationCandidates.TypeFamily("INTEGER"), RelationCandidates.TypeFamily("TEXT"));
        Assert.Equal("", RelationCandidates.TypeFamily(""));
        Assert.Equal("guid", RelationCandidates.TypeFamily("uniqueidentifier"));
    }

    [Fact]
    public void The_inclusion_score_drops_a_relation_with_too_many_orphans()
    {
        Assert.Equal(30, RelationDetectionService.InclusionScore(100, 0));
        Assert.Equal(20, RelationDetectionService.InclusionScore(1000, 5));
        Assert.Equal(10, RelationDetectionService.InclusionScore(100, 4));
        Assert.Null(RelationDetectionService.InclusionScore(100, 20));
        Assert.Null(RelationDetectionService.InclusionScore(0, 0));
    }

    [Fact]
    public async Task Detection_proposes_the_undeclared_relations_whose_values_exist_in_the_target_and_nothing_else()
    {
        var database = await ImportAsync();

        var run = (await Service().DetectAsync(database))!;
        var list = (await Service().ListAsync(database, null))!;

        Assert.Equal(4, run.Candidates); // client_id, billing_client_id, statut_id, produit_id ; lignes.commande_id est déclarée
        Assert.Equal(3, run.Suggested);
        Assert.Equal(["billing_client_id", "client_id", "statut_id"], list.Select(s => s.ChildColumn).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        var byColumn = list.ToDictionary(s => s.ChildColumn);
        Assert.Equal(("clients", "id", 95, "probable"), (byColumn["client_id"].ParentTable, byColumn["client_id"].ParentColumn, byColumn["client_id"].Score, byColumn["client_id"].Level));
        Assert.Equal(("probable", 0L, 40L), (byColumn["billing_client_id"].Level, byColumn["billing_client_id"].OrphanCount, byColumn["billing_client_id"].NonNullCount));
        // Trois lignes seulement dans la table cible : l'inclusion prouve peu, la suggestion descend d'un cran.
        Assert.Equal(("possible", 70), (byColumn["statut_id"].Level, byColumn["statut_id"].Score));
        Assert.Contains(byColumn["statut_id"].Reasons, r => r.Contains("3 lignes"));
        Assert.Contains(byColumn["client_id"].Reasons, r => r.Contains("100 %") && r.Contains("`clients.id`"));
        Assert.Equal(list.OrderByDescending(s => s.Score).Select(s => s.Id), list.Select(s => s.Id));
    }

    /// <summary>
    ///     <c>machines.code_fab</c> et <c>machines.sous_traitant</c> visent <c>fabricants.code_fab</c> sans que le nom de la table le dise ;
    ///     <c>serie</c> (valeurs absentes) et <c>actif</c> (deux valeurs) ne visent rien.
    /// </summary>
    private async Task<string> ImportByValuesAsync()
    {
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, """
            CREATE TABLE fabricants (code_fab TEXT PRIMARY KEY, nom TEXT);
            CREATE TABLE machines (id INTEGER PRIMARY KEY, modele TEXT, code_fab TEXT, sous_traitant TEXT, serie TEXT, actif INTEGER);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 30)
                INSERT INTO fabricants SELECT 'F' || i, 'SECRET-FAB-' || i FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 40)
                INSERT INTO machines SELECT i, 'm' || i, 'F' || ((i % 30) + 1), 'F' || (((i * 7) % 30) + 1), 'S' || i, i % 2 FROM n;
            """);
        await using var stream = File.OpenRead(path);
        return (await _env.Importer.ImportSqliteAsync(stream, "usine.db", "Usine", null, true)).Database.Id;
    }

    [Fact]
    public async Task Names_alone_miss_a_column_whose_name_does_not_say_the_table_but_the_values_find_it()
    {
        var database = await ImportByValuesAsync();

        var byName = (await Service().DetectAsync(database))!;
        Assert.Equal(0, byName.Candidates);
        Assert.Empty((await Service().ListAsync(database, null))!);

        var run = (await Service().DetectAsync(database, byValues: true))!;
        var list = (await Service().ListAsync(database, null))!.ToDictionary(s => s.ChildColumn);

        Assert.True(run.Candidates > 0);
        Assert.Equal(["code_fab", "sous_traitant"], list.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(("fabricants", "code_fab", "probable"), (list["code_fab"].ParentTable, list["code_fab"].ParentColumn, list["code_fab"].Level));
        Assert.Contains(list["code_fab"].Reasons, r => r.Contains("même nom"));
        // Aucun rapport de nom : seule l'inclusion des valeurs parle, la suggestion reste « possible ».
        Assert.Equal("possible", list["sous_traitant"].Level);
        Assert.Contains(list["sous_traitant"].Reasons, r => r.Contains("Aucun rapport entre les noms"));
        Assert.Contains(list["sous_traitant"].Reasons, r => r.Contains("100 %"));
        // Des comptes seulement : aucune valeur de la source dans le résultat.
        Assert.DoesNotContain("SECRET-FAB", System.Text.Json.JsonSerializer.Serialize(list.Values));
    }

    [Fact]
    public async Task The_value_search_is_repeatable_and_keeps_a_refusal()
    {
        var database = await ImportByValuesAsync();
        var service = Service();
        await service.DetectAsync(database, byValues: true);
        var refused = (await service.ListAsync(database, null))!.First(s => s.ChildColumn == "sous_traitant");
        await service.RejectAsync(database, refused.Id);

        await service.DetectAsync(database, byValues: true);

        Assert.Equal(["code_fab"], (await service.ListAsync(database, null))!.Select(s => s.ChildColumn).ToArray());
    }

    [Fact]
    public void Column_names_are_compared_without_case_accents_or_separators()
    {
        Assert.Equal(RelationCandidates.Key("Code_Client"), RelationCandidates.Key("codeClient"));
        Assert.Equal("codeclient", RelationCandidates.Key("Code client"));
        Assert.Equal("numeroequipe", RelationCandidates.Key("Numéro-équipe"));
    }

    [Fact]
    public async Task A_role_prefix_is_recognised_but_scores_below_an_exact_name()
    {
        var database = await ImportAsync();
        await Service().DetectAsync(database);

        var list = (await Service().ListAsync(database, null))!.ToDictionary(s => s.ChildColumn);

        Assert.Equal(95 - 12, list["billing_client_id"].Score);
        Assert.Contains(list["billing_client_id"].Reasons, r => r.Contains("rôle : billing"));
    }

    [Fact]
    public async Task Suggestions_carry_counts_and_reasons_but_never_a_value_of_the_source()
    {
        var database = await ImportAsync();
        await Service().DetectAsync(database);

        var everything = JsonSerializer.Serialize(await Service().ListAsync(database, null)) +
                         JsonSerializer.Serialize(await _env.Repository.GetAllAsync<RelationSuggestionInfo>());

        Assert.DoesNotContain("SECRET-CLIENT", everything);
        Assert.DoesNotContain("expédiée", everything);
    }

    [Fact]
    public async Task Accepting_creates_a_manual_relation_marked_detected_with_the_referenced_column_first()
    {
        var database = await ImportAsync();
        var service = Service();
        await service.DetectAsync(database);
        var suggestion = (await service.ListAsync(database, null))!.Single(s => s.ChildColumn == "client_id");

        var relation = (await service.AcceptAsync(database, suggestion.Id))!;

        Assert.Equal(RelationDetectionService.DetectedMarker, relation.MetaInfo);
        Assert.Equal(suggestion.ParentColumnId, relation.FirstId);
        Assert.Equal(suggestion.ChildColumnId, relation.SecondId);
        Assert.Contains(await _env.Repository.GetRelationsOfDatabaseAsync(database), r => r.Id == relation.Id);
        Assert.DoesNotContain(await service.ListAsync(database, null), s => s.Id == suggestion.Id);
        Assert.Contains(await service.ListAsync(database, "accepted"), s => s.Id == suggestion.Id);
        await Assert.ThrowsAsync<GranitExceptions>(() => service.AcceptAsync(database, suggestion.Id));

        // La relation existe : une nouvelle détection ne la repropose pas, et une resynchronisation la conserve.
        var again = (await service.DetectAsync(database))!;
        Assert.Equal(2, again.Suggested);
        await _env.Importer.ResyncAsync(database);
        Assert.Contains(await _env.Repository.GetRelationsOfDatabaseAsync(database), r => r.MetaInfo == RelationDetectionService.DetectedMarker);
    }

    [Fact]
    public async Task A_rejected_suggestion_is_remembered_and_not_proposed_again()
    {
        var database = await ImportAsync();
        var service = Service();
        await service.DetectAsync(database);
        var suggestion = (await service.ListAsync(database, null))!.Single(s => s.ChildColumn == "statut_id");

        var rejected = (await service.RejectAsync(database, suggestion.Id))!;
        var run = (await service.DetectAsync(database))!;

        Assert.Equal("rejected", rejected.Status);
        Assert.Equal(2, run.Suggested);
        Assert.DoesNotContain(await service.ListAsync(database, null), s => s.ChildColumn == "statut_id");
        Assert.Single(await service.ListAsync(database, "rejected"));
    }

    [Fact]
    public async Task Accepting_in_batch_takes_only_the_suggestions_above_the_threshold()
    {
        var database = await ImportAsync();
        var service = Service();
        await service.DetectAsync(database);

        var batch = (await service.AcceptAboveAsync(database, 75))!;

        Assert.Equal(new AcceptBatchView(2, 0), batch);
        var left = (await service.ListAsync(database, null))!;
        Assert.Equal(["statut_id"], left.Select(s => s.ChildColumn).ToArray());
    }

    [Fact]
    public async Task Deleting_a_column_or_a_database_removes_its_suggestions()
    {
        var database = await ImportAsync();
        var service = Service();
        await service.DetectAsync(database);
        var suggestion = (await service.ListAsync(database, null))!.First(s => s.ChildColumn == "statut_id");

        await _env.Repository.DeleteAsync<ColumnInfo>(suggestion.ChildColumnId);
        Assert.DoesNotContain(await _env.Repository.GetAllAsync<RelationSuggestionInfo>(), s => s.Id == suggestion.Id);

        await _env.Repository.DeleteAsync<DatabaseInfo>(database);
        Assert.Empty(await _env.Repository.GetAllAsync<RelationSuggestionInfo>());
    }

    [Fact]
    public async Task Detection_is_refused_when_profiling_is_disabled_and_unknown_things_are_missing()
    {
        var database = await ImportAsync();

        var disabled = await Assert.ThrowsAsync<GranitExceptions>(() => Service(false).DetectAsync(database));
        var service = Service();

        Assert.Contains("Catalog:AllowProfiling", disabled.Message);
        Assert.Null(await service.DetectAsync("absente"));
        Assert.Null(await service.ListAsync("absente", null));
        Assert.Null(await service.AcceptAsync(database, "absente"));
        Assert.Null(await service.RejectAsync(database, "absente"));
    }

    [Fact]
    public async Task The_inclusion_check_returns_only_counts()
    {
        var database = await ImportAsync();
        var source = (await _env.Repository.GetAsync<DatabaseInfo>(database))!.SourcePath!;

        var result = await new SqliteColumnProfiler().CheckInclusionAsync(source, new InclusionCheck("commandes", "produit_id", "produits", "id"), 30);

        Assert.Equal(new InclusionResult(40, 40, 5), result);
    }
}

/// <summary>Routes de la détection : détecter est réservé aux administrateurs et désactivé par défaut.</summary>
public sealed class RelationDetectionApiTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    private async Task<(KestrelApiFactory Factory, HttpClient Http, string Database)> StartAsync(bool allow)
    {
        var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:Provider"] = "Sqlite",
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api-" + Guid.NewGuid().ToString("N")),
            ["Catalog:AllowProfiling"] = allow ? "true" : "false"
        });
        var http = factory.CreateKestrelClient();
        var path = _env.NewFile($"{Guid.NewGuid():N}.db");
        TestCatalog.Execute(path, """
            CREATE TABLE clients (id INTEGER PRIMARY KEY);
            CREATE TABLE commandes (id INTEGER PRIMARY KEY, client_id INTEGER);
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 25) INSERT INTO clients SELECT i FROM n;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 25) INSERT INTO commandes SELECT i, i FROM n;
            """);
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", "a.db");
        var imported = await (await http.PostAsync("/api/v1/import/sqlite", content)).Content.ReadFromJsonAsync<JsonElement>();
        return (factory, http, imported.GetProperty("database").GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task Relations_are_detected_listed_accepted_and_rejected_through_the_api()
    {
        var (factory, http, database) = await StartAsync(true);
        using var _ = factory;
        using var __ = http;
        Assert.Empty((await http.GetFromJsonAsync<JsonElement>($"/api/v1/DatabaseInfo/{database}/relation-suggestions")).EnumerateArray());

        var detect = await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/detect", null);
        Assert.Equal(HttpStatusCode.OK, detect.StatusCode);
        Assert.Equal(1, (await detect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("suggested").GetInt32());

        var list = await http.GetFromJsonAsync<JsonElement>($"/api/v1/DatabaseInfo/{database}/relation-suggestions");
        var suggestion = list[0];
        Assert.Equal("commandes", suggestion.GetProperty("childTable").GetString());
        Assert.Equal("probable", suggestion.GetProperty("level").GetString());
        var id = suggestion.GetProperty("id").GetString();

        var accepted = await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/{id}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("detected", (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("metaInfo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/absente/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/v1/DatabaseInfo/absente/relation-suggestions/detect", null)).StatusCode);
        var batch = await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/accept?minScore=50", null);
        Assert.Equal(0, (await batch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
    }

    [Fact]
    public async Task Detection_answers_400_with_the_reason_when_profiling_is_disabled()
    {
        var (factory, http, database) = await StartAsync(false);
        using var _ = factory;
        using var __ = http;

        var response = await http.PostAsync($"/api/v1/DatabaseInfo/{database}/relation-suggestions/detect", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AllowProfiling", await response.Content.ReadAsStringAsync());
        SqliteConnection.ClearAllPools();
    }
}
