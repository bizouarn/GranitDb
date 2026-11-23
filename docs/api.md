# API REST

Toutes les routes sont sous **`/api/v1`** ; les anciennes routes sans préfixe (`/DatabaseInfo`, …) restent acceptées comme alias. Swagger est disponible sur `/swagger` en développement ; le contrat OpenAPI versionné est `client/api/openapi.json`.

Exemple avec l'API seule :

```bash
curl -F "file=@ma_base.db" -F "name=Ma base" http://localhost:5105/api/v1/import/sqlite
curl http://localhost:5105/api/v1/DatabaseInfo
```

## Routes

| Route | Rôle |
|---|---|
| `GET/POST/PUT/DELETE /api/v1/DatabaseInfo`, `/TableInfo`, `/ColumnInfo`, `/RelationInfo` | CRUD sur les objets du catalogue |
| `GET /api/v1/TableInfo/database/{id}` | Tables d'une base, avec colonnes, index et relations (filtres `name`, `tableType`, pagination) |
| `GET /api/v1/TableInfo/{id}/columns`, `/indexes`, `/relations` | Colonnes (dans l'ordre), index et relations d'une table |
| `POST /api/v1/TableInfo/manual`, `POST /api/v1/TableInfo/{id}/columns` | Table manuelle (avec ses colonnes et un domaine facultatif), colonne ajoutée à une table manuelle |
| `GET /api/v1/Impact/table/{id}`, `/Impact/column/{id}` | Analyse d'impact : ce qui référence l'élément (en chaîne) et ce qu'il référence |
| `PUT /api/v1/{DatabaseInfo\|TableInfo\|ColumnInfo\|RelationInfo}/{id}/documentation` | Modifie la description et les notes (`metaInfo`) d'un élément ; un texte vide l'efface |
| `PUT /api/v1/ColumnInfo/{id}/sensitivity` | Marque ou démarque une colonne comme contenant des données sensibles |
| `POST /api/v1/RelationInfo` | Crée une relation manuelle (1-1, 1-N, N-N) ; les relations détectées par la synchronisation sont corrigeables (`PUT`) mais pas supprimables |
| `GET /api/v1/DomainInfo/database/{id}`, `POST/PUT/DELETE /api/v1/DomainInfo` | Domaines d'une base et leurs tables |
| `PUT/DELETE /api/v1/DomainInfo/{id}/tables/{tableId}`, `PUT /api/v1/DomainInfo/{id}/layout` | Range une table dans un domaine (`?moveFrom=` la retire d'un autre), la retire, mémorise la place des tables |
| `PUT /api/v1/DomainInfo/{id}/settings` | Réglages d'affichage d'un domaine (voisines, relations masquées, retour à la documentation importée) |
| `POST /api/v1/TagInfo/attach`, `DELETE /api/v1/TagInfo/{tagId}/links/{type}/{entityId}`, `GET /api/v1/TagInfo/database/{id}/links` | Étiquettes (base, table, colonne) |
| `GET /api/v1/Completeness/database/{id}?domainId=` | Part des tables et colonnes qui ont une description |
| `POST /api/v1/DatabaseInfo/{id}/sync` | Resynchronise la base depuis sa source |
| `POST /api/v1/DatabaseInfo/{id}/sync/preview` | Aperçu d'une synchronisation : ce qui serait ajouté, modifié, supprimé, et ce qui ferait perdre une documentation |
| `GET /api/v1/History?entityId=` | Historique des modifications (description, notes, marquage sensible) |
| `POST /api/v1/import/sqlite`, `PUT /api/v1/import/sqlite/{id}` | Import / ré-import d'un fichier SQLite |
| `POST /api/v1/import/sqlite/{id}/preview` | Différences d'un fichier de remplacement, sans rien écrire |
| `POST /api/v1/import/sqlite/local` | Import par chemin serveur (si `AllowLocalPaths`) |
| `POST /api/v1/import/server/test`, `POST …/server`, `PUT …/server/{id}` | Tester une connexion, documenter une base SQL Server / PostgreSQL / MySQL, remplacer la connexion (si `AllowSqlServerSources`). `import/sqlserver` est l'alias historique (force SQL Server) |
| `POST /api/v1/import/documentation/{databaseId}` | Importe une documentation externe (descriptions vides, domaines, relations manuelles) en rapprochant par nom |
| `GET /api/v1/Audit?actor=&method=&status=&controller=&entityId=&from=&to=` | Journal d'audit : qui a modifié ou exporté quoi, et quand (jamais le contenu des requêtes) |
| `GET/POST/PUT/DELETE /api/v1/Webhooks`, `POST /Webhooks/{id}/test`, `GET /Webhooks/{id}/deliveries` | Webhooks (voir plus bas) |
| `GET /api/v1/DatabaseInfo/{id}/export/html\|markdown\|csv\|xlsx` | Exports de la documentation ; `export/html?inline=true` l'affiche dans le navigateur (aperÃ§u, impression) au lieu de la tÃ©lÃ©charger |
| `GET /api/v1/Capabilities` | Ce que le serveur autorise (sources serveur, chemins locaux, profilage, IA, synchronisation planifiée) |
| `GET /api/v1/Search?q=terme&limit=25` | Recherche globale |

Les routes des fonctions optionnelles sont décrites dans [aides-documentation.md](aides-documentation.md) (profilage, relations suggérées, assistant d'IA, synchronisation planifiée) et [authentification.md](authentification.md) (`Auth/*`).

## Contrat des réponses

- Les réponses ne portent que des **vues** : une base est renvoyée sans son chemin de fichier ni sa connexion enregistrée (`canResync` dit seulement si elle peut être relue), et une synchronisation ne renvoie que ses comptes.
- Les corps de `POST`/`PUT` sont validés (champs obligatoires, longueurs, `TableType` ∈ Table/View/Virtual, `RelationType` ∈ One-to-One/One-to-Many/Many-to-Many) et ne portent que les champs modifiables : le fichier source d'une base (`sourcePath`) n'est jamais écrasé par une modification.
- Les erreurs sont renvoyées en ProblemDetails : 400 pour une requête invalide, 409 pour une contrainte violée.

## Pagination et filtres

Les listes (`GET /api/v1/DatabaseInfo`, etc.) renvoient tout par défaut. Avec `?skip=0&take=50` elles renvoient une page triée par nom (`take` de 1 à 1000) et le total (filtré) dans l'en-tête `X-Total-Count`.

Filtres (cumulés par ET, valeurs vides ignorées) :

| Liste | Filtres |
|---|---|
| toutes | `name` (contient, sans tenir compte de la casse) |
| `/TableInfo` | `databaseId`, `tableType`, `domainId`, `tag` |
| `/ColumnInfo` | `databaseId`, `tableId`, `dataType`, `isPrimaryKey`, `isNullable`, `isSensitive`, `tag` |
| `/RelationInfo` | `firstId`, `secondId`, `relationType` |

Exemple : `GET /api/v1/ColumnInfo?databaseId=…&isPrimaryKey=true`.

## Webhooks

Une URL appelée en POST (JSON, signé HMAC-SHA256 si un secret est défini) :

- `schema.changed` : une resynchronisation a changé tables, colonnes ou index ;
- `sync.failed` : une synchronisation planifiée a échoué (avec la raison, sans secret) ;
- `database.imported`, `database.deleted` : à demander dans `events`.

Chaque envoi porte `X-GranitDb-Event`, `X-GranitDb-Delivery` et, avec un secret, `X-GranitDb-Signature: sha256=<hmac du corps>`. Les adresses locales ou privées sont refusées (voir [configuration.md](configuration.md)).

## Régénérer le client

Après tout changement de route ou de type de l'API :

```powershell
scripts/generate-client.ps1
```

Le script réécrit `client/api/openapi.json` puis le client C# généré (`client/api/Generated/`, à ne jamais éditer à la main). `OpenApiContractTests` échoue si le contrat n'est plus à jour.
