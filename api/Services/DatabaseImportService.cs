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
///     Documente une base à partir d'un fichier SQLite ou H2 (upload ou chemin local) ou d'une source déjà connue,
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

    /// <summary>
    ///     Synchronise le schéma dans une transaction (tout ou rien) puis laisse <paramref name="configure" /> fixer ce qui est propre à
    ///     la source (description, fichier ou connexion) avant d'enregistrer la base.
    /// </summary>
    private Task<ImportResult> SyncInTransactionAsync(string id, SourceSchema schema, string? name, Action<DatabaseInfo> configure,
        CancellationToken ct) =>
        repository.InTransactionAsync(async repo =>
        {
            var sync = await SchemaSynchronizer.SyncAsync(repo, id, schema, name, ct);
            var database = sync.Database;
            configure(database);
            await repo.UpdateAsync(id, database, ct);
            return new ImportResult(database, sync);
        }, ct);

    /// <summary>Importe un fichier SQLite envoyé par l'utilisateur et crée la base documentée.</summary>
    public async Task<ImportResult> ImportSqliteAsync(Stream content, string fileName, string? name,
        string? description, bool keepFile, CancellationToken ct = default)
    {
        var (temp, type) = await SaveTempAsync(content, ct);
        try
        {
            var schema = await Connector(type).ReadSchemaAsync(temp, ct);
            var id = Guid.NewGuid().ToString();
            var displayName = !string.IsNullOrWhiteSpace(name) ? name : BaseName(fileName);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = schema.DatabaseName;

            string? kept = null;
            try
            {
                // Tout ou rien : une erreur ou une annulation ne laisse pas de base à moitié importée.
                var imported = await SyncInTransactionAsync(id, schema, displayName, database =>
                {
                    database.Description = string.IsNullOrWhiteSpace(description) ? null : description;
                    database.SourcePath = keepFile ? kept = Keep(temp, type) : null;
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
        EnsureFile(database);

        var (temp, type) = await SaveTempAsync(content, ct);
        try
        {
            EnsureType(database, type);
            var schema = await Connector(type).ReadSchemaAsync(temp, ct);
            var previous = database.SourcePath;
            string? kept = null;
            ImportResult result;
            try
            {
                result = await SyncInTransactionAsync(id, schema, null, updated =>
                    updated.SourcePath = keepFile ? kept = Keep(temp, type) : null, ct);
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
        EnsureFile(database);
        var (temp, type) = await SaveTempAsync(content, ct);
        try
        {
            EnsureType(database, type);
            var schema = await Connector(type).ReadSchemaAsync(temp, ct);
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
        var type = File.Exists(full) && await TypeOfAsync(full, ct) is { } found ? found : SqliteSchemaConnector.TypeName;
        var schema = await Connector(type).ReadSchemaAsync(full, ct);
        var id = Guid.NewGuid().ToString();
        var imported = await SyncInTransactionAsync(id, schema, name, database =>
        {
            database.Description = string.IsNullOrWhiteSpace(description) ? null : description;
            database.SourcePath = full;
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
        await RecordSyncAsync(id, null, ct);
        return new ImportResult(sync.Database, sync);
    }

    /// <summary>
    ///     Note le résultat de la dernière relecture de la source (<c>error</c> nul : réussie) sans toucher au reste de la base ; une
    ///     panne d'enregistrement ne fait pas échouer la synchronisation.
    /// </summary>
    public async Task RecordSyncAsync(string id, string? error, CancellationToken ct = default)
    {
        try
        {
            var database = await repository.GetAsync<DatabaseInfo>(id, ct);
            if (database == null) return;
            database.LastSyncAt = DateTime.UtcNow;
            database.LastSyncOk = error == null;
            database.LastSyncError = error is { Length: > 500 } ? error[..500] : error;
            await repository.UpdateAsync(id, database, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Le suivi est un confort : la relecture elle-même a réussi ou échoué indépendamment.
        }
    }

    /// <summary>Annonce aux webhooks qu'une relecture planifiée a échoué.</summary>
    public void NotifySyncFailed(DatabaseInfo database, string error) =>
        webhooks.Enqueue(WebhookEvents.SyncFailed, database.Id, new
        {
            database = new { id = database.Id, name = database.Name, type = database.Type },
            error
        });

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
        if (ServerEngines.IsServer(database.Type))
            return (database, await ReadServerSourceAsync(database, ct));
        EnsureFile(database);
        var schema = await Connector(database.Type).ReadSchemaAsync(KeptFile(database), ct);
        // Le fichier conservé porte un nom aléatoire : le nom de la base ne doit pas en venir.
        return (database, schema with { DatabaseName = database.Name });
    }

    /// <summary>Le fichier conservé d'une base : présent, et dans le dossier des sources sauf si les chemins locaux sont permis.</summary>
    private string KeptFile(DatabaseInfo database)
    {
        if (string.IsNullOrWhiteSpace(database.SourcePath))
            throw new InvalidSourceException(
                "Aucun fichier source n'est conservé pour cette base : renvoyez le fichier (PUT).");
        if (!IsManagedFile(database.SourcePath) && !Options.AllowLocalPaths)
            throw new InvalidSourceException("Ce chemin source n'est pas autorisé.");
        return database.SourcePath;
    }

    // --- SQL Server ---

    /// <summary>Les sources client/serveur (SQL Server, PostgreSQL, MySQL) partagent un même interrupteur, <c>Catalog:AllowSqlServerSources</c>.</summary>
    private void EnsureServerAllowed(ServerEngine engine)
    {
        if (!Options.AllowSqlServerSources)
            throw new InvalidSourceException(
                $"Les sources {engine.DisplayName} sont désactivées (Catalog:AllowSqlServerSources = false) : l'API ne se connecte pas à un serveur choisi par l'appelant.");
    }

    private async Task<SourceSchema> ReadServerSourceAsync(DatabaseInfo database, CancellationToken ct)
    {
        var engine = ServerEngines.Of(database.Type);
        var connection = ServerConnectionOf(database);
        var schema = await Connector(engine.Type).ReadSchemaAsync(connection.ConnectionString, ct);
        return SourceSchemaFilter.Apply(schema, connection.Schemas, engine);
    }

    /// <summary>La connexion enregistrée (déchiffrée) d'une base client/serveur ; mêmes contrôles pour le schéma et le profilage.</summary>
    private SourceConnection ServerConnectionOf(DatabaseInfo database)
    {
        EnsureServerAllowed(ServerEngines.Of(database.Type));
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
        return connection;
    }

    /// <summary>
    ///     Ce qu'il faut pour lire les données d'une base (profilage) : le type et le chemin du fichier ou la chaîne de connexion,
    ///     avec les contrôles de la lecture du schéma. <c>null</c> si la base n'existe pas.
    /// </summary>
    public Task<(string Type, string Source)?> ResolveSourceAsync(DatabaseInfo database)
    {
        if (ServerEngines.Find(database.Type) is { } engine)
            return Task.FromResult<(string, string)?>((engine.Type, ServerConnectionOf(database).ConnectionString));
        if (IsType(database, H2SchemaConnector.TypeName))
            throw new InvalidSourceException(
                "La lecture des données (profilage, détection de relations) n'est pas disponible pour les bases H2 : seul leur schéma est lu.");
        EnsureSqlite(database);
        return Task.FromResult<(string, string)?>((SqliteSchemaConnector.TypeName, KeptFile(database)));
    }

    /// <summary>
    ///     Essaie la connexion et dit ce qui serait documenté. Un échec de connexion n'est pas une erreur de requête : il
    ///     revient avec la consigne à suivre (<c>Ok</c> faux).
    /// </summary>
    public async Task<ConnectionTestResult> TestServerAsync(ServerConnectionInput input, CancellationToken ct = default)
    {
        var engine = input.ServerEngine;
        EnsureServerAllowed(engine);
        SourceSchema schema;
        try
        {
            schema = await Connector(engine.Type).ReadSchemaAsync(input.BuildConnectionString(), ct);
        }
        catch (InvalidSourceException e)
        {
            return new ConnectionTestResult(false, e.Message, 0, 0, []);
        }

        var schemas = schema.Tables.GroupBy(t => engine.SupportsSchemas ? engine.SchemaOf(t.Name) : schema.DatabaseName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new SchemaCount(g.Key, g.Count())).ToList();
        var views = schema.Tables.Count(t => t.TableType == "View");
        var tables = schema.Tables.Count - views;
        var message = schema.Tables.Count == 0
            ? $"Connexion réussie, mais aucune table n'est visible : le compte doit pouvoir lire les métadonnées ({MetadataRight(engine)})."
            : $"Connexion réussie : {tables} table(s) et {views} vue(s) dans {schemas.Count} schéma(s).";
        return new ConnectionTestResult(true, message, tables, views, schemas);
    }

    /// <summary>Le droit qui permet de lire les métadonnées, pour les messages.</summary>
    private static string MetadataRight(ServerEngine engine) => engine.Type switch
    {
        SqlServerSchemaConnector.TypeName => "VIEW DEFINITION",
        PostgreSqlSchemaConnector.TypeName => "USAGE sur les schémas",
        _ => "SELECT sur la base"
    };

    /// <summary>Documente une base client/serveur : la connexion est enregistrée chiffrée, la base se resynchronise ensuite par l'API.</summary>
    public async Task<ImportResult> ImportServerAsync(ServerConnectionInput input, CancellationToken ct = default)
    {
        EnsureServerAllowed(input.ServerEngine);
        var (schema, secret) = await ReadForImportAsync(input, ct);
        var id = Guid.NewGuid().ToString();
        var name = string.IsNullOrWhiteSpace(input.Name) ? schema.DatabaseName : input.Name.Trim();
        var imported = await SyncInTransactionAsync(id, schema, name, database =>
        {
            database.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
            database.SourcePath = null;
            database.SourceSecret = secret;
            database.SourceLabel = input.Label;
        }, ct);
        NotifyImported(imported);
        return imported;
    }

    /// <summary>Remplace la connexion d'une base client/serveur (mot de passe changé, autre schéma…) et la resynchronise ; le moteur ne change pas.</summary>
    public async Task<ImportResult?> ReplaceServerConnectionAsync(string id, ServerConnectionInput input,
        CancellationToken ct = default)
    {
        EnsureServerAllowed(input.ServerEngine);
        var existing = await repository.GetAsync<DatabaseInfo>(id, ct);
        if (existing == null) return null;
        if (!existing.Type.Equals(input.ServerEngine.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidSourceException($"Cette base est de type {existing.Type}, pas {input.ServerEngine.Type}.");

        var (schema, secret) = await ReadForImportAsync(input, ct);
        var replaced = await SyncInTransactionAsync(id, schema, null, database =>
        {
            database.SourceSecret = secret;
            database.SourceLabel = input.Label;
        }, ct);
        NotifySchemaChange(replaced.Sync);
        return replaced;
    }

    private async Task<(SourceSchema Schema, string Secret)> ReadForImportAsync(ServerConnectionInput input, CancellationToken ct)
    {
        var engine = input.ServerEngine;
        var connectionString = input.BuildConnectionString();
        var schema = SourceSchemaFilter.Apply(
            await Connector(engine.Type).ReadSchemaAsync(connectionString, ct), input.SelectedSchemas, engine);
        if (schema.Tables.Count == 0)
            throw new InvalidSourceException(
                $"Aucune table à documenter : vérifiez les schémas choisis et que le compte peut lire les métadonnées ({MetadataRight(engine)}).");
        var secret = protector.Protect(JsonSerializer.Serialize(new SourceConnection(connectionString, input.SelectedSchemas)));
        return (schema, secret);
    }

    /// <summary>Synchronise une source (SqlServer ou Sqlite) avec un identifiant imposé, ex : au démarrage.</summary>
    public async Task<SyncResult> SyncSourceAsync(StartupSource source, CancellationToken ct = default)
    {
        // La chaîne de connexion peut être une référence (env:NOM, file:/chemin) : le secret reste hors du fichier de configuration.
        var schema = await Connector(source.Type).ReadSchemaAsync(SecretReference.Resolve(source.Source), ct);
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

    private static bool IsType(DatabaseInfo database, string type) =>
        database.Type.Equals(type, StringComparison.OrdinalIgnoreCase);

    private static void EnsureType(DatabaseInfo database, string type)
    {
        if (!IsType(database, type))
            throw new InvalidSourceException($"Cette base est de type {database.Type}, pas {type}.");
    }

    /// <summary>Une base documentée à partir d'un fichier (SQLite ou H2).</summary>
    private static void EnsureFile(DatabaseInfo database)
    {
        if (!IsType(database, SqliteSchemaConnector.TypeName) && !IsType(database, H2SchemaConnector.TypeName))
            throw new InvalidSourceException($"Cette base est de type {database.Type} : elle n'est pas documentée à partir d'un fichier.");
    }

    private static void EnsureSqlite(DatabaseInfo database) => EnsureType(database, SqliteSchemaConnector.TypeName);

    private bool IsManagedFile(string path)
    {
        var dir = Path.GetFullPath(Options.SourcesDirectory) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copie le flux dans un fichier temporaire en contrôlant taille et en-tête ; renvoie aussi le type reconnu.</summary>
    private async Task<(string Path, string Type)> SaveTempAsync(Stream content, CancellationToken ct)
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

            var type = await TypeOfAsync(temp, ct) ?? throw new InvalidSourceException(
                "Le fichier n'est ni une base SQLite ni une base H2 (en-tête invalide).");
            return (temp, type);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    /// <summary>Le type d'un fichier de base d'après ses premiers octets ; <c>null</c> s'il n'est pas reconnu.</summary>
    private static async Task<string?> TypeOfAsync(string path, CancellationToken ct)
    {
        var header = new byte[16];
        await using var file = File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(header, header.Length, false, ct);
        var start = header.AsSpan(0, read);
        if (start.StartsWith(SqliteHeader)) return SqliteSchemaConnector.TypeName;
        // Une base chiffrée est reconnue pour que le lecteur H2 dise pourquoi il la refuse.
        return start.StartsWith("H:2,"u8) || start.StartsWith("H2encrypt"u8) ? H2SchemaConnector.TypeName : null;
    }

    /// <summary>Le nom d'un fichier sans son extension (<c>.mv.db</c> compte pour une seule).</summary>
    private static string BaseName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.EndsWith(".mv.db", StringComparison.OrdinalIgnoreCase) ? name[..^6] : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Renomme le fichier temporaire en fichier conservé (nom aléatoire) et retourne son chemin.</summary>
    private string Keep(string temp, string type)
    {
        var extension = type == H2SchemaConnector.TypeName ? ".mv.db" : ".db";
        var final = Path.GetFullPath(Path.Combine(Options.SourcesDirectory, $"{Guid.NewGuid()}{extension}"));
        File.Copy(temp, final);
        return final;
    }

    /// <summary>
    ///     Supprime du dossier des sources les fichiers que plus aucune base ne référence (arrêt brutal entre la copie et
    ///     l'enregistrement, fichiers temporaires restants). Les fichiers de moins d'une heure sont laissés : un import peut
    ///     être en cours. Renvoie le nombre de fichiers supprimés.
    /// </summary>
    public async Task<int> CleanOrphanFilesAsync(CancellationToken ct = default)
    {
        var dir = Options.SourcesDirectory;
        if (!Directory.Exists(dir)) return 0;
        var referenced = (await repository.GetAllAsync<DatabaseInfo>(ct))
            .Select(d => d.SourcePath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limit = DateTime.UtcNow.AddHours(-1);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".db" or ".tmp")) continue;
            if (referenced.Contains(Path.GetFullPath(file)) || File.GetLastWriteTimeUtc(file) > limit) continue;
            DeleteQuietly(file);
            removed++;
        }
        return removed;
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
