using MySqlConnector;
using Npgsql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace GranitDb.Tests;

[CollectionDefinition(Name)]
public class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}

[CollectionDefinition(Name)]
public class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "MySql";
}

/// <summary>Un conteneur PostgreSQL partagé ; une base par test.</summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string User => "postgres";
    public string Password => "granit_pg_pwd";

    public async Task InitializeAsync()
    {
        if (!DockerSupport.IsAvailable && !DockerSupport.Required) return;
        _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").WithUsername(User).WithPassword(Password).Build();
        await _container.StartAsync();
        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(5432);
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    private string Connection(string database, bool pooling = false) => new NpgsqlConnectionStringBuilder
        { Host = Host, Port = Port, Username = User, Password = Password, Database = database, Pooling = pooling }.ConnectionString;

    /// <summary>Crée une base au nom unique, y exécute le SQL donné et renvoie son nom.</summary>
    public async Task<string> NewDatabaseAsync(params string[] statements)
    {
        var name = "granit_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync("postgres", $"CREATE DATABASE {name}");
        await ExecuteAsync(name, statements);
        return name;
    }

    public async Task ExecuteAsync(string database, params string[] statements)
    {
        await using var db = new NpgsqlConnection(Connection(database));
        await db.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Saisie de connexion vers une base de test.</summary>
    public GranitDB.API.Models.Inputs.ServerConnectionInput Input(string database, string? password = null) => new()
    {
        Engine = "PostgreSql", Server = Host, Port = Port, Database = database, User = User, Password = password ?? Password,
        Encrypt = false, TimeoutSeconds = 15
    };
}

/// <summary>Un conteneur MySQL partagé ; une base par test.</summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private MySqlContainer? _container;

    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public string User => "root";
    public string Password => "granit_my_pwd";

    public async Task InitializeAsync()
    {
        if (!DockerSupport.IsAvailable && !DockerSupport.Required) return;
        _container = new MySqlBuilder().WithImage("mysql:8.4").WithUsername(User).WithPassword(Password).Build();
        await _container.StartAsync();
        Host = _container.Hostname;
        Port = _container.GetMappedPublicPort(3306);
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }

    private string Connection(string? database) => new MySqlConnectionStringBuilder
        { Server = Host, Port = (uint)Port, UserID = User, Password = Password, Database = database ?? "", Pooling = false }.ConnectionString;

    public async Task<string> NewDatabaseAsync(params string[] statements)
    {
        var name = "granit_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(null, $"CREATE DATABASE {name}");
        await ExecuteAsync(name, statements);
        return name;
    }

    public async Task ExecuteAsync(string? database, params string[] statements)
    {
        await using var db = new MySqlConnection(Connection(database));
        await db.OpenAsync();
        foreach (var sql in statements)
        {
            await using var cmd = new MySqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public GranitDB.API.Models.Inputs.ServerConnectionInput Input(string database, string? password = null) => new()
    {
        Engine = "MySql", Server = Host, Port = Port, Database = database, User = User, Password = password ?? Password,
        Encrypt = false, TimeoutSeconds = 15
    };
}
