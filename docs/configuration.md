# Configuration

Les réglages vivent dans `api/appsettings.json` (ou en variables d'environnement : `Catalog__DataDirectory`, `Auth__Enabled`…). L'authentification a sa propre page : [authentification.md](authentification.md).

## Section `Catalog`

| Clé | Défaut | Rôle |
|---|---|---|
| `Provider` | `Sqlite` | Stockage du catalogue : `Sqlite` ou `SqlServer` |
| `ConnectionString` | — | Obligatoire pour `SqlServer` ; facultative en SQLite |
| `DataDirectory` | `granitdb-data` | Fichier de catalogue SQLite et fichiers importés |
| `MaxUploadMb` | `1024` | Taille maximale d'un fichier importé (`MAX_UPLOAD_MB` en Docker) ; le serveur en tient jusqu'à deux copies le temps de l'import |
| `AllowLocalPaths` | `false` | Autorise `POST /import/sqlite/local` (chemin sur le serveur) |
| `AllowSqlServerSources` | `true` | Autorise l'API à se connecter à un serveur (SQL Server, PostgreSQL, MySQL) choisi par l'appelant |
| `KeysDirectory` | `<DataDirectory>/keys` | Clés qui chiffrent les connexions enregistrées |
| `AllowedHosts` (racine, pas dans `Catalog`) | `localhost;127.0.0.1;[::1]` | Noms d'hôte acceptés par l'API (en-tête `Host`, séparés par `;`) : à étendre derrière un nom de domaine (`ALLOWED_HOSTS` en Docker) |
| `KeysProtection` | `None` | Chiffrement des clés : `Dpapi` (Windows) ou `Certificate` (`KeysCertificatePath`, `KeysCertificatePassword`) |
| `StartupSources` | `[]` | Sources (`Sqlite` ou `SqlServer`) synchronisées au démarrage |
| `AllowProfiling` | `false` | Profilage des colonnes et détection des relations ([aides-documentation.md](aides-documentation.md)) |
| `AllowAi` | `false` | Assistant d'IA local |
| `AllowScheduledSync` | `true` | Synchronisation planifiée |
| `MinSyncIntervalMinutes` | `5` | Intervalle minimal réglable |
| `SchedulerTickSeconds` | `60` | Fréquence à laquelle le planificateur regarde les échéances |
| `SyncTimeoutSeconds` | `300` | Durée maximale d'une relecture de source |

Les clés propres au profilage, à la détection de relations et à l'IA sont dans [aides-documentation.md](aides-documentation.md).

## Bases H2

Un fichier H2 (`.mv.db`) s'importe comme un fichier SQLite, **sans réglage, sans Java et sans jar** : l'API reconnaît son type à son en-tête et lit directement le schéma (tables, vues, colonnes, clés, relations, index ; le schéma `PUBLIC` garde les noms nus, les autres sont préfixés). Rien de la base n'est exécuté et le fichier est ouvert en lecture seule.

- **Versions** : H2 2.x (formats de fichier 2 et 3), vérifié avec 2.1.214 et 2.5.252. Les fichiers de H2 1.x sont refusés : convertissez-les avec H2 2.x (`SCRIPT` puis `RUNSCRIPT`).
- **Mot de passe** : il n'est pas demandé. Il protège la connexion par H2, pas le contenu d'un fichier non chiffré. Les bases chiffrées (`CIPHER`) ne sont pas prises en charge.
- **Vues** : H2 ne garde pas le type de leurs colonnes, affiché `ANY`.
- **Données** : seul le schéma est lu ; le profilage et la détection de relations ne sont pas disponibles pour H2.
- Fermez de préférence l'application qui utilise le fichier avant de le copier.

## Sources déclarées au démarrage

`Catalog:StartupSources` synchronise des sources à chaque démarrage. Une base lue ainsi n'a pas de secret enregistré : l'API ne peut pas la resynchroniser seule. Les chaînes de connexion peuvent être des références (`env:NOM`, `file:/run/secrets/nom`) plutôt que des mots de passe en clair (un avertissement le signale au démarrage).

## Journal d'audit (`Audit`)

`RetentionDays` (365 ; 0 garde tout). La purge est faite au plus une fois par heure, à l'écriture. Le journal enregistre l'auteur, la méthode, le chemin, le statut, l'adresse IP, jamais le corps des requêtes ; les exports y figurent aussi.

## Webhooks (`Webhooks`)

| Clé | Défaut | Rôle |
|---|---|---|
| `AllowPrivateHosts` | `false` | Refuse les adresses locales ou privées (`localhost`, `10.x`, `192.168.x`, `host.docker.internal`…) ; à ouvrir seulement sur un réseau de confiance |
| `MaxAttempts` | `3` | Tentatives par envoi |
| `RetryDelaysMs` | `[1000, 5000]` | Délais entre tentatives |
| `TimeoutSeconds` | `5` | Délai d'un appel |
| `DeliveriesKept` | `100` | Envois conservés par webhook |

La vérification est faite à l'enregistrement et de nouveau à la connexion.

## CORS

`Cors:AllowedOrigins` liste les origines autorisées (avec cookies). Vide : toutes les origines, sans cookies. Voir [authentification.md](authentification.md).

## Client

L'adresse de l'API se règle dans `client/wwwroot/appsettings*.json` (`ApiBaseUrl`) ; en Docker, avec `API_BASE_URL`.
