# État des lieux — GranitDb

_Date : 2026-11-17 — branche `feature/work` (base : `main` @ `ced701f`)_

> **Instantané du 2026-11-17, avant les jalons 1 et 2.** Les bugs de synchronisation, la dette d'architecture (accès statique, N+1, absence de tests/CI/migrations) et les problèmes de démarrage listés ci-dessous ont été corrigés depuis ; la sécurité et les fonctionnalités du client restent d'actualité. Voir [la roadmap](01-roadmap.md) pour l'état à jour.

## 1. Objectif du projet

GranitDb est un **catalogue de métadonnées de bases de données** : on décrit des bases, tables, colonnes et relations (avec description et `MetaInfo`), stockées dans une base SQL Server « catalogue », exposées par une API REST et consultables via une UI Blazor WebAssembly.

Le README ne contient que le titre. L'intention ci-dessus est déduite du code.

## 2. Structure

| Projet | Techno | Rôle |
|---|---|---|
| `api/` | ASP.NET Core 8, Dapper.Contrib, Swashbuckle, `Gabi.Base` | API REST + synchronisation du schéma |
| `client/` | Blazor WASM 8, MudBlazor 8.3 | Interface (PWA) |
| `interfaces/` | net10.0 | Interfaces partagées `I*Info` |
| `InitV1.sql` | T-SQL | Schéma du catalogue |
| `WebApp/` | — | Dossier vide, non suivi par git |

Historique : 5 commits (`first commit`, `Init API`, `Add API object CRUD`, `Init client App`, `Change UI KIT`).

## 3. Ce qui fonctionne (d'après la lecture du code, rien n'a été exécuté)

- **Modèle de données** : `Databases`, `Tables`, `Columns`, `Relations` (`InitV1.sql`), mappés par `DatabaseInfo`, `TableInfo`, `ColumnInfo`, `RelationInfo` (héritent de `InfoBase`).
- **CRUD générique** : `BaseDbInfoController<T>` (GET par id, POST, PUT, DELETE), plus un `TableInfoController` dédié qui enrichit une table avec ses colonnes et relations, et expose `GET TableInfo/database/{databaseId}`.
- **Synchronisation SQL Server** (`SqlServer.SyncAsync`) : lit le schéma (`GetSchema`) de la base connectée et crée/met à jour/supprime bases, tables et colonnes dans le catalogue. Déclenchée une seule fois au démarrage, avec l'id `"1"`.
- **Infra** : Swagger en Development, CORS ouvert, Dockerfile API, journalisation Serilog via `Gabi.Base`.
- **Client** : coque MudBlazor (layout + menu), PWA (service worker), page Home vide.

## 4. Ce qui est incomplet ou cassé

### Bugs probables dans l'API
1. **`IS_NULLABLE`** (`SqlServer.cs`) : `row["IS_NULLABLE"] == "NO"` compare un `object` à une chaîne par référence. Le test est presque toujours faux, donc toute colonne est enregistrée comme nullable.
2. **Mise à jour des colonnes** : le cas « update » force `IsNullable = true`.
3. **Colonnes indexées par nom seul** : le dictionnaire `columns` est clé sur `Name`, sans la table. Deux tables avec une colonne `Id` ou `Name` se marchent dessus (une seule colonne est conservée, les autres sont recréées à chaque sync).
4. **Suppressions « fire-and-forget »** : `_ = InfoBase.DeleteAsync(...)` n'est pas attendu, donc exceptions perdues et ordre non garanti.
5. **`table` potentiellement null** dans la boucle des colonnes si la table n'existe pas dans le catalogue (`table.Id` déréférencé).
6. **`Config.ReadConfig()`** renvoie `null` si `config.json` est absent : `NullReferenceException` au démarrage, sans message clair.
7. **`UploadSqlLiteController.CreateDatabase`** : mélange `IFormFile` et `[FromBody]` (incompatible en multipart), le résultat de `CreatedAtAction` est ignoré, et la logique de traitement du fichier est vide.
8. **`DeleteAsync<T>(id)`** : si l'id n'existe pas, `DeleteAsync(null)` est appelé sur Dapper sans garde.
9. **Table `Version`** lue au démarrage (`SELECT TOP(1) [versionNumber] FROM [Version]`) mais **absente de `InitV1.sql`** : l'API ne démarre pas sur une base créée uniquement avec ce script.
10. **`TableInfoController`** : N+1 massif (`GetAllAsync<ColumnInfo>()` et `<RelationInfo>()` dans une boucle, filtrage en mémoire).
11. **`Logger.CreateLogger`** est appelé après `builder.Build()`.

### Architecture / qualité
- **Accès données statique** : `InfoBase` porte les méthodes CRUD statiques et `Config.GetDb()` est un singleton statique. Pas d'injection de dépendances, difficile à tester.
- **Pas de filtrage côté SQL** : `GetAllAsync` charge toute la table, filtrée en LINQ.
- **Pas de contrainte d'intégrité** dans `InitV1.sql` (aucune FK, pas d'index sur `DatabaseId` / `TableId`).
- **Pas de migrations** : un seul script SQL manuel, sans versionnage alors qu'une table `Version` est attendue.
- **Pas de validation** des entrées (DTO = entités), pas de gestion d'erreurs centralisée (`GranitExceptions` n'est pas branchée sur un middleware).
- **Dockerfile** : `config.json` est ignoré par git et n'est pas copié dans l'image ; aucun docker-compose, aucune SQL Server de dev fournie.

### Sécurité
- **Aucune authentification ni autorisation** (`UseAuthorization` sans schéma configuré).
- **CORS** : toutes les origines acceptées (`SetIsOriginAllowed(_ => true)`).
- `IsPublic` existe sur `DatabaseInfo` mais n'est utilisé nulle part.
- Le contrôleur d'upload écrit des fichiers sur disque à partir d'un `id` fourni par le client (risque de path traversal à traiter).
- Le catalogue et la base surveillée utilisent **la même connexion** (`Config.GetDb()`) : pas de séparation.

### Client
- **Aucune fonctionnalité métier** : `Home.razor` est le gabarit par défaut, `Counter.razor` est un reste du template, `sample-data/weather.json` aussi.
- **`api/Client.cs`** : classe jamais instanciée (`new Client(...)` absent de `Program.cs`) ; seule `GetAllDatabaseAsync` existe, sans gestion d'erreur.
- **`HttpClient` enregistré** avec l'adresse du client lui-même, pas celle de l'API ; URL de l'API non configurable.
- **`client/Models/DatabaseInfo`** duplique le modèle API ; pas de modèles pour tables/colonnes/relations côté client.

### Tests, CI, doc
- **Aucun test** (ni unitaire, ni d'intégration).
- **`.github/workflows/` vide** : pas de CI.
- **Pas de documentation** utilisateur ni de procédure d'installation (le README est vide).
- **Mélange de langues** (FR/EN) dans le code, les commentaires et les messages d'erreur.

## 5. Dette technique prioritaire

| # | Sujet | Gravité |
|---|---|---|
| 1 | Table `Version` absente du script SQL (démarrage impossible depuis zéro) | Haute |
| 2 | Bugs de la synchronisation (`IS_NULLABLE`, clé de colonnes, suppressions non attendues) | Haute |
| 3 | Absence d'authentification et CORS grand ouvert | Haute |
| 4 | Pas de tests ni de CI | Moyenne |
| 5 | Accès données statique + N+1 | Moyenne |
| 6 | Client sans fonctionnalité, `Client.cs` non branché | Moyenne |
| 7 | Upload SQLite non fonctionnel | Basse (fonctionnalité non démarrée) |

## 6. Questions ouvertes

- Cible produit : outil interne d'équipe ou service multi-tenant avec bases « publiques » (`IsPublic`) ?
- Quels moteurs supporter ? Seul SQL Server est implémenté ; `Type` mentionne PostgreSQL/MySQL et un contrôleur d'upload SQLite existe.
- Les descriptions sont-elles éditées uniquement dans le catalogue, ou aussi réécrites vers la base source ?
- Conserver le projet `interfaces` partagé ou générer un client typé depuis OpenAPI ?
