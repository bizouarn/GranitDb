using System.Net;
using Bunit;
using GranitDb.Client.Components;
using GranitDb.Client.Components.Dialogs;
using GranitDb.Client.Layout;
using GranitDb.Client.Models;
using GranitDb.Client.Workspace;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace GranitDb.Tests;

/// <summary>
///     Jalon 11, côté interface : grille de saisie, fenêtres « Nouvelle table », « Colonnes » et « Étiquette », domaines (tables
///     liées, relations indirectes), liste des tables du diagramme dans la barre latérale.
/// </summary>
public class ClientInterfaceMilestoneTests : ShellTestBase
{
    private IDialogService DialogService => Ctx.Services.GetRequiredService<IDialogService>();

    private async Task<IDialogReference> OpenAsync<T>(string title, DialogParameters<T> parameters, MaxWidth width = MaxWidth.Large)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        await Dialogs!.InvokeAsync(() => DialogService.ShowAsync<T>(title, parameters, DialogHelper.Options(width)));

    private static TableInfo[] Boutique() => System.Text.Json.JsonSerializer.Deserialize<TableInfo[]>(BoutiqueTables,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    // --- Grille de saisie commune ---

    [BunitFact]
    public async Task The_table_dialog_validates_cell_by_cell_adds_a_blank_row_and_saves_everything_at_once()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/manual"
            ? FakeApi.Json("""{"id":"tnew","name":"fichier","databaseId":"d1","tableType":"Table","isManual":true,"columns":[],"indexes":[],"relations":[]}""", HttpStatusCode.Created)
            : null;
        var parameters = new DialogParameters<TableDialog> { { x => x.DatabaseId, "d1" }, { x => x.Tables, Boutique() } };
        var reference = await OpenAsync("Nouvelle table", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".table-dialog")));

        // Rien n'est enregistrable tant que le nom manque, et une ligne vide attend en fin de tableau.
        Assert.True(Dialogs!.Find("button.table-dialog-save").HasAttribute("disabled"));
        Assert.Single(Dialogs!.FindAll("tbody tr"));
        Dialogs!.Find("input.table-dialog-name").Input("fichier");
        Assert.True(Dialogs!.Find("button.table-dialog-save").HasAttribute("disabled")); // aucune colonne

        // Une première frappe crée la ligne suivante ; deux colonnes de même nom sont signalées sous la cellule.
        Dialogs!.Find("[data-cell='0:0']").Input("id");
        Assert.Equal(2, Dialogs!.FindAll("tbody tr").Count);
        Dialogs!.Find("[data-cell='1:0']").Input("ID");
        Assert.Contains("saisi deux fois", Dialogs!.Find("tr[data-row='1'] .egrid-error").TextContent);
        Assert.True(Dialogs!.Find("button.table-dialog-save").HasAttribute("disabled"));
        Dialogs!.Find("[data-cell='1:0']").Input("email");
        Assert.Empty(Dialogs!.FindAll(".egrid-error"));
        Dialogs!.Find("[data-cell='0:2']").Change(true); // clé primaire
        Assert.Contains("2 colonnes, dont 1 dans la clé primaire", Dialogs!.Find(".table-dialog-preview").TextContent);

        Dialogs!.Find("button.table-dialog-save").Click();
        var result = await reference.Result;

        Assert.False(result!.Canceled);
        Assert.Equal("tnew", result.Data);
        var body = BodyOf(HttpMethod.Post, "/api/v1/TableInfo/manual");
        Assert.Equal("fichier", body.GetProperty("name").GetString());
        Assert.Equal("d1", body.GetProperty("databaseId").GetString());
        var columns = body.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(["id", "email"], columns.Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.True(columns[0].GetProperty("isPrimaryKey").GetBoolean());
        Assert.False(columns[0].GetProperty("isNullable").GetBoolean() && columns[0].GetProperty("isPrimaryKey").GetBoolean() == false);
    }

    [BunitFact]
    public async Task The_table_dialog_refuses_a_name_already_taken_and_shows_the_server_error_without_closing()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/manual"
            ? FakeApi.Json("""{"title":"Une table nommée x existe déjà dans cette base.","status":400}""", HttpStatusCode.BadRequest)
            : null;
        var parameters = new DialogParameters<TableDialog> { { x => x.DatabaseId, "d1" }, { x => x.Tables, Boutique() } };
        await OpenAsync("Nouvelle table", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".table-dialog")));

        Dialogs!.Find("input.table-dialog-name").Input("Customers");
        Assert.Contains("existe déjà", Dialogs!.Find(".table-dialog-problem").TextContent);
        Dialogs!.Find("[data-cell='0:0']").Input("id");
        Assert.True(Dialogs!.Find("button.table-dialog-save").HasAttribute("disabled"));

        Dialogs!.Find("input.table-dialog-name").Input("autre");
        Dialogs!.Find("button.table-dialog-save").Click();
        Dialogs!.WaitForAssertion(() => Assert.Contains("existe déjà", Dialogs!.Find(".table-dialog-error").TextContent));
        Assert.NotEmpty(Dialogs!.FindAll(".table-dialog")); // la saisie n'est pas perdue
    }

    [BunitFact]
    public async Task Rows_pasted_from_a_spreadsheet_are_previewed_then_added_to_the_grid()
    {
        var parameters = new DialogParameters<TableDialog> { { x => x.DatabaseId, "d1" }, { x => x.Tables, Boutique() } };
        await OpenAsync("Nouvelle table", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".egrid")));

        Dialogs!.Find("textarea.egrid-paste-text").Input("id\tinteger\toui\tnon\tnon\tClé\r\nnom\ttext\r\n\r\nnom\ttext");
        // Trois lignes reconnues, dont une en double (« nom » deux fois) : le problème apparaîtra après l'ajout.
        Assert.Contains("3 lignes reconnues", Dialogs!.Find(".egrid-paste-preview").TextContent);
        Assert.Contains("2 avec un problème", Dialogs!.Find(".egrid-paste-preview").TextContent);
        Dialogs!.Find("button.egrid-paste-add").Click();

        Assert.Equal(4, Dialogs!.FindAll("tbody tr").Count); // 3 lignes + la ligne vide de fin
        Assert.Equal("Clé", Dialogs!.Find("[data-cell='0:5']").GetAttribute("value"));
        Assert.True(Dialogs!.Find("[data-cell='0:2']").HasAttribute("checked"));
        Assert.Equal(2, Dialogs!.FindAll(".egrid-error").Count);

        // Supprimer une ligne en erreur résout le doublon, sans quitter la grille.
        Dialogs!.Find("tr[data-row='3'] button.egrid-remove").Click();
        Assert.Empty(Dialogs!.FindAll(".egrid-error"));
        Assert.Equal(3, Dialogs!.FindAll("tbody tr").Count);
    }

    // --- Colonnes en grille ---

    [BunitFact]
    public async Task The_columns_dialog_describes_source_columns_in_bulk_and_keeps_their_name_and_type_locked()
    {
        var table = Boutique()[0];
        var parameters = new DialogParameters<ColumnsDialog> { { x => x.Table, table } };
        var reference = await OpenAsync("Colonnes de customers", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".columns-dialog")));

        // Une table de la source : pas de ligne vide pour en ajouter, nom et type en lecture seule.
        Assert.Equal(2, Dialogs!.FindAll("tbody tr").Count);
        Assert.True(Dialogs!.Find("[data-cell='0:0']").HasAttribute("readonly"));
        Assert.True(Dialogs!.Find("button.columns-dialog-save").HasAttribute("disabled"));

        Dialogs!.Find("[data-cell='1:5']").Input("Adresse de contact");
        Assert.Contains("1 description à enregistrer", Dialogs!.Find(".columns-dialog-preview").TextContent);
        Dialogs!.Find("button.columns-dialog-save").Click();
        Assert.False((await reference.Result)!.Canceled);

        var put = Requests(HttpMethod.Put, "/api/v1/ColumnInfo/c2/documentation").Single();
        Assert.Contains("Adresse de contact", Api.Bodies[Api.Requests.IndexOf(put)]);
        Assert.Empty(Requests(HttpMethod.Post, "/api/v1/TableInfo/t1/columns"));
    }

    [BunitFact]
    public async Task The_columns_dialog_adds_new_columns_to_a_manual_table()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TableInfo/tm/columns"
            ? FakeApi.Json("""{"id":"tm","name":"fichier","databaseId":"d1","tableType":"Table","isManual":true,"columns":[],"indexes":[],"relations":[]}""")
            : null;
        var table = new TableInfo { Id = "tm", Name = "fichier", DatabaseId = "d1", IsManual = true };
        var reference = await OpenAsync("Colonnes de fichier", new DialogParameters<ColumnsDialog> { { x => x.Table, table } });
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".columns-dialog")));

        Dialogs!.Find("[data-cell='0:0']").Input("ville");
        Dialogs!.Find("[data-cell='0:1']").Input("text");
        Assert.Contains("1 colonne à ajouter", Dialogs!.Find(".columns-dialog-preview").TextContent);
        Dialogs!.Find("button.columns-dialog-save").Click();
        Assert.False((await reference.Result)!.Canceled);

        var column = BodyOf(HttpMethod.Post, "/api/v1/TableInfo/tm/columns").GetProperty("columns").EnumerateArray().Single();
        Assert.Equal("ville", column.GetProperty("name").GetString());
    }

    // --- Étiquette ---

    [BunitFact]
    public async Task The_tag_dialog_creates_a_tag_and_puts_it_on_several_tables_at_once()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TagInfo"
            ? FakeApi.Json("""{"id":"gx","name":"legacy"}""", HttpStatusCode.Created)
            : r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/TagInfo/attach"
                ? FakeApi.Json("""{"id":"gx","name":"legacy"}""")
                : null;
        var databases = new[] { new DatabaseView { Id = "d1", Name = "Boutique", Type = "Sqlite" } };
        var tags = new[] { new TagInfo { Id = "g1", Name = "RGPD" } };
        var reference = await OpenAsync("Nouvelle étiquette", new DialogParameters<TagDialog> { { x => x.Databases, databases }, { x => x.Tags, tags } }, MaxWidth.Medium);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".tag-dialog")));

        Dialogs!.Find("input.tag-dialog-name").Input("rgpd");
        Assert.Contains("déjà ce nom", Dialogs!.Find(".tag-dialog-problem").TextContent);
        Assert.True(Dialogs!.Find("button.tag-dialog-save").HasAttribute("disabled"));
        Dialogs!.Find("input.tag-dialog-name").Input("legacy");
        Dialogs!.Find(".tag-dialog-database select").Change("d1");
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll("[data-tag-table]")));
        Dialogs!.Find("input.tag-dialog-search").Input("s");
        Dialogs!.Find("button.tag-dialog-select-found").Click();
        Assert.Contains("la poser sur 3 tables", Dialogs!.Find(".tag-dialog-preview").TextContent);

        Dialogs!.Find("button.tag-dialog-save").Click();
        Assert.Equal("legacy", await reference.Result is { Data: string name } ? name : null);

        Assert.Equal("legacy", BodyOf(HttpMethod.Post, "/api/v1/TagInfo").GetProperty("name").GetString());
        Assert.Equal(3, Requests(HttpMethod.Post, "/api/v1/TagInfo/attach").Count());
    }

    // --- Domaine : tables liées, relations indirectes, comptage par relation ---

    private const string TablesWithManualRelation = """
        [{"id":"t1","name":"customers","tableType":"Table","columns":[{"id":"c1","name":"id","dataType":"INTEGER","isPrimaryKey":true,"position":0}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c3","relationType":"One-to-Many","metaInfo":"auto:fk"},{"id":"r2","firstId":"c1","secondId":"c4","relationType":"One-to-Many"}]},
         {"id":"t2","name":"orders","tableType":"Table","columns":[{"id":"c3","name":"customer_id","dataType":"INTEGER","position":0}],"indexes":[],
          "relations":[{"id":"r1","firstId":"c1","secondId":"c3","relationType":"One-to-Many","metaInfo":"auto:fk"}]},
         {"id":"t3","name":"tags","tableType":"Table","columns":[{"id":"c4","name":"customer","dataType":"INTEGER","position":0}],"indexes":[],
          "relations":[{"id":"r2","firstId":"c1","secondId":"c4","relationType":"One-to-Many"}]}]
        """;

    private static TableInfo[] ManualRelationTables() => System.Text.Json.JsonSerializer.Deserialize<TableInfo[]>(TablesWithManualRelation,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    [BunitFact]
    public async Task The_domain_dialog_adds_the_linked_tables_in_one_click_and_says_how_many_relations_appear()
    {
        var existing = new DomainView
        {
            Id = "g1", DatabaseId = "d1", Name = "Ventes", Tables = [new DomainTableView { TableId = "t2" }]
        };
        var parameters = new DialogParameters<DomainDialog>
        {
            { x => x.DatabaseId, "d1" }, { x => x.Tables, ManualRelationTables() }, { x => x.Existing, existing }
        };
        await OpenAsync("Modifier le domaine", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".domain-dialog")));

        Assert.Contains("1 table dans ce domaine, 0 relation", Dialogs!.Find(".domain-dialog-preview").TextContent);
        // orders est reliée à customers (clé étrangère) : un clic l'ajoute, et la relation apparaît.
        Dialogs!.Find("button.domain-dialog-add-linked").Click();
        Assert.Equal(["customers", "orders"], Dialogs!.FindAll("[data-member]").Select(r => r.GetAttribute("data-member")!).Order().ToArray());
        Assert.Contains("1 relation entre elles", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Assert.Contains("en fait apparaître 1", Dialogs!.Find(".domain-dialog-preview").TextContent);
        // Les tables liées de customers : tags (relation manuelle), proposée à son tour.
        Dialogs!.Find("[data-member='customers'] button.domain-dialog-add-related").Click();
        Assert.Contains("en fait apparaître 2", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Assert.Empty(Dialogs!.FindAll("button.domain-dialog-add-linked")); // plus rien à ajouter
    }

    [BunitFact]
    public async Task A_new_domain_draws_only_detected_foreign_keys_unless_the_user_unticks_the_option()
    {
        Override = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/DomainInfo"
            ? FakeApi.Json("""{"id":"gnew","databaseId":"d1","name":"Tout"}""", HttpStatusCode.Created)
            : null;
        var parameters = new DialogParameters<DomainDialog>
        {
            { x => x.DatabaseId, "d1" }, { x => x.Tables, ManualRelationTables() }, { x => x.InitialTables, new[] { "t1", "t2", "t3" } }
        };
        var reference = await OpenAsync("Nouveau domaine", parameters);
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".domain-dialog")));

        // Deux relations entre les trois tables (comptées par relation) dont une manuelle : l'option est proposée, cochée.
        Assert.Contains("3 tables dans ce domaine, 2 relations", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Assert.Contains("1 relation indirecte ne sera pas dessinée", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Assert.True(Dialogs!.Find(".domain-dialog-fk-only input").HasAttribute("checked"));

        Dialogs!.Find("input.domain-dialog-name").Input("Tout");
        Dialogs!.Find("button.domain-dialog-save").Click();
        Assert.False((await reference.Result)!.Canceled);

        Assert.True(BodyOf(HttpMethod.Put, "/api/v1/DomainInfo/gnew/settings").GetProperty("hideIndirectRelations").GetBoolean());
    }

    [BunitFact]
    public async Task A_composite_relation_counts_once_in_the_domain_preview()
    {
        // Une clé composite : une relation, deux paires de colonnes.
        var tables = System.Text.Json.JsonSerializer.Deserialize<TableInfo[]>("""
            [{"id":"p","name":"parent","tableType":"Table","columns":[{"id":"pa","name":"a","dataType":"INTEGER","position":0},{"id":"pb","name":"b","dataType":"INTEGER","position":1}],"indexes":[],
              "relations":[{"id":"rc","firstId":"pa","secondId":"ca","relationType":"One-to-Many","metaInfo":"auto:fk","extraColumns":[{"firstId":"pb","secondId":"cb"}]}]},
             {"id":"c","name":"child","tableType":"Table","columns":[{"id":"ca","name":"pa","dataType":"INTEGER","position":0},{"id":"cb","name":"pb","dataType":"INTEGER","position":1}],"indexes":[],
              "relations":[{"id":"rc","firstId":"pa","secondId":"ca","relationType":"One-to-Many","metaInfo":"auto:fk","extraColumns":[{"firstId":"pb","secondId":"cb"}]}]}]
            """, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await OpenAsync("Nouveau domaine", new DialogParameters<DomainDialog>
        {
            { x => x.DatabaseId, "d1" }, { x => x.Tables, tables }, { x => x.InitialTables, new[] { "p", "c" } }
        });
        Dialogs!.WaitForAssertion(() => Assert.NotEmpty(Dialogs!.FindAll(".domain-dialog")));

        Assert.Contains("2 tables dans ce domaine, 1 relation entre elles", Dialogs!.Find(".domain-dialog-preview").TextContent);
        Assert.Empty(Dialogs!.FindAll(".domain-dialog-fk-only")); // une clé étrangère détectée n'est pas indirecte
    }

}
