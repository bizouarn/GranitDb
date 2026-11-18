using GranitDB.API.Connectors;
using GranitDB.API.Data;
using GranitDB.API.Exceptions;
using System.Security.Cryptography;
using System.Text.Json;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Inputs;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

public record ImportResult(DatabaseInfo Database, SyncResult Sync);

/// <summary>Nombre de tables d'un schéma SQL Server vu par un test de connexion.</summary>
public record SchemaCount(string Name, int Tables);

/// <summary>Résultat d'un test de connexion : <c>Ok</c> faux avec la consigne à suivre, ou ce qui serait documenté.</summary>
public record ConnectionTestResult(bool Ok, string Message, int Tables, int Views, IReadOnlyList<SchemaCount> Schemas);

/// <summary>Ce qui est chiffré dans le catalogue pour une source SQL Server (jamais exposé par l'API).</summary>
internal record SourceConnection(string ConnectionString, string[]? Schemas);

/// <summary>
///     Documente une base à partir d'un fichier SQLite (upload ou chemin local) ou d'une source déjà connue,
///     sans serveur de base de données.
/// </summary>
public class DatabaseImportService(
    ICatalogRepository repository,
    SchemaSynchronizer synchronizer,
    IEnumerable<ISchemaConnector> connectors,
    IOptions<CatalogOptions> options,
    ISecretProtector protector,
    WebhookDispatcher webhooks)
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    private CatalogOptions Options => options.Value;

    private ISchemaConnector Connector(string type)
    {
        return connectors.FirstOrDefault(c => c.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidSourceException($"Type de base non pris en charge : {type}");
    }

    /// <summary>Importe un fichier SQLite envoyé par l'utilisateur et crée la base documentée.</summary>
    public async Task<ImportResult> ImportSqliteAsync(Stream content, string fileName, string? name,
        string? description, bool keepFile, CancellationToken ct = default)
    {
        var temp = await SaveTempAsync(content, ct);
        try
        {
            var schema = await Connector(SqliteSchemaConnector.TypeName).ReadSchemaAsync(temp, ct);
            var id = Guid.NewGuid().ToString();
            var displayName = !string.IsNullOrWhiteSpace(name) ? name : Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = schema.DatabaseName;

            string? kept = null;
            try
            {
                // Tout ou rien : une erreur ou une annulation ne laisse pas de base à moitié importée.
                var imported = await repository.InTransactionAsync(async repo =>
                {
                    var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, displayName, ct);
                    var database = sync.Database;
                    database.Description = description;
                    database.SourcePath = keepFile ? kept = Keep(temp) : null;
                    await repo.UpdateAsync(id, database, ct);
                    return new ImportResult(database, sync);
                }, ct);
                NotifyImported(imported);
                return imported;
            }
            catch
            {
                if (kept != null) DeleteQuietly(kept);
                throw;
            }
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Remplace le fichier d'une base SQLite déjà documentée et la resynchronise.</summary>
    public async Task<ImportResult?> ReplaceSqliteAsync(string id, Stream content, bool keepFile,
        CancellationToken ct = default)
    {
        var database = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (database == null) return null;
        EnsureSqlite(database);

        var temp = await SaveTempAsync(content, ct);
        try
        {
            var schema = await Connector(SqliteSchemaConnector.TypeName).ReadSchemaAsync(temp, ct);
            var previous = database.SourcePath;
            string? kept = null;
            ImportResult result;
            try
            {
                result = await repository.InTransactionAsync(async repo =>
                {
                    var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, ct: ct);
                    var updated = sync.Database;
                    updated.SourcePath = keepFile ? kept = Keep(temp) : null;
                    await repo.UpdateAsync(id, updated, ct);
                    return new ImportResult(updated, sync);
                }, ct);
            }
            catch
            {
                if (kept != null) DeleteQuietly(kept);
                throw;
            }

            if (previous != null && IsManagedFile(previous)) DeleteQuietly(previous);
            NotifySchemaChange(result.Sync);
            return result;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Ce que ferait <see cref="ReplaceSqliteAsync" /> avec ce fichier, sans rien modifier ni conserver. <c>null</c> si la base n'existe pas.</summary>
    public async Task<SyncPreview?> PreviewReplaceSqliteAsync(string id, Stream content, CancellationToken ct = default)
    {
        var database = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (database == null) return null;
        EnsureSqlite(database);
        var temp = await SaveTempAsync(content, ct);
        try
        {
            var schema = await Connector(SqliteSchemaConnector.TypeName).ReadSchemaAsync(temp, ct);
            return await SchemaDiffer.DiffAsync(repository, id, schema, ct);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Documente un fichier SQLite existant sur le serveur, sans le copier.</summary>
    public async Task<ImportResult> ImportLocalSqliteAsync(string path, string? name, string? description,
        CancellationToken ct = default)
    {
        if (!Options.AllowLocalPaths)
            throw new InvalidSourceException(
                "Les chemins locaux sont désactivés (Catalog:AllowLocalPaths = false).");
        var full = Path.GetFullPath(path);
        var schema = await Connector(SqliteSchemaConnector.TypeName).ReadSchemaAsync(full, ct);
        var id = Guid.NewGuid().ToString();
        var imported = await repository.InTransactionAsync(async repo =>
        {
            var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, name, ct);
            var database = sync.Database;
            database.Description = description;
            database.SourcePath = full;
            await repo.UpdateAsync(id, database, ct);
            return new ImportResult(database, sync);
        }, ct);
        NotifyImported(imported);
        return imported;
    }

    /// <summary>Relit la source enregistrée d'une base (fichier SQLite conservé) et met à jour le catalogue.</summary>
    public async Task<ImportResult?> ResyncAsync(string id, CancellationToken ct = default)
    {
        var read = await ReadSourceAsync(id, ct);
        if (read == null) return null;
        var sync = await synchronizer.SyncAsync(id, read.Value.Schema, ct: ct);
        NotifySchemaChange(sync);
        return new ImportResult(sync.Database, sync);
    }

    /// <summary>Ce que ferait <see cref="ResyncAsync" />, sans rien modifier. <c>null</c> si la base n'existe pas.</summary>
    public async Task<SyncPreview?> PreviewResyncAsync(string id, CancellationToken ct = default)
    {
        var read = await ReadSourceAsync(id, ct);
        return read == null ? null : await SchemaDiffer.DiffAsync(repository, id, read.Value.Schema, ct);
    }

    /// <summary>Relit le schéma de la source conservée d'une base (mêmes contrôles pour la synchronisation et son aperçu).</summary>
    private async Task<(DatabaseInfo Database, SourceSchema Schema)?> ReadSourceAsync(string id, CancellationToken ct)
    {
        var database = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (database == null) return null;
        if (database.Type.Equals(SqlServerSchemaConnector.TypeName, StringComparison.OrdinalIgnoreCase))
            return (database, await ReadSqlServerSourceAsync(database, ct));
        EnsureSqlite(database);
        if (string.IsNullOrWhiteSpace(database.SourcePath))
            throw new InvalidSourceException(
                "Aucun fichier source n'est conservé pour cette base : renvoyez le fichier (PUT).");
        if (!IsManagedFile(database.SourcePath) && !Options.AllowLocalPaths)
            throw new InvalidSourceException("Ce chemin source n'est pas autorisé.");

        var schema = await Connector(SqliteSchemaConnector.TypeName).ReadSchemaAsync(database.SourcePath, ct);
        return (database, schema);
    }

    // --- SQL Server ---

    private void EnsureSqlServerAllowed()
    {
        if (!Options.AllowSqlServerSources)
            throw new InvalidSourceException(
                "Les sources SQL Server sont désactivées (Catalog:AllowSqlServerSources = false) : l'API ne se connecte pas à un serveur choisi par l'appelant.");
    }

    private async Task<SourceSchema> ReadSqlServerSourceAsync(DatabaseInfo database, CancellationToken ct)
    {
        EnsureSqlServerAllowed();
        if (string.IsNullOrWhiteSpace(database.SourceSecret))
            throw new InvalidSourceException(
                "Aucune connexion n'est enregistrée pour cette base (elle est lue au démarrage par Catalog:StartupSources) : enregistrez-la depuis « Connecter un serveur SQL Server ».");

        SourceConnection? connection;
        try
        {
            connection = JsonSerializer.Deserialize<SourceConnection>(protector.Unprotect(database.SourceSecret));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Clés de chiffrement perdues ou changées, ou contenu altéré.
            throw new InvalidSourceException(
                "La connexion enregistrée n'est plus lisible (les clés de chiffrement ont changé) : saisissez-la de nouveau.", e);
        }

        if (connection == null || string.IsNullOrWhiteSpace(connection.ConnectionString))
            throw new InvalidSourceException("La connexion enregistrée est vide : saisissez-la de nouveau.");
        var schema = await Connector(SqlServerSchemaConnector.TypeName).ReadSchemaAsync(connection.ConnectionString, ct);
        return SourceSchemaFilter.Apply(schema, connection.Schemas);
    }

    /// <summary>
    ///     Essaie la connexion et dit ce qui serait documenté. Un échec de connexion n'est pas une erreur de requête : il
    ///     revient avec la consigne à suivre (<c>Ok</c> faux).
    /// </summary>
    public async Task<ConnectionTestResult> TestSqlServerAsync(SqlServerConnectionInput input, CancellationToken ct = default)
    {
        EnsureSqlServerAllowed();
        SourceSchema schema;
        try
        {
            schema = await Connector(SqlServerSchemaConnector.TypeName).ReadSchemaAsync(input.BuildConnectionString(), ct);
        }
        catch (InvalidSourceException e)
        {
            return new ConnectionTestResult(false, e.Message, 0, 0, []);
        }

        var schemas = schema.Tables.GroupBy(t => SourceSchemaFilter.SchemaOf(t.Name))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new SchemaCount(g.Key, g.Count())).ToList();
        var views = schema.Tables.Count(t => t.TableType == "View");
        var tables = schema.Tables.Count - views;
        var message = schema.Tables.Count == 0
            ? "Connexion réussie, mais aucune table n'est visible : le compte doit pouvoir lire les métadonnées (VIEW DEFINITION)."
            : $"Connexion réussie : {tables} table(s) et {views} vue(s) dans {schemas.Count} schéma(s).";
        return new ConnectionTestResult(true, message, tables, views, schemas);
    }

    /// <summary>Documente une base SQL Server : la connexion est enregistrée chiffrée, la base se resynchronise ensuite par l'API.</summary>
    public async Task<ImportResult> ImportSqlServerAsync(SqlServerConnectionInput input, CancellationToken ct = default)
    {
        EnsureSqlServerAllowed();
        var (schema, secret) = await ReadForImportAsync(input, ct);
        var id = Guid.NewGuid().ToString();
        var name = string.IsNullOrWhiteSpace(input.Name) ? schema.DatabaseName : input.Name.Trim();
        var imported = await repository.InTransactionAsync(async repo =>
        {
            var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, name, ct);
            var database = sync.Database;
            database.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            database.SourcePath = null;
            database.SourceSecret = secret;
            database.SourceLabel = input.Label;
            await repo.UpdateAsync(id, database, ct);
            return new ImportResult(database, sync);
        }, ct);
        NotifyImported(imported);
        return imported;
    }

    /// <summary>Remplace la connexion d'une base SQL Server (mot de passe changé, autre schéma…) et la resynchronise.</summary>
    public async Task<ImportResult?> ReplaceSqlServerConnectionAsync(string id, SqlServerConnectionInput input,
        CancellationToken ct = default)
    {
        EnsureSqlServerAllowed();
        var existing = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (existing == null) return null;
        if (!existing.Type.Equals(SqlServerSchemaConnector.TypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidSourceException($"Cette base est de type {existing.Type}, pas SqlServer.");

        var (schema, secret) = await ReadForImportAsync(input, ct);
        var replaced = await repository.InTransactionAsync(async repo =>
        {
            var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, ct: ct);
            var database = sync.Database;
            database.SourceSecret = secret;
            database.SourceLabel = input.Label;
            await repo.UpdateAsync(id, database, ct);
            return new ImportResult(database, sync);
        }, ct);
        NotifySchemaChange(replaced.Sync);
        return replaced;
    }

    private async Task<(SourceSchema Schema, string Secret)> ReadForImportAsync(SqlServerConnectionInput input, CancellationToken ct)
    {
        var connectionString = input.BuildConnectionString();
        var schema = SourceSchemaFilter.Apply(
            await Connector(SqlServerSchemaConnector.TypeName).ReadSchemaAsync(connectionString, ct), input.SelectedSchemas);
        if (schema.Tables.Count == 0)
            throw new InvalidSourceException(
                "Aucune table à documenter : vérifiez les schémas choisis et que le compte peut lire les métadonnées (VIEW DEFINITION).");
        var secret = protector.Protect(JsonSerializer.Serialize(new SourceConnection(connectionString, input.SelectedSchemas)));
        return (schema, secret);
    }

    /// <summary>Synchronise une source (SqlServer ou Sqlite) avec un identifiant imposé, ex : au démarrage.</summary>
    public async Task<SyncResult> SyncSourceAsync(StartupSource source, CancellationToken ct = default)
    {
        var schema = await Connector(source.Type).ReadSchemaAsync(source.Source, ct);
        var result = await synchronizer.SyncAsync(source.Id, schema, source.Name, ct);
        NotifySchemaChange(result);
        return result;
    }

    /// <summary>Prévient les webhooks (sans filtre de base : la base vient d'être créée) qu'une base a été importée.</summary>
    private void NotifyImported(ImportResult imported) =>
        webhooks.Enqueue(WebhookEvents.DatabaseImported, imported.Database.Id, new
        {
            database = new { id = imported.Database.Id, name = imported.Database.Name, type = imported.Database.Type },
            tables = imported.Sync.TablesAdded
        });

    /// <summary>
    ///     Prévient les webhooks quand une resynchronisation a changé le schéma documenté. Les relations recréées à chaque
    ///     synchronisation ne comptent pas : seuls tables, colonnes et index font un changement.
    /// </summary>
    private void NotifySchemaChange(SyncResult sync)
    {
        if (sync.TablesAdded + sync.TablesRemoved + sync.ColumnsAdded + sync.ColumnsUpdated + sync.ColumnsRemoved +
            sync.IndexesAdded + sync.IndexesRemoved == 0) return;
        webhooks.Enqueue(WebhookEvents.SchemaChanged, sync.Database.Id, new
        {
            database = new { id = sync.Database.Id, name = sync.Database.Name },
            changes = new
            {
                tablesAdded = sync.TablesAdded, tablesRemoved = sync.TablesRemoved, columnsAdded = sync.ColumnsAdded,
                columnsUpdated = sync.ColumnsUpdated, columnsRemoved = sync.ColumnsRemoved, indexesAdded = sync.IndexesAdded,
                indexesRemoved = sync.IndexesRemoved
            }
        });
    }

    /// <summary>
    ///     Retire la base du catalogue (tables, colonnes, relations, domaines, étiquettes liés) et la note dans
    ///     l'historique ; le fichier source que le serveur conservait est supprimé aussi. La base source n'est jamais touchée.
    /// </summary>
    public async Task<bool> DeleteDatabaseAsync(string id, string? actor, CancellationToken ct = default)
    {
        var database = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (database == null) return false;
        var deleted = await repository.InTransactionAsync(async repo =>
        {
            if (!await repo.DeleteAsync<DatabaseInfo>(id, ct)) return false;
            await repo.RecordHistoryAsync<DatabaseInfo>(id, "Deleted", database.Name, null, actor, ct);
            return true;
        }, ct);
        if (deleted && !string.IsNullOrWhiteSpace(database.SourcePath) && IsManagedFile(database.SourcePath))
            DeleteQuietly(database.SourcePath);
        if (deleted) webhooks.Enqueue(WebhookEvents.DatabaseDeleted, id, new { database = new { id, name = database.Name } });
        return deleted;
    }

    private static void EnsureSqlite(DatabaseInfo database)
    {
        if (!database.Type.Equals(SqliteSchemaConnector.TypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidSourceException($"Cette base est de type {database.Type}, pas Sqlite.");
    }

    private bool IsManagedFile(string path)
    {
        var dir = Path.GetFullPath(Options.SourcesDirectory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copie le flux dans un fichier temporaire en contrôlant taille et en-tête SQLite.</summary>
    private async Task<string> SaveTempAsync(Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(Options.SourcesDirectory);
        var temp = Path.Combine(Options.SourcesDirectory, $"{Guid.NewGuid()}.tmp");
        var max = Options.MaxUploadMb * 1024L * 1024L;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > max)
                        throw new InvalidSourceException($"Fichier trop volumineux (maximum {Options.MaxUploadMb} Mo).");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                if (total == 0) throw new InvalidSourceException("Aucun fichier n'a été envoyé.");
            }

            var header = new byte[SqliteHeader.Length];
            await using (var check = File.OpenRead(temp))
            {
                var n = await check.ReadAsync(header, ct);
                if (n < SqliteHeader.Length || !header.AsSpan().SequenceEqual(SqliteHeader))
                    throw new InvalidSourceException("Le fichier n'est pas une base SQLite (en-tête invalide).");
            }

            return temp;
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    /// <summary>Renomme le fichier temporaire en fichier conservé (nom aléatoire) et retourne son chemin.</summary>
    private string Keep(string temp)
    {
        var final = Path.GetFullPath(Path.Combine(Options.SourcesDirectory, $"{Guid.NewGuid()}.db"));
        File.Copy(temp, final);
        return final;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
