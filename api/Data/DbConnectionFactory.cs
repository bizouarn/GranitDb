using System.Data;
using System.Data.Common;
using GranitDB.API.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Data;

public class DbConnectionFactory(IOptions<CatalogOptions> options) : IDbConnectionFactory
{
    private readonly CatalogOptions _options = options.Value;

    public string Provider => _options.IsSqlServer ? "SqlServer" : "Sqlite";

    public async Task<IDbConnection> OpenAsync(CancellationToken ct = default)
    {
        DbConnection connection;
        if (_options.IsSqlite)
        {
            connection = new SqliteConnection(_options.ResolveConnectionString());
            await connection.OpenAsync(ct);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }
        else if (_options.IsSqlServer)
        {
            connection = new SqlConnection(_options.ResolveConnectionString());
            await connection.OpenAsync(ct);
        }
        else
        {
            throw new InvalidOperationException(
                $"Fournisseur de catalogue inconnu : '{_options.Provider}' (Sqlite ou SqlServer).");
        }

        return connection;
    }
}
