# Déploiement, tests et maintenance

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) pour lancer sans Docker.
- Docker, facultatif : pile complète via `docker compose`, catalogue SQL Server, tests SQL Server / PostgreSQL / MySQL.

## Sans Docker

```bash
dotnet run --project api --launch-profile http   # API sur http://localhost:5105 (Swagger : /swagger)
dotnet run --project client                      # interface sur http://localhost:41248
```

## Docker

```bash
docker compose up --build            # interface http://localhost:8081, API http://localhost:5105/swagger
```

Le catalogue SQLite est conservé dans le volume `granitdb-data`. Pour utiliser SQL Server comme catalogue :

```bash
cp .env.example .env
docker compose --profile sqlserver up --build
```

L'API crée la base `GranitDb` si elle n'existe pas et attend que le serveur réponde (jusqu'à 60 s). L'URL de l'API vue par le navigateur se règle avec `API_BASE_URL`. Les deux Dockerfiles utilisent la racine du dépôt comme contexte de build. Le profil `ai` ajoute un modèle local : voir [aides-documentation.md](aides-documentation.md).

Variables courantes : `API_BASE_URL`, `ALLOW_SQLSERVER_SOURCES`, `ALLOW_AI`, `AI_MODEL` ; derrière un proxy, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.

## Sauvegarde

À sauvegarder ensemble : le catalogue (`granitdb-data/`, ou la base SQL Server), les fichiers sources conservés (`sources/`) et **les clés** (`keys/`), sans lesquelles les connexions enregistrées ne sont plus lisibles.

## Migrations du catalogue

Le schéma du catalogue est versionné (`api/Data/CatalogMigrations.cs`, version dans la table `SchemaVersion`) et mis à jour automatiquement au démarrage, en SQLite comme en SQL Server, y compris pour un catalogue créé avant l'introduction des migrations. Chaque migration s'applique dans sa propre transaction, plusieurs démarrages simultanés sont tolérés.

- Une migration déjà publiée ne se modifie jamais : on en ajoute une.
- Un catalogue plus récent que l'application est refusé.

## Tests

```bash
dotnet test tests/GranitDb.Tests
dotnet test tests/GranitDb.Tests --filter "FullyQualifiedName~SchemaSynchronizerTests"   # une classe
```

Les tests SQLite n'ont besoin d'aucun service externe. Les tests SQL Server, PostgreSQL et MySQL (migrations, dépôt, connecteurs) démarrent un conteneur via Testcontainers : ils sont ignorés si Docker est absent, sauf avec `GRANITDB_REQUIRE_SQLSERVER=1` (positionné par la CI). La première exécution télécharge l'image SQL Server (≈ 1,5 Go).

## Intégration continue

`.github/workflows/ci.yml` : build de la solution, tests (SQL Server compris, via Testcontainers) puis construction des deux images Docker et démarrage de la pile avec `docker compose`.

## Structure du dépôt

| Dossier | Contenu |
|---|---|
| `api/` | API ASP.NET Core (contrôleurs, accès au catalogue Dapper, connecteurs de sources, migrations) |
| `client/` | Interface Blazor WebAssembly + MudBlazor (PWA) |
| `tests/GranitDb.Tests` | Tests xUnit (API, synchronisation, pages bUnit) |
| `docs/` | Documentation |
| `work-doc/` | État des lieux et feuille de route |

L'architecture détaillée et les conventions sont dans [`CLAUDE.md`](../CLAUDE.md).
