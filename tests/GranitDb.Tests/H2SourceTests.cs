using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GranitDB.API.Connectors;
using GranitDB.API.Exceptions;

namespace GranitDb.Tests;

/// <summary>
///     Le lecteur H2 (sans Java) sur des fichiers créés par H2 lui-même, gardés dans <c>Fixtures/h2</c> :
///     <c>shop-*</c> (même script avec 2.1.214 et 2.5.252, le second avec un mot de passe), <c>many-*</c> (250 tables liées,
///     l'un compressé : la table système occupe plusieurs pages) et <c>live-*</c> (JVM arrêtée net, base ouverte avec une
///     transaction non validée).
/// </summary>
public sealed class H2SourceTests : IDisposable
{
    private readonly TestCatalog _env = new();

    public void Dispose() => _env.Dispose();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "h2", name + ".mv.db");

    private static Task<SourceSchema> ReadAsync(string path) => new H2SchemaConnector().ReadSchemaAsync(path);

    [Theory]
    [InlineData("shop-2.1.214")]
    [InlineData("shop-2.5.252")]
    public async Task Reads_tables_views_types_keys_and_indexes_of_each_version(string file)
    {
        var schema = await ReadAsync(Fixture(file));

        Assert.Equal(file, schema.DatabaseName);
        Assert.Equal("H2", schema.Type);
        // Ni la table supprimée, ni la table temporaire ; le schéma PUBLIC reste implicite.
        Assert.Equal(["CUSTOMERS", "SALES.LINES", "SALES.ORDERS", "V_CUSTOMERS", "Éléments (\"divers\")"],
            schema.Tables.Select(t => t.Name).Order(StringComparer.Ordinal));

        var columns = schema.Tables.Single(t => t.Name == "CUSTOMERS").Columns;
        Assert.Equal(["ID", "NAME", "BALANCE", "MAIL"], columns.Select(c => c.Name));
        Assert.Equal([0, 1, 2, 3], columns.Select(c => c.Ordinal));
        Assert.Equal(["INTEGER", "CHARACTER VARYING(100)", "DECIMAL(10,2)", "CHARACTER VARYING(200)"], columns.Select(c => c.DataType));
        Assert.True(columns[0].IsPrimaryKey);
        Assert.False(columns[0].IsNullable);
        // Une colonne à identifiant automatique n'a pas de valeur par défaut.
        Assert.Null(columns[0].DefaultValue);
        Assert.False(columns[1].IsNullable);
        Assert.Equal("'x, (y)'", columns[1].DefaultValue);
        Assert.Equal("0", columns[2].DefaultValue);

        Assert.Null(schema.Tables.Single(t => t.Name == "SALES.ORDERS").Columns.Single(c => c.Name == "TWICE").DefaultValue);
        var view = schema.Tables.Single(t => t.Name == "V_CUSTOMERS");
        Assert.Equal("View", view.TableType);
        Assert.Equal(["ID", "NAME"], view.Columns.Select(c => c.Name));
        Assert.All(view.Columns, c => Assert.Equal("ANY", c.DataType));
        Assert.Equal("Clé, 1", schema.Tables.Single(t => t.Name.StartsWith('É')).Columns.Single().Name);

        var groups = schema.ForeignKeyGroups().Select(g => g.ToList()).OrderBy(g => g.Count).ToList();
        Assert.Equal(2, groups.Count);
        Assert.Equal(("SALES.ORDERS", "CUSTOMER_ID", "CUSTOMERS", "ID"),
            (groups[0][0].Table, groups[0][0].Column, groups[0][0].RefTable, groups[0][0].RefColumn));
        Assert.Equal(["YR", "NUM"], groups[1].Select(k => k.Column));
        Assert.All(groups[1], k => Assert.Equal("SALES.ORDERS", k.RefTable));

        Assert.Contains(schema.Indexes, i => i is { Name: "UX_NAME", IsUnique: true });
        Assert.DoesNotContain(schema.Indexes, i => i.Name.StartsWith("PRIMARY_KEY", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("many-2.5.252")]
    [InlineData("many-compressed-2.1.214")]
    public async Task Reads_a_schema_spread_over_many_pages_compressed_or_not(string file)
    {
        var schema = await ReadAsync(Fixture(file));

        Assert.Equal(250, schema.Tables.Count);
        Assert.Equal(249, schema.ForeignKeys.Count);
        Assert.All(schema.Tables, t => Assert.Equal(3, t.Columns.Count));
        Assert.Equal("'valeur'", schema.Tables.Single(t => t.Name == "T_123").Columns[1].DefaultValue);
    }

    [Fact]
    public async Task Reads_a_database_copied_while_open_without_changing_it()
    {
        var copy = Path.Combine(_env.Directory, "live.mv.db");
        File.Copy(Fixture("live-2.5.252"), copy);
        var before = await File.ReadAllBytesAsync(copy);

        var schema = await ReadAsync(copy);

        Assert.Equal(12, schema.Tables.Count);
        Assert.Equal(before, await File.ReadAllBytesAsync(copy));
    }

    [Theory]
    [InlineData("H2encrypt\n0000000000000000", "chiffrée")]
    [InlineData("H:2,block:2,blockSize:1000,format:1,version:1\n", "1.x")]
    [InlineData("H:2,block:2,blockSize:1000,format:9,version:1\n", "pas encore pris en charge")]
    [InlineData("H:2,block:2,blockSize:1000,format:3,version:1\n", "endommagé")]
    [InlineData("autre chose", "en-tête")]
    public async Task Unsupported_files_say_why(string content, string expected)
    {
        var file = Path.Combine(_env.Directory, "x.mv.db");
        var bytes = new byte[16384];
        Encoding.Latin1.GetBytes(content).CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(file, bytes);

        var error = await Assert.ThrowsAsync<InvalidSourceException>(() => ReadAsync(file));

        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("shop-2.5.252")]
    [InlineData("many-compressed-2.1.214")]
    public async Task A_damaged_file_is_either_read_or_refused_never_a_crash(string name)
    {
        var original = await File.ReadAllBytesAsync(Fixture(name));
        var file = Path.Combine(_env.Directory, "damaged.mv.db");
        var random = new Random(1234);

        for (var attempt = 0; attempt < 300; attempt++)
        {
            var bytes = original.ToArray();
            if (attempt % 10 == 0) bytes = bytes[..random.Next(100, bytes.Length)];
            else
                for (var i = random.Next(1, 40); i > 0; i--)
                    bytes[random.Next(8192, bytes.Length)] = (byte)random.Next(256);
            await File.WriteAllBytesAsync(file, bytes);

            try
            {
                await ReadAsync(file);
            }
            catch (InvalidSourceException)
            {
                // Refus expliqué : attendu.
            }
        }
    }

    [Fact]
    public async Task The_file_import_recognizes_H2_resynchronizes_and_replaces_without_any_setting()
    {
        using var factory = new KestrelApiFactory(new Dictionary<string, string>
        {
            ["Catalog:DataDirectory"] = Path.Combine(_env.Directory, "api"),
            ["Catalog:AllowProfiling"] = "true"
        });
        using var client = factory.CreateKestrelClient();

        // Le fichier a un mot de passe H2 : il n'est pas demandé.
        using var upload = Form(Fixture("shop-2.5.252"), "boutique.mv.db");
        var response = await client.PostAsync("api/v1/import/sqlite", upload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        var database = JsonDocument.Parse(body).RootElement.GetProperty("database");
        var id = database.GetProperty("id").GetString();
        Assert.Equal("H2", database.GetProperty("type").GetString());
        Assert.Equal("boutique", database.GetProperty("name").GetString());
        Assert.True(database.GetProperty("canResync").GetBoolean());

        var sync = await client.PostAsync($"api/v1/DatabaseInfo/{id}/sync", null);
        Assert.True(sync.IsSuccessStatusCode, await sync.Content.ReadAsStringAsync());

        using var preview = Form(Fixture("many-2.5.252"), "autre.mv.db");
        var previewed = await client.PostAsync($"api/v1/import/sqlite/{id}/preview", preview);
        Assert.Contains("T_123", await previewed.Content.ReadAsStringAsync());
        using var replacement = Form(Fixture("shop-2.1.214"), "boutique.mv.db");
        var replaced = await client.PutAsync($"api/v1/import/sqlite/{id}", replacement);
        Assert.True(replaced.IsSuccessStatusCode, await replaced.Content.ReadAsStringAsync());

        // Une base H2 ne se remplace pas par un fichier SQLite.
        using var other = Form(_env.CreateSampleDb("other.db"), "other.db");
        var mismatch = await client.PutAsync($"api/v1/import/sqlite/{id}", other);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("H2", await mismatch.Content.ReadAsStringAsync());

        // Seul le schéma est lu : le profilage est refusé avec sa raison.
        var tables = await client.GetFromJsonAsync<JsonElement>($"api/v1/TableInfo/database/{id}");
        var tableId = tables.EnumerateArray().First(t => t.GetProperty("name").GetString() == "CUSTOMERS").GetProperty("id").GetString();
        var profile = await client.PostAsync($"api/v1/TableInfo/{tableId}/profile", null);
        Assert.Equal(HttpStatusCode.BadRequest, profile.StatusCode);
        Assert.Contains("H2", await profile.Content.ReadAsStringAsync());

        // La suppression retire aussi le fichier conservé.
        var sources = Path.Combine(_env.Directory, "api", "sources");
        Assert.EndsWith(".mv.db", Assert.Single(Directory.GetFiles(sources)));
        Assert.True((await client.DeleteAsync($"api/v1/DatabaseInfo/{id}")).IsSuccessStatusCode);
        Assert.Empty(Directory.GetFiles(sources));
    }

    private static MultipartFormDataContent Form(string file, string fileName) =>
        new() { { new ByteArrayContent(File.ReadAllBytes(file)), "file", fileName } };
}
