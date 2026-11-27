# Déploiement, tests et maintenance

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) pour lancer sans Docker.
- Docker, facultatif : pile complète via `docker compose`, catalogue SQL Server, tests SQL Server / PostgreSQL / MySQL.

## Sans Docker

```bash
dotnet run --project api --launch-profile http   # API sur http://localhost:5105 (Swagger : /swagger)
dotnet run --project client                      # interface sur http://localhost:5028
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

## Images publiées

La poussée d'un tag Git commençant par `v` déclenche `.github/workflows/release.yml` : la CI complète est rejouée sur le commit tagué puis, si elle réussit, les deux images (`linux/amd64`) sont construites et publiées sur GitHub Container Registry :

- `ghcr.io/bizouarn/granitdb-api`
- `ghcr.io/bizouarn/granitdb-client`

### Publier une version

Le workflow doit être présent dans le commit tagué. Depuis ce commit (en général `main` à jour) :

```bash
git tag -a v1.2.0 -m "v1.2.0"
git push origin v1.2.0
```

L'avancement se suit dans l'onglet *Actions* du dépôt. Les étiquettes des images sont déduites du tag :

| Tag Git | Étiquettes publiées |
|---|---|
| `v1.2.0` | `v1.2.0`, `1.2.0`, `1.2`, `latest` |
| `v2.0.0-rc.1` (préversion : le nom contient `-`) | `v2.0.0-rc.1`, `2.0.0-rc.1` |
| `v2` (pas une version complète `X.Y.Z`) | `v2`, `latest` |

`latest` désigne le dernier tag publié, hors préversions, et non le plus grand numéro : publier un correctif d'une ancienne branche (`v1.2.1` après `v2.0.0`) le fait revenir en arrière. Poussez les tags un par un : au-delà de trois tags dans une même poussée, GitHub ne déclenche aucun workflow.

Si un job échoue (une seule des deux images peut alors avoir été publiée), relancez les jobs en échec depuis *Actions*. Si le code doit être corrigé, publiez un nouveau numéro plutôt que de déplacer le tag.

Aucun secret n'est à créer : le workflow utilise le `GITHUB_TOKEN`, avec `packages: write` pour le seul job de publication.

### Visibilité des paquets

Après la première publication, vérifiez la visibilité de chaque paquet (profil GitHub → *Packages* → le paquet → *Package settings*) : un nouveau paquet est en principe **privé**. Pour permettre de le récupérer sans compte, passez-le en *Public* (GitHub ne permet pas de revenir ensuite en privé).

Pour un paquet privé, connectez-vous d'abord avec un jeton GitHub (*classic*) ayant la portée `read:packages` ; il est demandé à la place du mot de passe :

```bash
docker login ghcr.io -u <votre-login-github>
```

Si les paquets existent déjà sans être liés au dépôt (publiés à la main auparavant), donnez au dépôt le rôle *Write* dans *Package settings* → *Manage Actions access*, sinon la publication est refusée.

### Récupérer et lancer les images

```bash
docker pull ghcr.io/bizouarn/granitdb-api:1.2.0
docker pull ghcr.io/bizouarn/granitdb-client:1.2.0
```

Pour les lancer avec les réglages de `docker-compose.yml`, créez à côté de lui un fichier `compose.ghcr.yml` :

```yaml
services:
  api:
    image: ghcr.io/bizouarn/granitdb-api:${GRANITDB_VERSION:-latest}
  client:
    image: ghcr.io/bizouarn/granitdb-client:${GRANITDB_VERSION:-latest}
```

puis, sans rien construire (la version se choisit avec `GRANITDB_VERSION`, dans `.env` par exemple) :

```bash
docker compose -f docker-compose.yml -f compose.ghcr.yml pull api client
docker compose -f docker-compose.yml -f compose.ghcr.yml up -d --no-build
```

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

Les tests SQLite n'ont besoin d'aucun service externe. Les tests SQL Server, PostgreSQL et MySQL (migrations, dépôt, connecteurs) démarrent un conteneur via Testcontainers : ils sont ignorés si Docker est absent, sauf avec `GRANITDB_REQUIRE_SQLSERVER=1` (positionné par la CI). La première exécution télécharge l'image SQL Server (≈ 1,5 Go). Les tests H2 lisent des fichiers de référence (`tests/GranitDb.Tests/Fixtures/h2`) : Java n'est pas nécessaire.

## Intégration continue

`.github/workflows/ci.yml` : build de la solution, tests (SQL Server compris, via Testcontainers) puis construction des deux images Docker et démarrage de la pile avec `docker compose`. Elle est aussi rejouée avant chaque [publication d'images](#images-publiées).

## Structure du dépôt

| Dossier | Contenu |
|---|---|
| `api/` | API ASP.NET Core (contrôleurs, accès au catalogue Dapper, connecteurs de sources, migrations) |
| `client/` | Interface Blazor WebAssembly + MudBlazor (PWA) |
| `tests/GranitDb.Tests` | Tests xUnit (API, synchronisation, pages bUnit) |
| `docs/` | Documentation |
| `work-doc/` | État des lieux et feuille de route |

L'architecture détaillée et les conventions sont dans [`CLAUDE.md`](../CLAUDE.md).

## En-têtes de sécurité

L'image du client sert une `Content-Security-Policy` stricte (scripts de l'application seulement, `wasm-unsafe-eval` pour Blazor, aucun `eval`, pas d'encadrement dans une page tierce). L'adresse autorisée pour les appels à l'API (`connect-src`) est celle de `API_BASE_URL`, réduite à son origine : elle est calculée au démarrage du conteneur par `docker-entrypoint.sh`. L'API n'accepte que les noms d'hôte de `AllowedHosts` (`localhost` par défaut ; `ALLOWED_HOSTS` en Docker, à étendre derrière un nom de domaine). HSTS est à poser sur le proxy TLS.
