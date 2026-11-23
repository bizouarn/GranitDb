# Aides à la documentation

Quatre fonctions optionnelles, toutes désactivées ou à régler : profilage des colonnes, détection des relations, synchronisation planifiée et assistant d'IA local.

| Fonction | Interrupteur | Lit des données de la source ? |
|---|---|---|
| Profilage des colonnes | `Catalog:AllowProfiling` | Des comptes seulement |
| Détection des relations | `Catalog:AllowProfiling` | Des comptes seulement |
| Synchronisation planifiée | `Catalog:AllowScheduledSync` | Non (schéma) |
| Assistant d'IA | `Catalog:AllowAi` | Non (jamais de valeur de ligne) |

## Profilage léger des colonnes

Désactivé par défaut. Un **administrateur** peut demander, table par table (« Profiler la table ») ou pour toute une base (« Profiler les colonnes »), le **nombre de lignes, de valeurs nulles et de valeurs distinctes** de chaque colonne. Ces comptes sont calculés **par la source elle-même** (`COUNT`, `COUNT(DISTINCT …)`, en lecture seule) : aucune valeur n'est lue par l'API ni stockée, seuls des comptes remontent. C'est la seule fonction qui lit des données de la source et pas seulement son schéma ; un compte SQL Server doit alors pouvoir lire les tables (`db_datareader`, au lieu du seul `VIEW DEFINITION`).

- La page d'une table montre pour chaque colonne « 25 % de nuls · 850 valeurs » et un genre : *unique* (un identifiant probable), *constante*, *peu de valeurs* (une liste de codes), *variée*, *vide*.
- Seules les tables se profilent (pas les vues). `Catalog:ProfilingTimeoutSeconds` (60) borne une table ; `Catalog:ProfilingMaxTables` (100) borne une demande sur une base : les tables les plus anciennement profilées passent d'abord, une grosse base se profile en plusieurs demandes. Une table en échec est signalée sans arrêter les autres.
- API : `GET TableInfo/{id}/profile` (lecteur), `POST TableInfo/{id}/profile` et `POST DatabaseInfo/{id}/profile` (administrateur). `GET Capabilities` indique `allowProfiling`.
- Fonctionne sur SQLite, SQL Server, PostgreSQL et MySQL. Les exports n'incluent pas encore les profils.
- Limite : sur SQL Server, une table hors `dbo` s'appelle `schéma.table` dans le catalogue ; un nom de table de `dbo` qui contient un point serait mal interprété.

## Détection assistée des relations

Beaucoup de bases n'ont pas de clés étrangères déclarées. Le bouton « Relations suggérées » de la page d'une base ouvre un écran qui **propose** les relations manquantes, sans modèle d'IA (même interrupteur que le profilage).

- **Candidats par le nom** : `client_id`, `id_client`, `clientId`, `fk_client`, `client_code` sont rapprochés de la table `client`, `clients` ou `tbl_client` (singulier/pluriel, préfixes techniques) quand les types sont compatibles et que la cible est une clé primaire ou une colonne unique. Un préfixe de rôle (`billing_client_id`) est reconnu mais noté plus bas. Les colonnes déjà reliées ne reviennent pas.
- **Recherche par les valeurs** (« Détecter aussi par les valeurs ») : pour les colonnes dont le nom ne dit pas la table (`code_fab` → `fabricants`). Chaque colonne comparable est rapprochée des colonnes uniques de même famille de type des autres tables, classées par ressemblance des noms ; un test rapide sur un échantillon écarte la plupart des candidats avant la vérification complète. Limites : `Catalog:RelationDetectionSampleSize`, `RelationDetectionMaxValueChecks` (20 000), `RelationDetectionMaxValueSeconds` (300) ; seulement sur une base d'au plus 80 tables pour les colonnes sans ressemblance de nom.
- **Vérification par des comptes** (administrateur) : la source compte, en lecture seule, les valeurs de la colonne qui n'existent pas dans la cible. Aucune valeur n'est lue ni gardée. Plus de 5 % d'orphelines : le candidat est écarté. `Catalog:RelationDetectionMaxChecks` (500) borne le nombre de vérifications par détection.
- **Score de 0 à 100** (nom 40 ou 28, type 10, cible unique 15, inclusion 30, moins 25 si la cible a moins de 20 lignes : un statut ou un code se retrouve par hasard), classé *probable* (75 et plus), *possible* (55 et plus) ou *faible*, avec ses raisons en clair.
- **Rien n'est créé sans accord** : « Accepter » crée une relation manuelle marquée `detected` (la resynchronisation la conserve), « Refuser » est mémorisé et la relation n'est plus proposée, « Accepter les probables » traite le lot.
- API : `GET DatabaseInfo/{id}/relation-suggestions?status=`, `POST …/relation-suggestions/detect[?byValues=true]` (administrateur), `POST …/{suggestionId}/accept|reject`, `POST …/relation-suggestions/accept?minScore=`.
- Pas encore : les clés composites et l'affichage « détectée » dans le diagramme.

## Synchronisation planifiée

Un **administrateur** règle, sur la page d'une base qui a une source à relire (fichier conservé ou connexion enregistrée), une **synchronisation automatique** : toutes les 15 minutes, toutes les heures, toutes les 6 heures ou tous les jours (`PUT DatabaseInfo/{id}/schedule`, `intervalMinutes`, 0 = arrêt). Le planificateur de l'API (`SyncScheduler`, un service d'arrière-plan qui regarde les échéances toutes les `Catalog:SchedulerTickSeconds` secondes) relit alors la source comme « Resynchroniser » : les descriptions saisies sont conservées, les tables, colonnes et index qui changent sont mis à jour.

- **Notification** : un changement de schéma déclenche le webhook `schema.changed` ; un échec (source injoignable, compte refusé, délai dépassé) déclenche `sync.failed` avec la raison, sans secret. Le dernier résultat reste sur la base.
- Une base en échec n'est pas retentée avant l'intervalle suivant, pour ne pas martyriser une source en panne. `Catalog:MinSyncIntervalMinutes` (5) borne l'intervalle réglable, `Catalog:SyncTimeoutSeconds` (300) la durée d'une relecture. `Catalog:AllowScheduledSync` à `false` éteint tout. Sans intervalle réglé, rien ne tourne.
- Une base SQLite importée par envoi de fichier relit le fichier conservé : la planification n'y a de sens qu'avec un chemin local (`Catalog:AllowLocalPaths`) ou un fichier que vous remplacez.

## Assistant d'IA : socle local

Désactivé par défaut (`Catalog:AllowAi`). Un modèle **léger et local** reçoit des noms, des types et des descriptions du catalogue, **jamais de valeur de ligne**. Un seul protocole, l'API compatible OpenAI (`/chat/completions`), que parlent Ollama, `llama-server` (llama.cpp), LM Studio, vLLM et la plupart des services hébergés.

- **Avec Docker** : `ALLOW_AI=true` puis `docker compose --profile ai up --build -d` (PowerShell : `$env:ALLOW_AI = "true"; docker compose --profile ai up --build -d`). Un service Ollama démarre, un second télécharge le modèle au premier lancement (plusieurs Go, volume `ollama-models`) ; tant qu'il manque, l'API répond « modèle en cours de téléchargement » au lieu d'échouer.
- **Sans Docker** : installez Ollama, `ollama pull qwen3:4b-instruct`, puis `Catalog:AllowAi=true` (l'adresse par défaut est `http://localhost:11434/v1`).
- **Modèle par défaut** : `qwen3:4b-instruct` (4 milliards de paramètres quantifiés, ≈ 2,5 Go, tourne sur CPU avec 8 Go de mémoire). `AI_MODEL=gemma3:4b` pour Gemma, `qwen3:1.7b` ou `gemma3:1b` pour une machine modeste, un modèle de 12 à 14 milliards pour la qualité avec GPU. Vérifiez les noms et les conditions de licence au moment de l'installation.
- **GPU NVIDIA** (très recommandé) : `docker compose -f docker-compose.yml -f docker-compose.gpu.yml --profile ai up --build -d`, avec les pilotes NVIDIA (et Docker Desktop sous WSL2 sous Windows). Ne l'utilisez pas sans GPU : le conteneur refuserait de démarrer. Mesuré sur une GTX 1650 (4 Go de mémoire vidéo) : 82 % du modèle tient sur la carte, environ 19 jetons par seconde, une description en 7 secondes.
- **Pourquoi « instruct »** : le `qwen3:4b` d'Ollama réfléchit toujours (un bloc de raisonnement caché) : 1 475 jetons et 3 min 35 pour une seule phrase, contre 53 jetons et 7 s avec `qwen3:4b-instruct`. Pour un autre modèle « thinking », préférez sa variante « instruct ».

### Réglages `Ai:*`

| Clé | Rôle |
|---|---|
| `Endpoint` | Adresse du serveur (défaut `http://localhost:11434/v1`) |
| `Model` | Nom du modèle |
| `ApiKey` | Clé, de préférence `env:NOM` ou `file:/chemin` |
| `DisableThinking` | Vrai : les modèles qui réfléchissent (Qwen3) répondent directement, beaucoup plus vite |
| `TimeoutSeconds` | 120 |
| `MaxConcurrent` | 1 : un petit serveur ne tient pas la charge, les appels font la queue |
| `MaxRequestChars` | 12 000 |
| `MaxOutputTokens` | 1 024 |
| `Language` | Français par défaut |
| `AllowRemote` | Autorise un serveur distant sans https (réseau de confiance, par exemple celui de Docker) |

- **Confidentialité** : un serveur qui n'est pas cette machine doit être en **https** (sinon `Ai:AllowRemote=true`). L'adresse ne porte ni identifiants ni paramètres. Les journaux ne gardent que le modèle, la durée et le nombre de jetons, jamais le contenu.
- **Sortie JSON contrainte** par un schéma, validée, avec un seul réessai qui dit au modèle ce qui n'allait pas, puis une erreur claire ; les blocs `<think>` sont retirés.
- API (administrateur) : `GET Ai/status` (serveur joignable, modèle installé ou en téléchargement), `POST Ai/test`. `GET Capabilities` annonce `allowAi` et `aiModel`. La page « Étiquettes et domaines » montre l'état et un bouton « Tester le modèle ».

## Assistant de documentation (propositions à relire)

Quand l'assistant d'IA est activé, la page d'une base propose « Assistant de documentation » : des **propositions** de descriptions pour tout ce qui n'en a pas, à relire avant d'être enregistrées.

- **Documenter en lot** : une tâche d'arrière-plan parcourt la base (description de la base, de chaque table et de ses colonnes, puis des relations), avec progression, bouton « Arrêter » et reprise (un élément déjà proposé, accepté ou refusé n'est pas redemandé). Une seule documentation à la fois par base ; trois échecs de suite l'arrêtent avec la raison.
- **Ce que reçoit le modèle** : le nom de la base, de la table, les noms, types, clés et obligations des colonnes, les relations et la description des tables voisines, quelques descriptions déjà saisies (pour imiter le style) et le genre du profil s'il existe. **Jamais de valeur de ligne, jamais une colonne marquée sensible** (ni son nom, ni les relations qui la touchent). Les colonnes partent par lots de quinze.
- **Relecture** : chaque proposition se modifie puis s'accepte, ou se refuse (un refus n'est plus reproposé par le lot). Une proposition est marquée **incertaine** quand le nom est cryptique (`ZX_T01`, `cdcl`) ou que le modèle doute ; « Accepter les propositions sûres » ne prend jamais les incertaines. Accepter écrit la description avec l'origine dans l'historique (« alice (proposé par qwen3:4b-instruct) »). **Une description déjà saisie n'est jamais remplacée**, même si elle est arrivée entre la proposition et l'acceptation.
- **« Suggérer »** : sous une description vide de table ou de colonne, un bouton demande une proposition pour cet élément seul.
- **Colonnes sensibles** : « Détecter les colonnes sensibles » propose, par le nom (e-mail, IBAN, téléphone, date de naissance, mot de passe, nom de personne, adresse, carte), les colonnes à marquer sensibles. Pas de modèle ici, cela marche même sans IA ; chaque proposition se valide.
- API (éditeur) : `GET DescriptionSuggestions/database/{id}?status=`, `POST …/generate`, `GET …/job`, `POST …/job/cancel`, `POST …/suggest` (`targetType`, `targetId`), `POST …/detect-sensitive`, `POST …/{id}/accept` (texte retouché facultatif), `POST …/{id}/reject`, `POST …/accept-confident`.
- Pas encore : glossaire d'abréviations, domaines suggérés, évaluation comparée des modèles.
