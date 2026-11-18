using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Reflection;
using Dapper;
using GranitDB.API.Exceptions;
using GranitDB.API.Models;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Data;

public class CatalogRepository : ICatalogRepository
{
    private sealed record EntityMap(string Table, string[] Columns);

    private static readonly Dictionary<Type, EntityMap> Maps = new();

    private readonly IDbConnectionFactory _factory;
    private readonly IOptions<CatalogOptions> _options;

    // Renseignés uniquement pour l'instance liée à une transaction (voir InTransactionAsync).
    private readonly IDbConnection? _boundConnection;
    private readonly IDbTransaction? _boundTransaction;

    public CatalogRepository(IDbConnectionFactory factory, IOptions<CatalogOptions> options)
    {
        _factory = factory;
        _options = options;
    }

    private CatalogRepository(CatalogRepository parent, IDbConnection connection, IDbTransaction transaction)
    {
        _factory = parent._factory;
        _options = parent._options;
        _boundConnection = connection;
        _boundTransaction = transaction;
    }

    /// <summary>Connexion à utiliser pour une opération : celle de la transaction en cours, sinon une nouvelle.</summary>
    private sealed class Lease(IDbConnection connection, IDbTransaction? transaction, bool owned) : IDisposable
    {
        public IDbConnection Connection { get; } = connection;
        public IDbTransaction? Transaction { get; } = transaction;

        public void Dispose()
        {
            if (owned) Connection.Dispose();
        }
    }

    private async Task<Lease> LeaseAsync(CancellationToken ct)
    {
        return _boundConnection != null
            ? new Lease(_boundConnection, _boundTransaction, false)
            : new Lease(await _factory.OpenAsync(ct), null, true);
    }

    private static EntityMap MapOf<T>()
    {
        lock (Maps)
        {
            if (Maps.TryGetValue(typeof(T), out var map))
                return map;
            var table = typeof(T).GetCustomAttribute<TableAttribute>()?.Name
                        ?? throw new InvalidOperationException($"{typeof(T).Name} n'a pas d'attribut [Table].");
            var columns = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p is { CanRead: true, CanWrite: true } && p.GetCustomAttribute<NotMappedAttribute>() == null)
                .Select(p => p.Name)
                .ToArray();
            return Maps[typeof(T)] = new EntityMap(table, columns);
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var opt = _options.Value;
        if (opt.IsSqlite)
        {
            Directory.CreateDirectory(opt.DataDirectory);
            Directory.CreateDirectory(opt.SourcesDirectory);
        }
        else
        {
            await SqlServerBootstrap.EnsureDatabaseAsync(opt.ResolveConnectionString(), ct: ct);
        }

        await new MigrationRunner(_factory).MigrateAsync(ct);
    }

    public async Task<TResult> InTransactionAsync<TResult>(Func<ICatalogRepository, Task<TResult>> work,
        CancellationToken ct = default)
    {
        // Déjà dans une transaction : on la réutilise, la validation reste à l'appelant le plus externe.
        if (_boundConnection != null) return await work(this);

        using var db = await _factory.OpenAsync(ct);
        using var tx = db.BeginTransaction();
        try
        {
            var result = await work(new CatalogRepository(this, db, tx));
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<T?> GetAsync<T>(string id, CancellationToken ct = default) where T : InfoBase
    {
        var map = MapOf<T>();
        using var lease = await LeaseAsync(ct);
        return await lease.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            $"SELECT * FROM \"{map.Table}\" WHERE \"Id\" = @id", new { id }, lease.Transaction,
            cancellationToken: ct));
    }

    public Task<IReadOnlyList<T>> GetAllAsync<T>(CancellationToken ct = default) where T : InfoBase
    {
        return GetAllAsync<T>([], ct);
    }

    public Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take, CancellationToken ct = default)
        where T : InfoBase
    {
        return GetPageAsync<T>(skip, take, [], ct);
    }

    public Task<int> CountAsync<T>(CancellationToken ct = default) where T : InfoBase
    {
        return CountAsync<T>([], ct);
    }

    /// <summary>Clause WHERE et paramètres des filtres ; les noms de colonnes doivent être des propriétés mappées.</summary>
    private static (string Where, DynamicParameters Parameters) BuildWhere(EntityMap map,
        IReadOnlyList<CatalogFilter> filters)
    {
        var parameters = new DynamicParameters();
        var clauses = new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            // Le nom de colonne est interpolé : il doit être une propriété mappée connue.
            var column = map.Columns.FirstOrDefault(c => c.Equals(filter.Property, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ArgumentException($"Propriété inconnue : {filter.Property}", nameof(filters));
            switch (filter.Mode)
            {
                case FilterMode.Contains:
                    clauses.Add($"LOWER(COALESCE(\"{column}\", '')) LIKE @p{i} ESCAPE '!'");
                    parameters.Add($"p{i}", "%" + filter.Value.Trim().ToLowerInvariant().Replace("!", "!!")
                        .Replace("%", "!%").Replace("_", "!_") + "%");
                    break;
                case FilterMode.In:
                    // Liste vide : aucun résultat (un IN () vide n'est pas du SQL valide).
                    if (filter.Values.Count == 0)
                    {
                        clauses.Add("1 = 0");
                        break;
                    }

                    clauses.Add($"\"{column}\" IN @p{i}");
                    parameters.Add($"p{i}", filter.Values.ToList());
                    break;
                case FilterMode.Bool:
                    if (!bool.TryParse(filter.Value, out var flag))
                        throw new GranitExceptions($"{filter.Property} doit valoir true ou false.");
                    clauses.Add($"\"{column}\" = @p{i}");
                    parameters.Add($"p{i}", flag);
                    break;
                default:
                    clauses.Add($"\"{column}\" = @p{i}");
                    parameters.Add($"p{i}", filter.Value);
                    break;
            }
        }

        return (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), parameters);
    }

    public async Task<IReadOnlyList<T>> GetAllAsync<T>(IReadOnlyList<CatalogFilter> filters,
        CancellationToken ct = default) where T : InfoBase
    {
        var map = MapOf<T>();
        var (where, parameters) = BuildWhere(map, filters);
        using var lease = await LeaseAsync(ct);
        return (await lease.Connection.QueryAsync<T>(new CommandDefinition(
            $"SELECT * FROM \"{map.Table}\"{where}", parameters, lease.Transaction, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<T>> GetPageAsync<T>(int skip, int take, IReadOnlyList<CatalogFilter> filters,
        CancellationToken ct = default) where T : InfoBase
    {
        var map = MapOf<T>();
        var order = map.Columns.Contains("Name") ? "\"Name\", \"Id\"" : "\"Id\"";
        var (where, parameters) = BuildWhere(map, filters);
        parameters.Add("skip", skip);
        parameters.Add("take", take);
        var sql = _factory.Provider == "Sqlite"
            ? $"SELECT * FROM \"{map.Table}\"{where} ORDER BY {order} LIMIT @take OFFSET @skip"
            : $"SELECT * FROM \"{map.Table}\"{where} ORDER BY {order} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        using var lease = await LeaseAsync(ct);
        return (await lease.Connection.QueryAsync<T>(new CommandDefinition(sql, parameters, lease.Transaction,
            cancellationToken: ct))).AsList();
    }

    public async Task<int> CountAsync<T>(IReadOnlyList<CatalogFilter> filters, CancellationToken ct = default)
        where T : InfoBase
    {
        var map = MapOf<T>();
        var (where, parameters) = BuildWhere(map, filters);
        using var lease = await LeaseAsync(ct);
        return await lease.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM \"{map.Table}\"{where}", parameters, lease.Transaction, cancellationToken: ct));
    }

    /// <summary>Champs dont l'historique garde la trace (ceux que présente l'entité).</summary>
    private static readonly string[] TrackedFields = ["Description", "MetaInfo", "IsSensitive"];

    private static Dictionary<string, string?> TrackedValues<T>(T entity)
    {
        var values = new Dictionary<string, string?>();
        foreach (var name in TrackedFields)
        {
            var property = typeof(T).GetProperty(name);
            if (property == null) continue;
            values[name] = property.GetValue(entity) switch
            {
                bool flag => flag ? "true" : "false",
                var other => other?.ToString()
            };
        }

        return values;
    }

    private static string KindOf<T>()
    {
        return typeof(T) == typeof(DatabaseInfo) ? "database"
            : typeof(T) == typeof(TableInfo) ? "table"
            : typeof(T) == typeof(ColumnInfo) ? "column"
            : typeof(T) == typeof(IndexInfo) ? "index"
            : typeof(T) == typeof(RelationInfo) ? "relation"
            : typeof(T) == typeof(DomainInfo) ? "domain"
            : typeof(T) == typeof(TagInfo) ? "tag"
            : typeof(T).Name.ToLowerInvariant();
    }

    public async Task<T?> ModifyAsync<T>(string id, Action<T> apply, CancellationToken ct = default,
        string? changedBy = null) where T : InfoBase
    {
        return await InTransactionAsync<T?>(async repo =>
        {
            var entity = await repo.GetAsync<T>(id, ct);
            if (entity == null) return null;
            var before = TrackedValues(entity);
            apply(entity);
            entity.Id = id;
            if (!await repo.UpdateAsync(id, entity, ct)) return null;

            var self = (CatalogRepository)repo;
            var now = DateTime.UtcNow;
            using var lease = await self.LeaseAsync(ct);
            var after = TrackedValues(entity);
            foreach (var (field, oldValue) in before)
            {
                var newValue = after[field];
                if (oldValue == newValue) continue;
                await lease.Connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO "History" ("Id", "EntityType", "EntityId", "Field", "OldValue", "NewValue", "ChangedAt", "ChangedBy")
                    VALUES (@Id, @EntityType, @EntityId, @Field, @OldValue, @NewValue, @ChangedAt, @ChangedBy)
                    """,
                    new
                    {
                        Id = Guid.NewGuid().ToString(), EntityType = KindOf<T>(), EntityId = id, Field = field,
                        OldValue = oldValue, NewValue = newValue, ChangedAt = now,
                        ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? null : changedBy.Trim()[..Math.Min(changedBy.Trim().Length, 255)]
                    }, lease.Transaction, cancellationToken: ct));
            }

            return entity;
        }, ct);
    }

    public async Task RecordHistoryAsync<T>(string entityId, string field, string? oldValue, string? newValue,
        string? changedBy = null, CancellationToken ct = default) where T : InfoBase
    {
        using var lease = await LeaseAsync(ct);
        await lease.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO "History" ("Id", "EntityType", "EntityId", "Field", "OldValue", "NewValue", "ChangedAt", "ChangedBy")
            VALUES (@Id, @EntityType, @EntityId, @Field, @OldValue, @NewValue, @ChangedAt, @ChangedBy)
            """,
            new
            {
                Id = Guid.NewGuid().ToString(), EntityType = KindOf<T>(), EntityId = entityId, Field = field,
                OldValue = oldValue, NewValue = newValue, ChangedAt = DateTime.UtcNow,
                ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? null : changedBy.Trim()[..Math.Min(changedBy.Trim().Length, 255)]
            }, lease.Transaction, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(string? entityId, int skip, int take,
        CancellationToken ct = default)
    {
        var where = entityId == null ? "" : " WHERE \"EntityId\" = @entityId";
        var sql = _factory.Provider == "Sqlite"
            ? $"SELECT * FROM \"History\"{where} ORDER BY \"ChangedAt\" DESC, \"Id\" LIMIT @take OFFSET @skip"
            : $"SELECT * FROM \"History\"{where} ORDER BY \"ChangedAt\" DESC, \"Id\" OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY";
        using var lease = await LeaseAsync(ct);
        return (await lease.Connection.QueryAsync<HistoryEntry>(new CommandDefinition(sql,
            new { entityId, skip, take }, lease.Transaction, cancellationToken: ct))).AsList();
    }

    public async Task<int> CountHistoryAsync(string? entityId, CancellationToken ct = default)
    {
        var where = entityId == null ? "" : " WHERE \"EntityId\" = @entityId";
        using var lease = await LeaseAsync(ct);
        return await lease.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM \"History\"{where}", new { entityId }, lease.Transaction, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string term, int limitPerKind,
        CancellationToken ct = default)
    {
        var pattern = "%" + term.Trim().ToLowerInvariant().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
        string Match(string column) => $"LOWER(COALESCE({column}, '')) LIKE @pattern ESCAPE '!'";
        string Limit(string order) => _factory.Provider == "Sqlite"
            ? $" ORDER BY {order} LIMIT @limit"
            : $" ORDER BY {order} OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY";

        var queries = new[]
        {
            // Chaque requête est entourée d'un SELECT pour pouvoir y appliquer son propre tri et sa limite.
            "SELECT * FROM (SELECT 'database' AS Kind, d.\"Id\" AS Id, d.\"Name\" AS Name, d.\"Description\" AS Description, " +
            "d.\"Id\" AS DatabaseId, d.\"Name\" AS DatabaseName, NULL AS TableId, NULL AS TableName " +
            $"FROM \"Databases\" d WHERE {Match("d.\"Name\"")} OR {Match("d.\"Description\"")}) q" + Limit("q.Name, q.Id"),

            "SELECT * FROM (SELECT 'table' AS Kind, t.\"Id\" AS Id, t.\"Name\" AS Name, t.\"Description\" AS Description, " +
            "t.\"DatabaseId\" AS DatabaseId, d.\"Name\" AS DatabaseName, t.\"Id\" AS TableId, t.\"Name\" AS TableName " +
            "FROM \"Tables\" t JOIN \"Databases\" d ON d.\"Id\" = t.\"DatabaseId\" " +
            $"WHERE {Match("t.\"Name\"")} OR {Match("t.\"Description\"")}) q" + Limit("q.Name, q.Id"),

            "SELECT * FROM (SELECT 'column' AS Kind, c.\"Id\" AS Id, c.\"Name\" AS Name, c.\"Description\" AS Description, " +
            "c.\"DatabaseId\" AS DatabaseId, d.\"Name\" AS DatabaseName, c.\"TableId\" AS TableId, t.\"Name\" AS TableName " +
            "FROM \"Columns\" c JOIN \"Tables\" t ON t.\"Id\" = c.\"TableId\" JOIN \"Databases\" d ON d.\"Id\" = c.\"DatabaseId\" " +
            $"WHERE {Match("c.\"Name\"")} OR {Match("c.\"Description\"")}) q" + Limit("q.Name, q.Id"),

            "SELECT * FROM (SELECT 'index' AS Kind, i.\"Id\" AS Id, i.\"Name\" AS Name, i.\"Description\" AS Description, " +
            "i.\"DatabaseId\" AS DatabaseId, d.\"Name\" AS DatabaseName, i.\"TableId\" AS TableId, t.\"Name\" AS TableName " +
            "FROM \"Indexes\" i JOIN \"Tables\" t ON t.\"Id\" = i.\"TableId\" JOIN \"Databases\" d ON d.\"Id\" = i.\"DatabaseId\" " +
            $"WHERE {Match("i.\"Name\"")} OR {Match("i.\"ColumnNames\"")} OR {Match("i.\"Description\"")}) q" + Limit("q.Name, q.Id")
        };

        using var lease = await LeaseAsync(ct);
        var hits = new List<SearchHit>();
        foreach (var sql in queries)
            hits.AddRange(await lease.Connection.QueryAsync<SearchHit>(new CommandDefinition(sql,
                new { pattern, limit = limitPerKind }, lease.Transaction, cancellationToken: ct)));
        return hits;
    }

    public async Task<IReadOnlyList<T>> FindAsync<T>(string property, string value, CancellationToken ct = default)
        where T : InfoBase
    {
        var map = MapOf<T>();
        // Le nom de colonne est interpolé : il doit être une propriété mappée connue.
        var column = map.Columns.FirstOrDefault(c => c.Equals(property, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException($"Propriété inconnue : {property}", nameof(property));
        using var lease = await LeaseAsync(ct);
        return (await lease.Connection.QueryAsync<T>(new CommandDefinition(
            $"SELECT * FROM \"{map.Table}\" WHERE \"{column}\" = @value", new { value }, lease.Transaction,
            cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<RelationInfo>> GetRelationsOfDatabaseAsync(string databaseId,
        CancellationToken ct = default)
    {
        using var lease = await LeaseAsync(ct);
        var relations = (await lease.Connection.QueryAsync<RelationInfo>(new CommandDefinition(
            """
            SELECT * FROM "Relations"
            WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @databaseId)
               OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @databaseId)
            """, new { databaseId }, lease.Transaction, cancellationToken: ct))).AsList();
        var extras = (await lease.Connection.QueryAsync<RelationColumnInfo>(new CommandDefinition(
            """
            SELECT * FROM "RelationColumns"
            WHERE "RelationId" IN (SELECT "Id" FROM "Relations"
                WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @databaseId))
            ORDER BY "Position"
            """, new { databaseId }, lease.Transaction, cancellationToken: ct))).ToLookup(e => e.RelationId);
        foreach (var relation in relations)
            relation.ExtraColumns = extras[relation.Id].Select(e => new RelationColumnPair(e.FirstId, e.SecondId)).ToList();
        return relations;
    }

    public async Task<T> CreateAsync<T>(T info, CancellationToken ct = default) where T : InfoBase
    {
        var map = MapOf<T>();
        if (string.IsNullOrWhiteSpace(info.Id))
            info.Id = Guid.NewGuid().ToString();
        var cols = string.Join(", ", map.Columns.Select(c => $"\"{c}\""));
        var vals = string.Join(", ", map.Columns.Select(c => $"@{c}"));
        using var lease = await LeaseAsync(ct);
        await lease.Connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO \"{map.Table}\" ({cols}) VALUES ({vals})", info, lease.Transaction,
            cancellationToken: ct));
        return info;
    }

    public async Task<bool> UpdateAsync<T>(string id, T info, CancellationToken ct = default) where T : InfoBase
    {
        if (id != info.Id)
            throw new IdMisMatchException();
        var map = MapOf<T>();
        var set = string.Join(", ", map.Columns.Where(c => c != "Id").Select(c => $"\"{c}\" = @{c}"));
        using var lease = await LeaseAsync(ct);
        var rows = await lease.Connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE \"{map.Table}\" SET {set} WHERE \"Id\" = @Id", info, lease.Transaction,
            cancellationToken: ct));
        return rows > 0;
    }

    public async Task<bool> DeleteAsync<T>(string id, CancellationToken ct = default) where T : InfoBase
    {
        var map = MapOf<T>();

        string[] cascade;
        if (typeof(T) == typeof(DatabaseInfo))
            cascade =
            [
                """DELETE FROM "DomainHiddenRelations" WHERE "DomainId" IN (SELECT "Id" FROM "Domains" WHERE "DatabaseId" = @id)""",
                """DELETE FROM "DomainTables" WHERE "DomainId" IN (SELECT "Id" FROM "Domains" WHERE "DatabaseId" = @id)""",
                """DELETE FROM "Domains" WHERE "DatabaseId" = @id""",
                """
                DELETE FROM "TagLinks" WHERE ("EntityType" = 'database' AND "EntityId" = @id)
                    OR ("EntityType" = 'table' AND "EntityId" IN (SELECT "Id" FROM "Tables" WHERE "DatabaseId" = @id))
                    OR ("EntityType" = 'column' AND "EntityId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @id))
                """,
                """DELETE FROM "RelationColumns" WHERE "RelationId" IN (SELECT "Id" FROM "Relations" WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @id) OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @id))""",
                """DELETE FROM "Relations" WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @id) OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "DatabaseId" = @id)""",
                """DELETE FROM "Indexes" WHERE "DatabaseId" = @id""",
                """DELETE FROM "Columns" WHERE "DatabaseId" = @id""",
                """DELETE FROM "Tables" WHERE "DatabaseId" = @id"""
            ];
        else if (typeof(T) == typeof(TableInfo))
            cascade =
            [
                """DELETE FROM "DomainHiddenRelations" WHERE "RelationId" IN (SELECT "Id" FROM "Relations" WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id) OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id))""",
                """DELETE FROM "DomainTables" WHERE "TableId" = @id""",
                """
                DELETE FROM "TagLinks" WHERE ("EntityType" = 'table' AND "EntityId" = @id)
                    OR ("EntityType" = 'column' AND "EntityId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id))
                """,
                """DELETE FROM "RelationColumns" WHERE "RelationId" IN (SELECT "Id" FROM "Relations" WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id) OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id))""",
                """DELETE FROM "Relations" WHERE "FirstId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id) OR "SecondId" IN (SELECT "Id" FROM "Columns" WHERE "TableId" = @id)""",
                """DELETE FROM "Indexes" WHERE "TableId" = @id""",
                """DELETE FROM "Columns" WHERE "TableId" = @id"""
            ];
        else if (typeof(T) == typeof(ColumnInfo))
            cascade =
            [
                """DELETE FROM "DomainHiddenRelations" WHERE "RelationId" IN (SELECT "Id" FROM "Relations" WHERE "FirstId" = @id OR "SecondId" = @id)""",
                """DELETE FROM "TagLinks" WHERE "EntityType" = 'column' AND "EntityId" = @id""",
                """DELETE FROM "RelationColumns" WHERE "RelationId" IN (SELECT "Id" FROM "Relations" WHERE "FirstId" = @id OR "SecondId" = @id)""",
                """DELETE FROM "RelationColumns" WHERE "FirstId" = @id OR "SecondId" = @id""",
                """DELETE FROM "Relations" WHERE "FirstId" = @id OR "SecondId" = @id"""
            ];
        else if (typeof(T) == typeof(DomainInfo))
            cascade =
            [
                """DELETE FROM "DomainHiddenRelations" WHERE "DomainId" = @id""",
                """DELETE FROM "DomainTables" WHERE "DomainId" = @id"""
            ];
        else if (typeof(T) == typeof(RelationInfo))
            cascade =
            [
                """DELETE FROM "DomainHiddenRelations" WHERE "RelationId" = @id""",
                """DELETE FROM "RelationColumns" WHERE "RelationId" = @id"""
            ];
        else if (typeof(T) == typeof(TagInfo))
            cascade = ["""DELETE FROM "TagLinks" WHERE "TagId" = @id"""];
        else
            cascade = [];

        // La suppression et ses dépendances sont atomiques, dans la transaction en cours ou dans la leur.
        return await InTransactionAsync(async repo =>
        {
            var self = (CatalogRepository)repo;
            using var lease = await self.LeaseAsync(ct);
            foreach (var sql in cascade)
                await lease.Connection.ExecuteAsync(new CommandDefinition(sql, new { id }, lease.Transaction,
                    cancellationToken: ct));
            var rows = await lease.Connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM \"{map.Table}\" WHERE \"Id\" = @id", new { id }, lease.Transaction,
                cancellationToken: ct));
            return rows > 0;
        }, ct);
    }
}
