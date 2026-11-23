# Configuration

Les réglages vivent dans `api/appsettings.json` (ou en variables d'environnement : `Catalog__DataDirectory`, `Auth__Enabled`…). L'authentification a sa propre page : [authentification.md](authentification.md).

## Section `Catalog`

| Clé | Défaut | Rôle |
|---|---|---|
| `Provider` | `Sqlite` | Stockage du catalogue : `Sqlite` ou `SqlServer` |
| `ConnectionString` | — | Obligatoire pour `SqlServer` ; facultative en SQLite |
| `DataDirectory` | `granitdb-data` | Fichier de catalogue SQLite et fichiers importés |
| `MaxUploadMb` | `100` | Taille maximale d'un fichier importé |
| `AllowLocalPaths` | `false` | Autorise `POST /import/sqlite/local` (chemin sur le serveur) |
| `AllowSqlServerSources` | `true` | Autorise l'API à se connecter à un serveur (SQL Server, PostgreSQL, MySQL) choisi par l'appelant |
| `KeysDirectory` | `<DataDirectory>/keys` | Clés qui chiffrent les connexions enregistrées |
| `KeysProtection` | `None` | Chiffrement des clés : `Dpapi` (Windows) ou `Certificate` (`KeysCertificatePath`, `KeysCertificatePassword`) |
| `StartupSources` | `[]` | Sources (`Sqlite` ou `SqlServer`) synchronisées au démarrage |
| `AllowProfiling` | `false` | Profilage des colonnes et détection des relations ([aides-documentation.md](aides-documentation.md)) |
| `AllowAi` | `false` | Assistant d'IA local |
| `AllowScheduledSync` | `true` | Synchronisation planifiée |
| `MinSyncIntervalMinutes` | `5` | Intervalle minimal réglable |
| `SchedulerTickSeconds` | `60` | Fréquence à laquelle le planificateur regarde les échéances |
| `SyncTimeoutSeconds` | `300` | Durée maximale d'une relecture de source |

Les clés propres au profilage, à la détection de relations et à l'IA sont dans [aides-documentation.md](aides-documentation.md).

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
