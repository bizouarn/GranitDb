# GranitDb
### 🚧 A poc under construction

Catalogue de métadonnées de bases de données : on décrit bases, tables, colonnes et relations, et on les consulte via une API REST et une interface Blazor.

**Aucun serveur de base de données n'est nécessaire** : le catalogue est un fichier SQLite et une base à documenter peut être un simple fichier SQLite.

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pour lancer sans Docker)
- Docker, facultatif : pile complète via `docker compose`, catalogue SQL Server, tests SQL Server

## Structure du dépôt

| Dossier | Contenu |
|---|---|
| `api/` | API ASP.NET Core (contrôleurs, accès au catalogue Dapper, connecteurs de sources, migrations) |
| `client/` | Interface Blazor WebAssembly + MudBlazor (PWA) |
| `interfaces/` | Interfaces `I*Info` partagées |
| `tests/GranitDb.Tests` | Tests xUnit (API, synchronisation, pages bUnit) |
| `work-doc/` | État des lieux et feuille de route |

## Démarrage rapide

```bash
dotnet run --project api --launch-profile http   # API sur http://localhost:5105 (Swagger : /swagger)
dotnet run --project client                      # interface sur http://localhost:41248
```

Ouvrez l'interface, glissez un fichier `.db` / `.sqlite` sur la zone de dépôt : tables, colonnes, types, clés et relations (clés étrangères) sont extraits et affichés. Le fichier source est ouvert en lecture seule.

Avec l'API seule :

```bash
curl -F "file=@ma_base.db" -F "name=Ma base" http://localhost:5105/api/v1/import/sqlite
curl http://localhost:5105/api/v1/DatabaseInfo
```

## API

Toutes les routes sont sous **`/api/v1`** ; les anciennes routes sans préfixe (`/DatabaseInfo`, …) restent acceptées comme alias.

| Route | Rôle |
|---|---|
| `GET/POST/PUT/DELETE /api/v1/DatabaseInfo`, `/TableInfo`, `/ColumnInfo`, `/RelationInfo` | CRUD sur les objets du catalogue |
| `GET /api/v1/TableInfo/database/{id}` | Tables d'une base, avec colonnes, index et relations (filtres `name`, `tableType`, pagination) |
| `GET /api/v1/TableInfo/{id}/columns`, `/indexes`, `/relations` | Colonnes (dans l'ordre), index et relations d'une table |
| `GET /api/v1/Impact/table/{id}`, `/Impact/column/{id}` | Analyse d'impact : ce qui référence l'élément (en chaîne) et ce qu'il référence |
| `PUT /api/v1/{DatabaseInfo|TableInfo|ColumnInfo|RelationInfo}/{id}/documentation` | Modifie la description et les notes (`metaInfo`) d'un élément ; un texte vide l'efface |
| `PUT /api/v1/ColumnInfo/{id}/sensitivity` | Marque ou démarque une colonne comme contenant des données sensibles |
| `POST /api/v1/RelationInfo` | Crée une relation manuelle (1-1, 1-N, N-N) ; les relations détectées par la synchronisation sont corrigeables (`PUT`) mais pas supprimables |
| `GET /api/v1/DomainInfo/database/{id}`, `POST/PUT/DELETE /api/v1/DomainInfo` | Domaines d'une base et leurs tables |
| `PUT/DELETE /api/v1/DomainInfo/{id}/tables/{tableId}`, `PUT /api/v1/DomainInfo/{id}/layout` | Range une table dans un domaine (`?moveFrom=` la retire d'un autre), la retire, mémorise la place des tables |
| `POST /api/v1/TagInfo/attach`, `DELETE /api/v1/TagInfo/{tagId}/links/{type}/{entityId}`, `GET /api/v1/TagInfo/database/{id}/links` | Étiquettes (base, table, colonne) |
| `GET /api/v1/Completeness/database/{id}?domainId=` | Part des tables et colonnes qui ont une description |
| `POST /api/v1/DatabaseInfo/{id}/sync/preview` | Aperçu d'une synchronisation : ce qui serait ajouté, modifié, supprimé, et ce qui ferait perdre une documentation |
| `GET /api/v1/History?entityId=` | Historique des modifications (description, notes, marquage sensible) |
| `POST /api/v1/import/sqlite`, `PUT /api/v1/import/sqlite/{id}` | Import / ré-import d'un fichier SQLite |
| `POST /api/v1/import/sqlite/local` | Import par chemin serveur (si `AllowLocalPaths`) |
| `POST /api/v1/import/sqlserver/test`, `POST /api/v1/import/sqlserver`, `PUT /api/v1/import/sqlserver/{id}` | Tester une connexion SQL Server, documenter la base (connexion enregistrée chiffrée), remplacer la connexion (si `AllowSqlServerSources`) |
| `GET /api/v1/Audit?actor=&method=&status=&controller=&entityId=&from=&to=` | Journal d'audit : qui a modifié ou exporté quoi, et quand (jamais le contenu des requêtes) |
| `GET/POST/PUT/DELETE /api/v1/Webhooks`, `POST /Webhooks/{id}/test`, `GET /Webhooks/{id}/deliveries` | Webhooks : une URL appelée en POST (JSON, signé HMAC-SHA256 si un secret est défini) quand une resynchronisation change le schéma (`schema.changed`) , qu'une base est importée (`database.imported`) ou supprimée (`database.deleted`) (ces deux-là sont à demander dans `events`) |
| `GET /api/v1/Capabilities` | Ce que le serveur autorise (sources SQL Server, chemins locaux) |
| `POST /api/v1/DatabaseInfo/{id}/sync` | Resynchronise la base depuis sa source |
| `GET /api/v1/Search?q=terme&limit=25` | Recherche globale |

Les réponses ne portent que des vues : une base est renvoyée sans son chemin de fichier ni sa connexion enregistrée (`canResync` dit seulement si elle peut être relue), et une synchronisation ne renvoie que ses comptes.

Les corps de `POST`/`PUT` sont validés (champs obligatoires, longueurs, `TableType` ∈ Table/View/Virtual, `RelationType` ∈ One-to-One/One-to-Many/Many-to-Many) et ne portent que les champs modifiables : le fichier source d'une base (`sourcePath`) n'est jamais écrasé par une modification.

Swagger est disponible sur `/swagger` en développement. Les erreurs sont renvoyées en ProblemDetails (400 pour une requête invalide, 409 pour une contrainte violée).

## Configuration (`api/appsettings.json`, section `Catalog`)

| Clé | Défaut | Rôle |
|---|---|---|
| `Provider` | `Sqlite` | Stockage du catalogue : `Sqlite` ou `SqlServer` |
| `ConnectionString` | — | Obligatoire pour `SqlServer` ; facultative en SQLite |
| `DataDirectory` | `granitdb-data` | Fichier de catalogue SQLite et fichiers importés |
| `MaxUploadMb` | `100` | Taille maximale d'un fichier importé |
| `AllowLocalPaths` | `false` | Autorise `POST /import/sqlite/local` (chemin sur le serveur) |
| `AllowSqlServerSources` | `true` | Autorise l'API à se connecter à un serveur SQL Server choisi par l'appelant (voir ci-dessous) |
| `KeysDirectory` | `<DataDirectory>/keys` | Clés qui chiffrent les connexions enregistrées |
| `StartupSources` | `[]` | Sources (`Sqlite` ou `SqlServer`) synchronisées au démarrage |

Journal d'audit (section `Audit`) : `RetentionDays` (365 ; 0 garde tout), purge faite au plus une fois par heure à l'écriture.

Webhooks (section `Webhooks`) : `AllowPrivateHosts` (`false` : refuse les adresses locales ou privées — `localhost`, `10.x`, `192.168.x`, `host.docker.internal`… — à ouvrir seulement sur un réseau de confiance), `MaxAttempts` (3), `RetryDelaysMs` (`[1000, 5000]`), `TimeoutSeconds` (5), `DeliveriesKept` (100 par webhook). Chaque envoi porte `X-GranitDb-Event`, `X-GranitDb-Delivery` et, avec un secret, `X-GranitDb-Signature: sha256=<hmac du corps>`.

## Documenter une base SQL Server

> **API dans Docker et SQL Server sur votre poste :** `localhost` et les adresses IPv6 locales (`fe80::…%11`) désignent le conteneur, pas votre machine. Saisissez `host.docker.internal` comme serveur (et le port TCP de l'instance ; activez « Faire confiance au certificat » pour un certificat auto-signé). Le serveur doit accepter TCP/IP (SQL Server Configuration Manager) et le pare-feu Windows laisser passer le port.

Activé par défaut (désactivable : `Catalog:AllowSqlServerSources=false`, en Docker `ALLOW_SQLSERVER_SOURCES=false`) ; « Connecter un serveur SQL Server » depuis l'accueil (ou Ctrl+K) : serveur, port, base, authentification (identifiant et mot de passe SQL, ou compte Windows du processus de l'API), chiffrement. **Tester la connexion** dit ce qui échoue (identifiants refusés, base absente, serveur injoignable, certificat non approuvé, droits insuffisants) ; une fois le test réussi on choisit les schémas à documenter, puis **Importer**. La base se resynchronise ensuite comme un fichier SQLite (aperçu des différences compris), **Modifier la connexion** change le mot de passe ou les schémas.

- **Seul le schéma est lu** (tables, colonnes, clés, index), jamais les données : un compte qui n'a que `VIEW DEFINITION` suffit. Créez-en un dédié, par exemple `CREATE LOGIN granitdb WITH PASSWORD = '…'; USE MaBase; CREATE USER granitdb FOR LOGIN granitdb; GRANT VIEW DEFINITION TO granitdb;`. La connexion déclare une intention de lecture seule.
- **La connexion est chiffrée** (ASP.NET Core Data Protection) avant d'être enregistrée dans le catalogue ; seul un libellé « serveur / base » est affichable, et aucune réponse de l'API ne renvoie le mot de passe ni la connexion. Les clés sont des fichiers de `KeysDirectory` : **sauvegardez-les avec le catalogue** et protégez ce dossier. Sans elles, une connexion enregistrée n'est plus lisible et l'interface demande de la saisir de nouveau.
- **Activé par défaut** (le catalogue peut être SQLite) : l'API se connecte alors à un serveur choisi par l'appelant, ce qui revient à lui prêter le réseau du serveur. Tant qu'il n'y a pas d'authentification (jalon 6), désactivez ceci hors d'un poste ou d'un réseau de confiance.

## Tests

```bash
dotnet test tests/GranitDb.Tests
```

Les tests SQLite n'ont besoin d'aucun service externe. Les tests SQL Server (migrations, dépôt, connecteur) démarrent un conteneur via Testcontainers : ils sont ignorés si Docker est absent, sauf avec `GRANITDB_REQUIRE_SQLSERVER=1` (positionné par la CI).

## Docker

```bash
docker compose up --build            # interface http://localhost:8081, API http://localhost:5105/swagger
```

Le catalogue SQLite est conservé dans le volume `granitdb-data`. Pour utiliser SQL Server comme catalogue :

```bash
cp .env.example .env
docker compose --profile sqlserver up --build
```

L'API crée la base `GranitDb` si elle n'existe pas et attend que le serveur réponde. L'URL de l'API vue par le navigateur se règle avec `API_BASE_URL`.

## Migrations du catalogue

Le schéma du catalogue est versionné (`api/Data/CatalogMigrations.cs`, version dans la table `SchemaVersion`) et mis à jour automatiquement au démarrage, en SQLite comme en SQL Server, y compris pour un catalogue créé avant l'introduction des migrations. Une migration déjà publiée ne se modifie jamais : on en ajoute une. Un catalogue plus récent que l'application est refusé. `InitV1.sql` n'est qu'une référence de l'état final côté SQL Server.

## Intégration continue

`.github/workflows/ci.yml` : build de la solution, tests (SQL Server compris, via Testcontainers) puis construction des deux images Docker et démarrage de la pile avec `docker compose`.

## Pagination et filtres

Les listes (`GET /api/v1/DatabaseInfo`, etc.) renvoient tout par défaut. Avec `?skip=0&take=50` elles renvoient une page triée par nom (`take` de 1 à 1000) et le total (filtré) dans l'en-tête `X-Total-Count`.

Filtres (cumulés par ET, valeurs vides ignorées) : `name` (contient, sans tenir compte de la casse) sur toutes les listes ; `databaseId`, `tableType`, `domainId`, `tag` sur `/TableInfo` ; `databaseId`, `tableId`, `dataType`, `isPrimaryKey`, `isNullable`, `isSensitive`, `tag` sur `/ColumnInfo` ; `firstId`, `secondId`, `relationType` sur `/RelationInfo`. Exemple : `GET /api/v1/ColumnInfo?databaseId=…&isPrimaryKey=true`.

## L'interface

L'interface est un espace de travail, pas une suite de pages :

- **Explorateur** (à gauche) : l'arbre des bases → tables → colonnes, filtrable, avec les **domaines** comme collections (glissez une table sur un domaine pour l'y ranger) et vos **favoris** en haut (étoile ou touche F, réordonnables par glisser-déposer). Flèches, Entrée et Origine/Fin le parcourent au clavier.
- **Onglets** : chaque page ouverte (base, table, diagramme, recherche) a son onglet, épinglable, fermable (croix, clic du milieu, Suppr) et réordonnable ; ils sont mémorisés dans le navigateur avec les favoris.
- **Détails** (à droite) : la sélection de l'explorateur (base, table ou colonne) avec sa description modifiable, ses étiquettes, sa complétude et son impact.
- **Palette de commandes** (Ctrl+K) : actions (importer, fermer l'onglet, basculer le thème…), bases et résultats de recherche au clavier.
- **Retours** : les confirmations apparaissent en bas, avec **Annuler** quand l'action peut être défaite (suppression d'une relation ou d'un domaine, étiquette retirée, marquage sensible, texte modifié…).

| Raccourci | Action |
|---|---|
| Ctrl+K | Palette de commandes |
| Alt+W | Fermer l'onglet affiché |
| Ctrl+B | Afficher ou masquer l'explorateur |
| ↑ ↓ ← → Origine Fin Entrée F | Parcourir l'explorateur, ouvrir, ajouter aux favoris |

Un lien « Aller au contenu » est proposé au clavier, le focus est toujours visible et les animations respectent la préférence « réduire les animations » du système. Sur un petit écran, l'explorateur et les détails deviennent des panneaux superposés.

## Documenter

Un clic sur une description ou des notes (page d'une base, onglet d'une table ou panneau de détail) l'ouvre en édition : le texte s'enregistre en quittant le champ ou avec Entrée (Maj+Entrée va à la ligne), Échap annule, et « Annuler » dans la confirmation rétablit l'ancien texte ; les modifications sont historisées. Chaque colonne peut être marquée **sensible** (bouclier) et étiquetée ; chaque table et colonne porte des **étiquettes** (ex : « RGPD »). Un indicateur de **complétude** montre la part des tables et colonnes décrites et signale d'abord les colonnes sensibles sans description. Les relations détectées (clés étrangères) restent corrigeables ; on peut ajouter ses propres relations (1-1, 1-N, N-N), qu'aucune synchronisation ne touche.

**Resynchroniser** commence par un **aperçu** des différences (ajouts, modifications, suppressions) et prévient quand une suppression ferait perdre du travail saisi (description, notes, marquage sensible, étiquettes, relation manuelle) ; rien n'est appliqué avant la confirmation.

## Diagramme et impact

Depuis une base, le bouton **Diagramme** ouvre un diagramme entité-relation interactif : choisissez les tables affichées (ou « Voisines » pour ne garder que la sélection et ses tables liées), glissez-les pour les placer, zoomez. Cliquer une table ou une colonne affiche ce qui dépend d'elle (rouge : « modifier cet élément peut casser ») et ce dont elle dépend (bleu), via `GET /Impact/…`. Les **domaines** regroupent des tables (une table peut appartenir à plusieurs domaines) pour des diagrammes lisibles par sujet : dans la page du diagramme, créez un domaine, puis cochez ses tables ou glissez-les sur lui (depuis la liste, ou en sortant la table du dessin vers la liste des domaines). La vue d'un domaine montre ses tables et, en pointillés et réduites aux colonnes de leurs relations, les tables des autres domaines qui leur sont liées ; la place de chaque table est mémorisée par domaine.

Le diagramme s'exporte en **SVG**, en **PNG** et en **Mermaid** (`erDiagram`, à coller dans Markdown GitHub/GitLab ou mermaid.live) ; l'export ne contient que les tables affichées.

## Recherche et thème

La barre de recherche de l'en-tête cherche dans les noms et descriptions des bases, tables, colonnes et index (Entrée pour lancer) ; un résultat ouvre la base avec la table concernée dépliée. Le bouton à droite de l'en-tête bascule entre thème clair et sombre (choix mémorisé). API : `GET /api/v1/Search?q=terme&limit=25`.

## Documentation du projet

L'état des lieux et la feuille de route sont dans [`work-doc/`](work-doc/). L'architecture détaillée et les conventions sont dans [`CLAUDE.md`](CLAUDE.md).
