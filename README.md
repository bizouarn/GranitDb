# GranitDb

**Le catalogue de métadonnées de vos bases de données** : décrivez bases, tables, colonnes et relations, gardez la documentation vivante, partagez-la.

![Diagramme du domaine Logistique](docs/diagramme-logistique.png)

*Diagramme entité-relation du domaine « Logistique » d'une base d'exemple : tables du domaine, clés primaires et étrangères, cardinalités.*

## Pourquoi GranitDb

- **Aucun serveur requis** : le catalogue est un fichier SQLite, et une base à documenter peut être un simple fichier SQLite. SQL Server, PostgreSQL et MySQL / MariaDB sont aussi pris en charge comme sources.
- **Schéma lu, jamais modifié** : tables, colonnes, clés, index et relations sont extraits en lecture seule ; les descriptions saisies à la main survivent aux resynchronisations (aperçu des différences avant application, synchronisation planifiée possible).
- **Documentation collaborative** : descriptions modifiables en place, étiquettes, colonnes sensibles, historique, complétude, analyse d'impact.
- **Diagrammes par domaine** : regroupez les tables par sujet, placez-les, exportez en SVG, PNG ou Mermaid.
- **Exports** : documentation HTML autonome, Markdown, CSV, Excel, PDF.
- **Aides à la documentation** : détection assistée des relations manquantes, profilage léger des colonnes, propositions de descriptions par un modèle local (optionnel, jamais de valeur de ligne envoyée).
- **Prêt à partager** : API REST (Swagger), interface Blazor, authentification GitHub / OpenID Connect avec rôles, bases publiques en lecture, webhooks, journal d'audit, Docker.

## Démarrage en deux minutes

Avec Docker :

```bash
docker compose up --build   # interface http://localhost:8081, API http://localhost:5105/swagger
```

Sans Docker ([.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)) :

```bash
dotnet run --project api --launch-profile http   # API sur http://localhost:5105 (Swagger : /swagger)
dotnet run --project client                      # interface sur http://localhost:41248
```

Ouvrez l'interface et glissez un fichier `.db` / `.sqlite` sur la zone de dépôt : tables, colonnes, types, clés et relations sont extraits et affichés.

## Documentation de référence

Le reste de ce document détaille la structure du dépôt, l'API, la configuration et les fonctions.

## Prérequis

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (pour lancer sans Docker)
- Docker, facultatif : pile complète via `docker compose`, catalogue SQL Server, tests SQL Server

## Structure du dépôt

| Dossier | Contenu |
|---|---|
| `api/` | API ASP.NET Core (contrôleurs, accès au catalogue Dapper, connecteurs de sources, migrations) |
| `client/` | Interface Blazor WebAssembly + MudBlazor (PWA) |
| `tests/GranitDb.Tests` | Tests xUnit (API, synchronisation, pages bUnit) |
| `work-doc/` | État des lieux et feuille de route |

## Détails du démarrage

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

## Authentification et rôles (section `Auth`)

Désactivée par défaut (`Auth:Enabled` à `false`) : le poste de développement et le catalogue SQLite local restent utilisables sans compte. Activée, **toute route de l'API demande une session** (401 sinon) ; l'interface affiche alors un écran « Se connecter avec… ». La session est un cookie émis par l'API (`HttpOnly`, glissant, `Auth:SessionHours` heures, 8 par défaut) ; le navigateur ne détient aucun jeton lisible par le script de la page.

**Connexion GitHub** (OAuth App : *Settings → Developer settings → OAuth Apps*, adresse de retour `https://<api>/auth/callback/github`) et/ou **connecteur OpenID Connect générique** déclaré par configuration seule (Entra ID, Keycloak, Okta, Google… adresse de retour `https://<api>/auth/callback/<nom>`). Chaque fournisseur configuré donne un bouton sur l'écran de connexion ; plusieurs peuvent coexister.

```json
"Auth": {
  "Enabled": true,
  "GitHub": { "ClientId": "…", "ClientSecret": "…" },
  "Providers": {
    "corp": {
      "DisplayName": "SSO d'entreprise",
      "Authority": "https://sso.exemple.fr/realms/granit",
      "ClientId": "granitdb", "ClientSecret": "…",
      "Scopes": ["openid", "profile", "email"],
      "LoginClaim": "preferred_username", "NameClaim": "name", "GroupsClaim": "groups"
    }
  },
  "Roles": {
    "Admins": ["octocat"], "Editors": ["alice"], "Readers": [],
    "AdminGroups": ["monorg/admins"], "EditorGroups": ["monorg/dev", "corp-editors"], "ReaderGroups": ["monorg"],
    "DefaultRole": "None"
  }
},
"Cors": { "AllowedOrigins": ["https://doc.exemple.fr"] }
```

- **Secrets** : `ClientSecret` par variable d'environnement (`Auth__GitHub__ClientSecret`, `Auth__Providers__corp__ClientSecret`), user-secrets ou coffre, jamais dans le dépôt.
- **Rôles** : lecteur (lit), éditeur (écrit), administrateur (en plus : journal d'audit, webhooks, sources SQL Server, import par chemin local). Une liste de logins (`octocat` ou `github:octocat` / `corp:jdupont` pour viser un fournisseur) et/ou des groupes ; le rôle le plus élevé gagne ; sans correspondance, `DefaultRole` (`None` : accès refusé, le compte retourne à l'écran de connexion avec un message). Le premier administrateur se déclare dans `Admins` (ou `AdminGroups`) : l'API refuse de démarrer sans. Les rôles sont calculés **à la connexion** : un changement de groupe s'applique à la session suivante.
- **Groupes GitHub** : `organisation` pour tout membre, `organisation/equipe` pour une équipe (portée `read:org` demandée seulement si des groupes sont configurés ; les organisations qui restreignent l'accès aux OAuth Apps doivent l'approuver). Au plus 100 organisations et 100 équipes par compte sont lues. **Groupes OpenID Connect** : revendication `GroupsClaim` du jeton d'identité (à activer côté fournisseur : mapper de groupes Keycloak, « groups claim » Entra ID).
- **CORS et cookies** : déclarez l'adresse de l'interface dans `Cors:AllowedOrigins` (avec cookies). Sans cela, l'API répond à toutes les origines mais sans cookies : seule sa propre origine peut se connecter (un avertissement le dit au démarrage). Interface et API sur le même site : `Lax` (défaut) ; sur deux sites différents : `Auth:CookieSameSite` = `None` (HTTPS obligatoire, cookie `Secure`). L'adresse de retour de la connexion n'est suivie que si elle est un chemin de l'API ou une origine de `Cors:AllowedOrigins`.
- **Écritures** : avec l'authentification, chaque POST/PUT/DELETE doit porter l'en-tête `X-GranitDb-Client` (l'interface l'ajoute) : un site tiers ne peut pas l'envoyer sans autorisation CORS, ce qui ferme la voie aux requêtes forgées avec le cookie.
- **Derrière un proxy ou un conteneur** : l'API calcule l'adresse de retour du fournisseur depuis la requête ; activez les en-têtes transmis (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`) pour qu'elle reflète l'adresse publique (HTTPS).
- **Bases publiques** (`IsPublic`, réglage réservé aux administrateurs ; un éditeur qui modifie une base garde sa valeur) : lisibles **sans compte**, en lecture seule : fiche, tables, colonnes, relations, domaines, complétude, impact, diagrammes, exports et recherche (limitée à ces bases). L'interface s'ouvre alors en « Visiteur » avec un menu « Se connecter » ; le serveur et la base d'origine ne sont pas montrés. Tout le reste (journal, webhooks, historique, étiquettes globales, écritures) demande une session. `Auth:AllowPublicDatabases` à `false` ferme tout.
- **Secrets hors du fichier** : la chaîne de connexion d'une source de `Catalog:StartupSources` peut être une référence, `env:NOM` (variable d'environnement) ou `file:/run/secrets/nom` (secret Docker ou Kubernetes), au lieu du mot de passe en clair (un avertissement le signale au démarrage). Les clés qui chiffrent les connexions enregistrées peuvent être chiffrées à leur tour : `Catalog:KeysProtection` = `Dpapi` (Windows) ou `Certificate` (`KeysCertificatePath`, `KeysCertificatePassword` en `env:`/`file:`) ; les clés déjà créées restent lisibles, seules les nouvelles sont chiffrées. Un coffre distant (Key Vault…) se branche sur le même point de `Program.cs` mais n'est pas fourni.
- **Journal et historique** : l'auteur de chaque écriture est le login du compte (`History.ChangedBy`, `GET Audit`).
- `GET /api/v1/Auth/me` dit si l'authentification est active, qui est connecté et avec quel rôle ; `POST /api/v1/Auth/logout` ferme la session.

## Profilage léger des colonnes (`Catalog:AllowProfiling`)

Désactivé par défaut. Quand il est activé, un **administrateur** peut demander, table par table (« Profiler la table ») ou pour toute une base (« Profiler les colonnes »), le **nombre de lignes, de valeurs nulles et de valeurs distinctes** de chaque colonne. Ces comptes sont calculés **par la source elle-même** (`COUNT`, `COUNT(DISTINCT …)`, en lecture seule) : aucune valeur n'est lue par l'API ni stockée, seuls des comptes remontent. C'est la seule fonction qui lit des données de la source et pas seulement son schéma ; un compte SQL Server doit alors pouvoir lire les tables (`db_datareader`, au lieu du seul `VIEW DEFINITION`).

- La page d'une table montre pour chaque colonne « 25 % de nuls · 850 valeurs » et un genre : *unique* (un identifiant probable), *constante*, *peu de valeurs* (une liste de codes), *variée*, *vide*.
- Seules les tables se profilent (pas les vues). `Catalog:ProfilingTimeoutSeconds` (60) borne une table ; `Catalog:ProfilingMaxTables` (100) borne une demande sur une base : les tables les plus anciennement profilées passent d'abord, une grosse base se profile en plusieurs demandes. Une table en échec est signalée sans arrêter les autres.
- API : `GET TableInfo/{id}/profile` (lecteur), `POST TableInfo/{id}/profile` et `POST DatabaseInfo/{id}/profile` (administrateur). `GET Capabilities` indique `allowProfiling`.
- Limite : sur SQL Server, une table hors `dbo` s'appelle `schéma.table` dans le catalogue ; un nom de table de `dbo` qui contient un point serait mal interprété.

## Détection assistée des relations (même interrupteur que le profilage)

Beaucoup de bases n'ont pas de clés étrangères déclarées. Le bouton « Relations suggérées » de la page d'une base ouvre un écran qui **propose** les relations manquantes, sans modèle d'IA :

- **Candidats par le nom** : `client_id`, `id_client`, `clientId`, `fk_client`, `client_code` sont rapprochés de la table `client`, `clients` ou `tbl_client` (singulier/pluriel, préfixes techniques) quand les types sont compatibles et que la cible est une clé primaire ou une colonne unique. Un préfixe de rôle (`billing_client_id`) est reconnu mais noté plus bas. Les colonnes déjà reliées ne reviennent pas.
- **Vérification par des comptes** (administrateur, `Catalog:AllowProfiling`) : la source compte, en lecture seule (`NOT EXISTS`), les valeurs de la colonne qui n'existent pas dans la cible. Aucune valeur n'est lue ni gardée. Plus de 5 % d'orphelines : le candidat est écarté. `Catalog:RelationDetectionMaxChecks` (500) borne le nombre de vérifications par détection.
- **Score de 0 à 100** (nom 40 ou 28, type 10, cible unique 15, inclusion 30, moins 25 si la cible a moins de 20 lignes : un statut ou un code se retrouve par hasard), classé *probable* (75 et plus), *possible* (55 et plus) ou *faible*, avec ses raisons en clair.
- **Rien n'est créé sans accord** : « Accepter » crée une relation manuelle marquée `detected` (la resynchronisation la conserve), « Refuser » est mémorisé et la relation n'est plus proposée, « Accepter les probables » traite le lot.
- API : `GET DatabaseInfo/{id}/relation-suggestions?status=`, `POST …/relation-suggestions/detect` (administrateur), `POST …/{suggestionId}/accept|reject`, `POST …/relation-suggestions/accept?minScore=`. Pas encore : les clés composites et l'affichage « détectée » dans le diagramme.

## Assistant d'IA : socle local (`Catalog:AllowAi`)

Désactivé par défaut. C'est le socle des futures aides à la documentation (jalon 13, lot C) : un modèle **léger et local** qui reçoit des noms, des types et des descriptions du catalogue, **jamais de valeur de ligne**. Un seul protocole, l'API compatible OpenAI (`/chat/completions`), que parlent Ollama, `llama-server` (llama.cpp), LM Studio, vLLM et la plupart des services hébergés.

- **Avec Docker** : `ALLOW_AI=true` puis `docker compose --profile ai up --build -d` (PowerShell : `$env:ALLOW_AI = "true"; docker compose --profile ai up --build -d`). Un service Ollama démarre, un second télécharge le modèle au premier lancement (plusieurs Go, volume `ollama-models`) ; tant qu'il manque, l'API répond « modèle en cours de téléchargement » au lieu d'échouer. Pour le GPU, voir ci-dessous.
- **Modèle par défaut** : `qwen3:4b-instruct` (4 milliards de paramètres quantifiés, ≈ 2,5 Go, tourne sur CPU avec 8 Go de mémoire). `AI_MODEL=gemma3:4b` pour Gemma, `qwen3:1.7b` ou `gemma3:1b` pour une machine modeste, un modèle de 12 à 14 milliards pour la qualité avec GPU. Vérifiez les noms et les conditions de licence (Gemma a les siennes) au moment de l'installation.
- **GPU NVIDIA** (très recommandé) : `docker compose -f docker-compose.yml -f docker-compose.gpu.yml --profile ai up --build -d`, avec les pilotes NVIDIA (et Docker Desktop sous WSL2 sous Windows). Ne l'utilisez pas sans GPU : le conteneur refuserait de démarrer. Mesuré sur une GTX 1650 (4 Go de mémoire vidéo) : 82 % du modèle tient sur la carte, environ 19 jetons par seconde, une description en 7 secondes.
- **Pourquoi « instruct »** : le `qwen3:4b` d'Ollama est une variante qui réfléchit toujours (un bloc de raisonnement caché) : 1 475 jetons et 3 min 35 pour une seule phrase, contre 53 jetons et 7 s avec `qwen3:4b-instruct`. Pour un autre modèle « thinking », préférez sa variante « instruct ».
- **Sans Docker** : installez Ollama, `ollama pull qwen3:4b-instruct`, puis `Catalog:AllowAi=true` (l'adresse par défaut est `http://localhost:11434/v1`).
- **Réglages `Ai:*`** : `Endpoint`, `Model`, `ApiKey` (`env:NOM` ou `file:/chemin` pour ne pas l'écrire), `DisableThinking` (vrai : les modèles qui réfléchissent, comme Qwen3, répondent directement, beaucoup plus vite), `TimeoutSeconds` (120), `MaxConcurrent` (1 : un petit serveur ne tient pas la charge, les appels font la queue), `MaxRequestChars` (12 000), `MaxOutputTokens` (1 024), `Language` (français), `AllowRemote`.
- **Confidentialité** : un serveur qui n'est pas cette machine doit être en **https** (sinon `Ai:AllowRemote=true`, pour un réseau de confiance comme celui de Docker). L'adresse ne porte ni identifiants ni paramètres. Les journaux ne gardent que le modèle, la durée et le nombre de jetons, jamais le contenu.
- **Sortie JSON contrainte** par un schéma, validée, avec un seul réessai qui dit au modèle ce qui n'allait pas, puis une erreur claire ; les blocs `<think>` des modèles qui raisonnent sont retirés.
- API (administrateur) : `GET Ai/status` (serveur joignable, modèle installé ou en téléchargement), `POST Ai/test` (appel minimal). `GET Capabilities` annonce `allowAi` et `aiModel`. La page « Étiquettes et domaines » montre l'état et un bouton « Tester le modèle ».

## Assistant de documentation (propositions à relire)

Quand l'assistant d'IA est activé (voir ci-dessus), la page d'une base propose « Assistant de documentation » : des **propositions** de descriptions pour tout ce qui n'en a pas, à relire avant d'être enregistrées.

- **Documenter en lot** : une tâche d'arrière-plan parcourt la base (description de la base, de chaque table et de ses colonnes, puis des relations), avec progression, bouton « Arrêter » et reprise (un élément déjà proposé, accepté ou refusé n'est pas redemandé). Une seule documentation à la fois par base ; trois échecs de suite l'arrêtent avec la raison.
- **Ce que reçoit le modèle** : le nom de la base, de la table, les noms, types, clés et obligations des colonnes, les relations et la description des tables voisines, quelques descriptions déjà saisies (pour imiter le style) et le genre du profil (« unique », « peu de valeurs ») s'il existe. **Jamais de valeur de ligne, jamais une colonne marquée sensible** (ni son nom, ni les relations qui la touchent). Les colonnes partent par lots de quinze.
- **Relecture** : chaque proposition se modifie puis s'accepte, ou se refuse (un refus n'est plus reproposé par le lot). Une proposition est marquée **incertaine** quand le nom est cryptique (`ZX_T01`, `cdcl`) ou que le modèle doute ; « Accepter les propositions sûres » ne prend jamais les incertaines. Accepter écrit la description avec l'origine dans l'historique (« alice (proposé par qwen3:4b-instruct) »). **Une description déjà saisie n'est jamais remplacée**, même si elle est arrivée entre la proposition et l'acceptation.
- **« Suggérer »** : sous une description vide de table ou de colonne, un bouton demande une proposition pour cet élément seul, à accepter ou modifier.
- **Colonnes sensibles** : « Détecter les colonnes sensibles » propose, par le nom (e-mail, IBAN, téléphone, date de naissance, mot de passe, nom de personne, adresse, carte), les colonnes à marquer sensibles. Pas de modèle ici, et cela marche même sans IA ; chaque proposition se valide.
- API (éditeur) : `GET DescriptionSuggestions/database/{id}?status=`, `POST …/generate`, `GET …/job`, `POST …/job/cancel`, `POST …/suggest` (`targetType`, `targetId`), `POST …/detect-sensitive`, `POST …/{id}/accept` (texte retouché facultatif), `POST …/{id}/reject`, `POST …/accept-confident`.
- Pas encore : glossaire d'abréviations, domaines suggérés, évaluation comparée des modèles.

## Synchronisation planifiée (`Catalog:AllowScheduledSync`)

Un **administrateur** règle, sur la page d'une base qui a une source à relire (fichier conservé ou connexion enregistrée), une **synchronisation automatique** : toutes les 15 minutes, toutes les heures, toutes les 6 heures ou tous les jours (`PUT DatabaseInfo/{id}/schedule`, `intervalMinutes`). Le planificateur de l'API (`SyncScheduler`, un service d'arrière-plan qui regarde les échéances toutes les `Catalog:SchedulerTickSeconds` secondes, 60 par défaut) relit alors la source comme « Resynchroniser » : les descriptions saisies sont conservées, les tables, colonnes et index qui changent sont mis à jour.

- **Notification** : un changement de schéma déclenche le webhook `schema.changed` ; un échec (source injoignable, compte refusé, délai dépassé) déclenche `sync.failed` avec la raison, sans secret. Le dernier résultat reste sur la base (« Dernière synchronisation : … — réussie / échec : … » sur sa page).
- Une base en échec n'est pas retentée avant l'intervalle suivant, pour ne pas martyriser une source en panne. `Catalog:MinSyncIntervalMinutes` (5) borne l'intervalle réglable, `Catalog:SyncTimeoutSeconds` (300) la durée d'une relecture. `Catalog:AllowScheduledSync` à `false` éteint tout. Sans intervalle réglé, rien ne tourne.
- Une base SQLite importée par envoi de fichier relit le fichier conservé : la planification n'y a de sens qu'avec un chemin local (`Catalog:AllowLocalPaths`) ou un fichier que vous remplacez.

## Documenter une base SQL Server, PostgreSQL ou MySQL

> **API dans Docker et SQL Server sur votre poste :** `localhost` et les adresses IPv6 locales (`fe80::…%11`) désignent le conteneur, pas votre machine. Saisissez `host.docker.internal` comme serveur (et le port TCP de l'instance ; activez « Faire confiance au certificat » pour un certificat auto-signé). Le serveur doit accepter TCP/IP (SQL Server Configuration Manager) et le pare-feu Windows laisser passer le port.

Activé par défaut (désactivable : `Catalog:AllowSqlServerSources=false`, en Docker `ALLOW_SQLSERVER_SOURCES=false`) ; « Connecter un serveur SQL Server » depuis l'accueil (ou Ctrl+K) : serveur, port, base, authentification (identifiant et mot de passe SQL, ou compte Windows du processus de l'API), chiffrement. **Tester la connexion** dit ce qui échoue (identifiants refusés, base absente, serveur injoignable, certificat non approuvé, droits insuffisants) ; une fois le test réussi on choisit les schémas à documenter, puis **Importer**. La base se resynchronise ensuite comme un fichier SQLite (aperçu des différences compris), **Modifier la connexion** change le mot de passe ou les schémas.

- **Seul le schéma est lu** (tables, colonnes, clés, index), jamais les données : un compte qui n'a que `VIEW DEFINITION` suffit. Créez-en un dédié, par exemple `CREATE LOGIN granitdb WITH PASSWORD = '…'; USE MaBase; CREATE USER granitdb FOR LOGIN granitdb; GRANT VIEW DEFINITION TO granitdb;`. La connexion déclare une intention de lecture seule.
- **La connexion est chiffrée** (ASP.NET Core Data Protection) avant d'être enregistrée dans le catalogue ; seul un libellé « serveur / base » est affichable, et aucune réponse de l'API ne renvoie le mot de passe ni la connexion. Les clés sont des fichiers de `KeysDirectory` : **sauvegardez-les avec le catalogue** et protégez ce dossier. Sans elles, une connexion enregistrée n'est plus lisible et l'interface demande de la saisir de nouveau.
- **Moteurs pris en charge** : Microsoft SQL Server, **PostgreSQL** et **MySQL / MariaDB** (champ « Type de base » de la page de connexion ; `engine` = `SqlServer`, `PostgreSql` ou `MySql` dans l'API, routes `import/server`, et `import/sqlserver` pour SQL Server). Le même interrupteur `Catalog:AllowSqlServerSources` couvre les trois.
  - **PostgreSQL** : un compte qui peut se connecter à la base et a `USAGE` sur les schémas suffit ; les tables du schéma `public` gardent leur nom, les autres s'appellent `schéma.table` ; on choisit les schémas à documenter ; vues et vues matérialisées comprises. La connexion ouvre ses transactions en lecture seule (`default_transaction_read_only`).
  - **MySQL / MariaDB** : un compte qui peut lire la base (`SELECT`) suffit ; « schéma » et « base » sont la même chose, on documente la base de la connexion (pas de choix de schémas) ; les clés étrangères vers une autre base sont ignorées. « Chiffrer » exige un certificat vérifiable (`VerifyCA`) sauf si « Faire confiance au certificat » est coché.
  - Authentification : identifiant et mot de passe ; le compte Windows de l'API (`Integrated`) n'existe que pour SQL Server. Le profilage des colonnes fonctionne sur les trois moteurs.
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
