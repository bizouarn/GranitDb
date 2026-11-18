using System.Diagnostics;
using Dapper;
using GranitDB.API.Data;
using GranitDB.API.Options;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace GranitDb.Tests;

/// <summary>
///     Détecte Docker. Sans Docker les tests SQL Server sont ignorés, sauf si GRANITDB_REQUIRE_SQLSERVER=1
///     (positionné par la CI) : ils échouent alors au lieu de passer sous silence.
/// </summary>
public static class DockerSupport
{
    private static readonly Lazy<bool> Available = new(Probe);

    public static bool Required => Environment.GetEnvironmentVariable("GRANITDB_REQUIRE_SQLSERVER") == "1";

    public static bool IsAvailable => Available.Value;

    public static string? SkipReason => IsAvailable || Required
        ? null
        : "Docker n'est pas disponible : tests SQL Server ignorés (GRANITDB_REQUIRE_SQLSERVER=1 pour les exiger).";

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) return false;
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Équivalent de [Fact] ignoré automatiquement quand Docker est absent.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        Skip = DockerSupport.SkipReason;
    }
}

[CollectionDefinition(Name)]
public class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}

/// <summary>Un seul conteneur SQL Server partagé par tous les tests SQL Server ; une base par test.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public async Task InitializeAsync()
    {
        if (!DockerSupport.IsAvailable && !DockerSupport.Required) return;
        _container = new MsSqlBuilder().WithImage("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    /// <summary>Chaîne de connexion vers une base (pas encore créée) au nom unique.</summary>
    public string NewDatabaseConnectionString(string prefix = "granit")
    {
        var builder = new SqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            InitialCatalog = $"{prefix}_{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
    }

    public CatalogOptions NewCatalogOptions(string? connectionString = null)
    {
        return new CatalogOptions
        {
            Provider = "SqlServer",
            ConnectionString = connectionString ?? NewDatabaseConnectionString(),
            DataDirectory = Path.Combine(Path.GetTempPath(), "granitdb-tests", Guid.NewGuid().ToString("N"))
        };
    }

    public static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        foreach (var sql in statements) await db.ExecuteAsync(sql);
    }

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        return (await db.ExecuteScalarAsync<T>(sql))!;
    }

    public static CatalogRepository CreateRepository(CatalogOptions options)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        return new CatalogRepository(new DbConnectionFactory(wrapped), wrapped);
    }
}
