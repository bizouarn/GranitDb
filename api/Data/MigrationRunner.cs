using System.Data;
using Dapper;

namespace GranitDB.API.Data;

public record MigrationReport(int FromVersion, int ToVersion, IReadOnlyList<int> Applied);

/// <summary>
///     Applique les migrations en attente, chacune dans sa transaction, et mémorise la version atteinte dans la
///     table <c>SchemaVersion</c>. Un catalogue déjà à jour n'est pas modifié.
/// </summary>
public class MigrationRunner(IDbConnectionFactory factory, IReadOnlyList<Migration>? migrations = null)
{
    public const string TableName = "SchemaVersion";

    private readonly IReadOnlyList<Migration> _migrations =
        (migrations ?? CatalogMigrations.All).OrderBy(m => m.Version).ToList();

    private bool IsSqlite => factory.Provider == "Sqlite";

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public async Task<int> CurrentVersionAsync(CancellationToken ct = default)
    {
        using var db = await factory.OpenAsync(ct);
        await EnsureVersionTableAsync(db, ct);
        return await ReadVersionAsync(db, ct);
    }

    public async Task<MigrationReport> MigrateAsync(CancellationToken ct = default)
    {
        using var db = await factory.OpenAsync(ct);
        await EnsureVersionTableAsync(db, ct);
        var current = await ReadVersionAsync(db, ct);

        if (current > LatestVersion)
            throw new InvalidOperationException(
                $"Le catalogue est en version {current}, plus récente que celle connue par cette application " +
                $"({LatestVersion}) : mettez l'application à jour.");

        var applied = new List<int>();
        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            using var tx = db.BeginTransaction();
            try
            {
                foreach (var sql in IsSqlite ? migration.Sqlite : migration.SqlServer)
                    await db.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
                await db.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO {TableName} (Version, Name, AppliedAt) VALUES (@Version, @Name, @AppliedAt)",
                    new { migration.Version, migration.Name, AppliedAt = DateTime.UtcNow.ToString("O") },
                    tx, cancellationToken: ct));
                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                // Une autre instance a pu appliquer cette migration en même temps : ce n'est pas une erreur.
                if (await ReadVersionAsync(db, ct) >= migration.Version) continue;
                throw new InvalidOperationException(
                    $"Échec de la migration {migration.Version} ({migration.Name}) : {e.Message}", e);
            }

            applied.Add(migration.Version);
        }

        return new MigrationReport(current, applied.Count > 0 ? applied[^1] : current, applied);
    }

    private async Task EnsureVersionTableAsync(IDbConnection db, CancellationToken ct)
    {
        var sql = IsSqlite
            ? $"CREATE TABLE IF NOT EXISTS {TableName} (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedAt TEXT NOT NULL)"
            : $"IF OBJECT_ID(N'dbo.{TableName}', N'U') IS NULL CREATE TABLE dbo.{TableName} " +
              "(Version INT NOT NULL PRIMARY KEY, Name NVARCHAR(200) NOT NULL, AppliedAt NVARCHAR(40) NOT NULL)";
        await db.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    private static async Task<int> ReadVersionAsync(IDbConnection db, CancellationToken ct)
    {
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COALESCE(MAX(Version), 0) FROM {TableName}", cancellationToken: ct));
    }
}
