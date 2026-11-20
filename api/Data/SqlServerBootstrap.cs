using Dapper;
using Microsoft.Data.SqlClient;

namespace GranitDB.API.Data;

/// <summary>
///     Prépare un serveur SQL Server pour le catalogue : attend qu'il réponde (utile au démarrage d'un conteneur)
///     et crée la base indiquée dans la chaîne de connexion si elle n'existe pas.
/// </summary>
public static class SqlServerBootstrap
{
    /// <summary>Numéro d'erreur SQL Server « Cannot open database requested by the login ».</summary>
    private const int DatabaseMissing = 4060;

    public static async Task EnsureDatabaseAsync(string connectionString, int maxAttempts = 30,
        TimeSpan? delay = null, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database)) return;
        var wait = delay ?? TimeSpan.FromSeconds(2);

        // Sondes hors pool : SqlClient mémorise pendant quelques secondes l'échec de connexion d'un pool
        // (PoolBlockingPeriod), ce qui ferait échouer l'ouverture juste après la création de la base.
        // ConnectRetryCount = 0 : sinon SqlClient retente en silence (10 s) une base absente avant de signaler l'erreur 4060.
        var probe = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0 }
            .ConnectionString;

        for (var attempt = 1;; attempt++)
        {
            try
            {
                await using var db = new SqlConnection(probe);
                await db.OpenAsync(ct);
                return;
            }
            catch (SqlException e) when (e.Number == DatabaseMissing)
            {
                await CreateDatabaseAsync(builder, database, ct);
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(wait, ct); // serveur pas encore prêt
            }
        }
    }

    private static async Task CreateDatabaseAsync(SqlConnectionStringBuilder builder, string database,
        CancellationToken ct)
    {
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using var db = new SqlConnection(master.ConnectionString);
        await db.OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition(
            "IF DB_ID(@name) IS NULL EXEC('CREATE DATABASE ' + @quoted)",
            new { name = database, quoted = "[" + database.Replace("]", "]]") + "]" }, cancellationToken: ct));
    }
}
