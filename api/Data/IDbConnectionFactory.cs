using System.Data;

namespace GranitDB.API.Data;

/// <summary>Ouvre des connexions vers le catalogue (SQLite ou SQL Server).</summary>
public interface IDbConnectionFactory
{
    string Provider { get; }

    /// <summary>Retourne une connexion déjà ouverte ; l'appelant la libère.</summary>
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);
}
