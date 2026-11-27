using GranitDB.API.Connectors.H2;
using GranitDB.API.Exceptions;

namespace GranitDB.API.Connectors;

/// <summary>
///     Lit le schéma d'un fichier H2 (<c>.mv.db</c>, H2 2.x) directement, sans Java ni jar : <see cref="H2Store" /> retrouve les
///     instructions SQL que H2 garde dans le fichier et <see cref="H2Ddl" /> en tire le schéma. Rien de la base n'est exécuté,
///     le fichier est ouvert en lecture seule et son mot de passe n'est pas nécessaire (il protège la connexion H2, pas le
///     contenu d'un fichier non chiffré). Les types des colonnes d'une vue ne sont pas conservés par H2 (<c>ANY</c>).
/// </summary>
public class H2SchemaConnector : ISchemaConnector
{
    public const string TypeName = "H2";

    public string Type => TypeName;

    public Task<SourceSchema> ReadSchemaAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidSourceException($"Fichier H2 introuvable : {source}");
        return Task.Run(() =>
        {
            var name = Path.GetFileName(source);
            name = name.EndsWith(".mv.db", StringComparison.OrdinalIgnoreCase) ? name[..^6] : Path.GetFileNameWithoutExtension(name);
            return H2Ddl.Build(name, TypeName, H2Store.ReadStatements(source, ct));
        }, ct);
    }
}
