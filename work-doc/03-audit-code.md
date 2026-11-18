# Audit du code — GranitDb

_Réalisé le 2026-11-18 (commit `68cbc6e`), **mis à jour le 2026-11-18 sur le commit `ea8d061`** (relations multi-colonnes, audit, webhooks, fenêtres de saisie, exports ; voir « Suivi depuis le premier audit »). Revue **statique** et ciblée du code de l'API et du client, des Dockerfile, de la CI et des tests ; aucun test d'intrusion, aucune analyse dynamique. Les constats renvoient aux fichiers concernés. Complète l'[état des lieux](00-etat-des-lieux.md) et la [roadmap](01-roadmap.md)._

## Périmètre et méthode

| Zone | Taille (lignes `.cs` / `.razor`) | Lu |
|---|---|---|
| `api/` (ASP.NET Core, Dapper) | ≈ 4 500 à l'origine ; le total `api/` + `client/` + `tests/` fait ≈ 25 500 lignes à `ea8d061` | `Program.cs`, dépôt du catalogue, import, contrôleurs de base, tables, domaines, import de documentation, options, connecteurs SQL Server, gestion d'erreurs, Dockerfile |
| `client/` (Blazor WASM, MudBlazor) | ≈ 5 500 | explorateur, cache, pages base et diagramme, modèle de diagramme, client HTTP, nginx |
| `tests/` | ≈ 10 000, ≈ 620 tests (à `ea8d061`) | structure et couverture, pas chaque test |
| CI / Docker | — | `ci.yml`, `docker-compose.yml`, Dockerfile de l'API et du client |

**Limites.** Les sources de packages NuGet ne répondaient pas toutes pendant l'analyse : `dotnet list package --vulnerable` ne signale rien, mais ce résultat n'est qu'indicatif. Le modèle de menaces suppose un déploiement sur un poste de développement ou un réseau de confiance, ce que la documentation du projet demande déjà ; la gravité des points de sécurité se lit **en cas d'exposition au-delà**.

## Synthèse

Le code est **sain pour un outil interne** : couches nettes (contrôleurs → dépôt → connecteurs), transactions bien utilisées, entrées validées par des DTO, requêtes SQL paramétrées, aucun package vulnérable connu, tests nombreux. Les risques sérieux sont presque tous **un seul et même sujet** : l'API n'a ni authentification ni autorisation, et plusieurs routes en écriture ou à effet de bord sont ouvertes à tous. Le reste est de la dette de performance sur de grosses bases (le cas AYB_MECANIQUE : 855 tables, 10 900 colonnes) et de la maintenabilité.

| Gravité | Nombre | Thèmes |
|---|---|---|
| Élevée | 3 (inchangées) | aucune authentification, SQL Server activé par défaut (rebond réseau), CORS ouvert |
| Moyenne | 6 (M1 traitée, M3 et M4 en partie : voir le suivi) | suppression et import sans garde-fou, clés et secrets sur le même volume, lecture complète de la base à chaque page, SQLite sans paramètres de concurrence, etc. |
| Faible | 8 | en-têtes HTTP, journalisation, nettoyage de fichiers, duplication, composants volumineux |

---

## Suivi depuis le premier audit

État des constats au commit `ea8d061` (vérifié dans le code).

| Constat | État | Détail |
|---|---|---|
| E1 Aucune authentification | **Ouvert** | Toujours aucun `[Authorize]`. Les nouvelles routes `GET Audit` et `Webhooks` sont ouvertes comme les autres : tout appelant lit le journal (acteur, chemin, IP) et gère les abonnements. Le port `5105` reste publié sans `127.0.0.1` dans `docker-compose.yml`. |
| E2 SQL Server par défaut | **Ouvert** | `AllowSqlServerSources` vaut toujours `true` (`appsettings.json`, compose) ; `CatalogOptions` n'a toujours pas de liste d'hôtes. |
| E3 CORS ouvert | **Ouvert** | `SetIsOriginAllowed(_ => true)` inchangé. |
| M1 Suppression sans trace | **Traité** | `DatabaseImportService.DeleteDatabaseAsync` écrit une ligne `Deleted` dans `History`, supprime le fichier source géré, déclenche le webhook `database.deleted`, et chaque appel d'écriture est journalisé par `AuditMiddleware`. Le client demande de retaper le nom. Reste : pas de sauvegarde/export automatique avant suppression. |
| M2 Clés et secrets sur le même volume | Ouvert | Inchangé. Les secrets de webhooks utilisent le même `ISecretProtector` : même remarque. |
| M3 `FillAsync` relit la base | **Ouvert** | `TableInfoController.FillAsync` charge encore colonnes, relations et index de toute la base (plus, depuis les relations multi-colonnes, les paires supplémentaires de `GetRelationsOfDatabaseAsync`). |
| M4 Chargement complet côté client | **Traité pour l'explorateur et le panneau de détail** | `GET TableInfo/database/{id}?summary=true` renvoie les tables sans colonnes, index ni relations (et saute `FillAsync`) ; `CatalogCache.EnsureColumnsAsync` lit les colonnes d'une table à l'ouverture (`TableInfo/{id}/columns`, une fois par table). L'arbre n'affiche que 300 tables à la fois (« Afficher la suite ») au lieu de 855 d'un bloc, ce qui remplace `Virtualize` (inadapté à un arbre à hauteurs variables). Reste : la page de la base et le diagramme demandent encore les tables complètes (colonnes et relations), car ils les affichent toutes. |
| M5 SQLite sans WAL | Ouvert | Aucun `journal_mode`/`busy_timeout`. L'écriture d'une ligne d'audit à chaque requête d'écriture augmente un peu la concurrence sur le même fichier. |
| M6 Import de documentation | Ouvert | Pas de plafond ; la route accepte maintenant les relations multi-colonnes (`extra`), sans changement de risque. |
| F2 Journalisation | **Traité en grande partie** | Journal d'audit en base (migration 11) : acteur, méthode, chemin, statut, contrôleur, IP, jamais le corps ; rétention `Audit:RetentionDays` (365 j, purge au plus une fois par heure). Reste : pas d'identifiant de corrélation ni de journal technique des 500. |
| F1 En-têtes de sécurité | **Traité (sauf CSP)** | `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` sur l'API (middleware de `Program.cs`) et dans `client/nginx.conf` (+ `Permissions-Policy`). La CSP reste à régler au déploiement (`wasm-unsafe-eval`, styles MudBlazor, adresse de l'API choisie au démarrage) ; HSTS relève du proxy TLS ; `AllowedHosts` inchangé. |
| F2 Identifiant de requête | **Traité** | Chaque réponse porte `X-Request-Id` (= `traceId` des erreurs) et le journal porte `RequestId` en portée. |
| F3 Message du pilote SQL Server | **Traité** | Le repli ne renvoie plus `e.Message` : numéro d'erreur + renvoi au journal de l'API. |
| F4 Fichiers sources orphelins | **Traité** | `DatabaseImportService.CleanOrphanFilesAsync` au démarrage : supprime les `.db`/`.tmp` non référencés de plus d'une heure. Test : `HardeningTests`. |
| F5 Diagramme en grande taille | Ouvert (effort non faible) | Inchangé. |
| F7 Contrats dupliqués | **Traité** | Le contrat OpenAPI est enregistré (`client/api/openapi.json`), vérifié par `OpenApiContractTests`, et le client en est généré : modèles et appels de l'interface viennent du contrat, plus d'un double écrit à la main. Une évolution de l'API sans régénération échoue au test ; une évolution régénérée qui casse l'interface échoue à la compilation. Reste dupliquée la liste des types de relation (`One-to-Many`…) côté interface. |
| F6 Composants volumineux | **Aggravé** | `DatabaseDetail.razor` 913 lignes, `Diagram.razor` 889, `Explorer.razor` 515 (contre 858 / 703 / 508). Les fenêtres de saisie (`Components/Dialogs/`) ont toutefois sorti la saisie des relations, domaines et webhooks des pages. |
| F8 Swagger en Production | Ouvert | Le compose lance toujours l'API en `Production` alors que l'en-tête renvoie à `/swagger`. |
| F8 `Newtonsoft.Json` | Ouvert | Toujours référencé par `client/GranitDb.Client.csproj`. |
| CI | Ouvert | Toujours ni CodeQL ni `dotnet list package --vulnerable`. |

### Nouveaux points depuis le premier audit

- **Webhooks (SSRF sortant)** : bien maîtrisé. Les cibles loopback ou privées sont refusées à l'enregistrement et de nouveau à la connexion (`WebhookUrlPolicy`, `ConnectCallback`, ce qui couvre le changement de DNS), secret chiffré et jamais renvoyé, signature HMAC `sha256=`. Seule exception : `Webhooks:AllowPrivateHosts`, à laisser à `false` hors poste de développement. Tant que E1 est ouvert, n'importe qui peut cependant créer un abonnement vers un hôte public et recevoir les notifications de schéma.
- **Journal d'audit** : il consigne l'IP et le chemin (donc des identifiants de base et de table) ; il est lisible par tous tant qu'il n'y a pas de rôles. À réserver aux administrateurs avec le jalon 6. Le journal ne stocke pas le corps des requêtes, ce qui évite d'y trouver des secrets de connexion.
- **Relations multi-colonnes** : nouvelle table `RelationColumns` sans clé étrangère SQL, nettoyée à la main dans `CatalogRepository.DeleteAsync` ; couvert par `MultiColumnRelationTests` (suppression d'une colonne, d'une relation, resynchronisation). Point de vigilance : toute nouvelle suppression d'entité doit passer par `DeleteAsync` pour ne pas laisser de paires orphelines.
- **Exports HTML/Markdown/CSV** : le HTML est un fichier autonome avec les données en JSON dans la page ; les descriptions sont échappées avant insertion et le CSV neutralise `=+-@` en tête de cellule. Les appels `/export/` sont aussi journalisés.
- **Fenêtres de saisie** : les formulaires valident côté client puis laissent l'API refuser (`ApiException` affichée dans la fenêtre) ; aucun secret n'est conservé dans l'état de la page après l'enregistrement d'un webhook (le secret n'est jamais relu).

---

## Points forts

- **Pas d'injection SQL visible.** Toutes les valeurs passent par des paramètres Dapper ; les rares noms de colonnes interpolés (`CatalogRepository.BuildWhere`) sont d'abord rapprochés des propriétés mappées de l'entité (`api/Data/CatalogRepository.cs`). Les chaînes de connexion SQL Server sont construites avec `SqlConnectionStringBuilder`, jamais concaténées.
- **Les sources ne sont jamais modifiées.** SQLite s'ouvre en lecture seule, SQL Server avec `ApplicationIntent=ReadOnly`, et les connecteurs ne font que lire le schéma.
- **Fichiers téléversés bien traités** : en-tête SQLite vérifié, taille limitée en flux (pas de lecture complète en mémoire), nom généré côté serveur (GUID), relecture limitée au dossier `sources/` sauf autorisation explicite (`DatabaseImportService`).
- **Secrets de connexion chiffrés** dans le catalogue (Data Protection), jamais renvoyés par l'API (vues de sortie `DatabaseView`, `[JsonIgnore]`).
- **Atomicité** (la lecture du schéma source se fait avant la transaction, donc sans verrou du catalogue pendant l'attente d'un serveur distant) : `InTransactionAsync` rend la synchronisation, les imports, la suppression en cascade et l'import de documentation atomiques et rapides ; imbriquable.
- **Migrations** versionnées, jamais modifiées après publication, tolérantes à des démarrages concurrents.
- **Rendu du texte** : les descriptions sont affichées par Blazor (échappement par défaut, pas de `MarkupString`) ; le convertisseur Dataedo réduit le HTML source à du texte.
- **Traçabilité (nouveau)** : journal d'audit avec rétention, historique des suppressions, webhooks signés et limités aux hôtes publics.
- **Conteneur de l'API non root** (`USER $APP_UID`), CI qui exige les tests SQL Server, dépendances récentes (.NET 10, MudBlazor 9).

---

## Constats

### Élevée

**E1 — Aucune authentification ni autorisation** · `api/Program.cs`, tous les contrôleurs
`app.UseAuthorization()` est appelé mais aucun schéma d'authentification n'est configuré et aucun `[Authorize]` n'existe. N'importe quel appelant peut lire, modifier et **supprimer** tout le catalogue, téléverser des fichiers, déclencher des resynchronisations, et appeler l'import de documentation (`POST import/documentation/{id}`, ajouté récemment : écriture massive de descriptions, domaines et relations).
_Atténuation actuelle_ : documentée (README, roadmap jalon 6), l'API écoute sur `5105` mais est pensée pour un poste ou un réseau de confiance.
_Recommandation_ : jalon 6 (OIDC/JWT + rôles lecteur/éditeur/administrateur). En attendant, **lier le port de l'API à `127.0.0.1`** dans `docker-compose.yml` (`"127.0.0.1:5105:8080"`) et mettre l'API derrière un proxy authentifiant dès qu'elle sort du poste.

**E2 — Sources SQL Server activées par défaut : l'API se connecte où l'appelant le demande** · `api/appsettings.json`, `docker-compose.yml`, `SqlServerImportController`
Depuis le commit « SQL Server sources enabled by default », `Catalog:AllowSqlServerSources` vaut `true` (par défaut dans `appsettings.json` et dans le compose). Le commentaire de `CatalogOptions` dit encore « désactivé par défaut ». Combiné à E1, tout appelant peut faire ouvrir à l'API une connexion vers n'importe quel hôte et port atteignable depuis le serveur (serveurs internes, métadonnées cloud, `host.docker.internal`) : **balayage de ports et rebond réseau** (SSRF), avec en prime la lecture du schéma d'un serveur interne si des identifiants valides sont connus. `TrustServerCertificate` est de plus laissé au choix de l'appelant.
_Recommandation_ : remettre `false` par défaut tant qu'il n'y a pas de rôle administrateur (jalon 6), corriger le commentaire de `CatalogOptions`, et prévoir une **liste blanche d'hôtes** (`Catalog:AllowedSqlServerHosts`) même une fois l'authentification en place.

**E3 — CORS ouvert à toutes les origines** · `api/Program.cs`
`SetIsOriginAllowed(_ => true)` avec toutes méthodes et en-têtes. Aujourd'hui sans cookies ni jetons, le risque est surtout qu'un site tiers visité depuis le poste du développeur puisse lire et écrire dans l'API locale (`localhost:5105`) via le navigateur. Dès qu'une authentification par cookie apparaîtra, cela deviendrait un CSRF cross-origin.
_Recommandation_ : une liste d'origines configurée (`Cors:Origins`), par défaut l'origine du client (`http://localhost:8081`, `http://localhost:41248`).

### Moyenne

**M1 — Suppression sans garde-fou ni trace** · `BaseDbInfoController` (`DELETE {id}`), `CatalogRepository.DeleteAsync`
Supprimer une base emporte en cascade tables, colonnes, relations, domaines, étiquettes et index, d'un seul appel, sans confirmation côté serveur ni écriture dans l'historique (la table `History` ne reçoit que les changements de `Description`/`MetaInfo`). Une base documentée à la main (ou par l'import Herakles : ≈ 5 000 descriptions) se perd irrémédiablement. Le fichier source géré dans `sources/` n'est pas supprimé non plus avec la base (fichier orphelin).
_Recommandation_ : journaliser la suppression (type, id, nom, appelant), supprimer le fichier géré, et prévoir une exportation/sauvegarde du catalogue avant suppression (voir roadmap : bouton de suppression avec confirmation).

**M2 — Clés de chiffrement et données chiffrées sur le même volume** · `Program.cs`, `CatalogOptions.ResolveKeysDirectory`
Par défaut, les clés Data Protection sont dans `DataDirectory/keys`, donc dans le volume `granitdb-data` avec le catalogue qui contient `SourceSecret`. Quiconque obtient le volume (sauvegarde, snapshot) obtient clés **et** secrets : le chiffrement ne protège que contre une fuite partielle. Les clés ne sont pas chiffrées au repos (`ProtectKeysWith…` absent).
_Recommandation_ : sauvegarder les clés ailleurs que le catalogue, ajouter une protection des clés (DPAPI/certificat/Key Vault) en production, documenter la perte des clés (les connexions enregistrées deviennent illisibles → « saisissez-la de nouveau », ce qui est déjà géré).

**M3 — Chaque page de tables relit la base entière** · `TableInfoController.FillAsync`
Pour une page de 50 tables (ou une seule table), `FillAsync` charge **toutes** les colonnes, **toutes** les relations et **tous** les index de la base, puis filtre en mémoire. Sur AYB_MECANIQUE (10 900 colonnes, 1 800 relations) chaque appel `GET TableInfo/{id}` coûte une lecture complète, et le client en enchaîne beaucoup (explorateur, panneau de détail, pages). Même coût en sérialisation pour `GET TableInfo/database/{id}` sans pagination.
_Recommandation_ : filtrer colonnes et index par `TableId IN (…)` pour les pages et les lectures unitaires ; garder la lecture « par base » seulement pour le cas sans pagination.

**M4 — Le client charge tout le catalogue d'une base d'un bloc** · `client/Api/Client.cs`, `CatalogCache`, pages
L'explorateur, la page de la base et le diagramme appellent `GetTablesAsync` (toutes les tables avec leurs colonnes, ≈ plusieurs Mo de JSON pour 855 tables). La page de la base est maintenant paginée à l'affichage et indexée (commit `c88caa3`), mais pas au chargement. Les 855 tables sont aussi rendues dans l'arbre de l'explorateur dès l'expansion de la base (`Explorer.razor`, `VisibleTables`) sans virtualisation.
_Recommandation_ : charger les colonnes à l'ouverture d'une table, `Virtualize` pour l'arbre et la liste des tables, et un point d'API de **résumé** (nom, type, nombre de colonnes, pourcentage) sans les colonnes.

**M5 — SQLite sans réglage de concurrence** · `api/Data/DbConnectionFactory.cs`
Seul `PRAGMA foreign_keys = ON` est posé. Pas de `journal_mode=WAL`, pas de `busy_timeout` explicite : un import ou une synchronisation (transaction longue) bloque les autres écritures et peut faire échouer les lectures concurrentes (« database is locked ») sur le catalogue partagé en conteneur.
_Recommandation_ : WAL + `busy_timeout` (par exemple 5 s) à l'ouverture, et un test de concurrence (import pendant des lectures).

**M6 — Import de documentation : écriture massive sans limite ni contrôle de rôle** · `api/Controllers/DocumentationImportController.cs`
La taille du corps JSON n'est pas bornée au-delà de la limite Kestrel par défaut (≈ 30 Mo), le nombre de tables/colonnes/relations n'est pas plafonné, et le mode `Overwrite` remplace des descriptions saisies à la main. Chaque description passe par `ModifyAsync` (lecture + écriture + historique) : ≈ 5 000 allers-retours dans la transaction, acceptable aujourd'hui (≈ quelques secondes), mais linéaire.
_Recommandation_ : réserver la route aux administrateurs (jalon 6), plafonner la taille et le nombre d'éléments, grouper les écritures. `Overwrite` devrait exiger une confirmation explicite côté interface.

### Faible

**F1 — En-têtes de sécurité HTTP absents** · `client/nginx.conf`, `Program.cs`
Ni `Content-Security-Policy`, ni `X-Content-Type-Options`, ni `X-Frame-Options`/`frame-ancestors`, ni `Referrer-Policy`, ni HSTS. `AllowedHosts: "*"`. À ajouter avec le déploiement réel (une CSP pour Blazor WASM demande `wasm-unsafe-eval`).

**F2 — Journalisation minimale** · `Program.cs`, `GranitExceptionHandler`
Seuls le démarrage et les refus (400/409) sont journalisés ; pas d'identifiant de corrélation, pas de journal des imports, suppressions et resynchronisations. Les erreurs 500 ne sont pas explicitement tracées. Les secrets ne sont pas journalisés (bien), mais il faut garder cette règle quand la journalisation s'étoffera. Voir le jalon « Journal d'audit ».

**F3 — Erreurs SQL Server : message du pilote exposé** · `SqlServerErrors.Describe`
Les messages sont traduits en instructions (bien), mais le détail du pilote peut contenir le nom du serveur ou de la base ; à examiner avant d'ouvrir l'API.

**F4 — Fichier source orphelin après remplacement échoué** · `DatabaseImportService`
Les chemins d'erreur nettoient bien le fichier temporaire et le fichier conservé, mais un arrêt brutal du processus entre la copie et l'enregistrement en base laisse un `.db` dans `sources/`. Prévoir un balayage au démarrage des fichiers non référencés.

**F5 — Le diagramme n'est pas évalué en grande taille** · `client/Diagram/ErDiagram.cs`, `Diagram.razor`
Placement par couches en `O(n × arêtes)` avec relaxation bornée par `n` passes ; sur un domaine de 20 tables c'est instantané, sur 855 tables c'était bloquant (corrigé : une grosse base ouvre sur un domaine). Le rendu SVG d'un domaine très dense (« Article ») est lisible surtout en zoom. Les croisements d'arêtes ne sont pas minimisés (déjà noté dans la roadmap).

**F6 — Composants Razor volumineux** · `DatabaseDetail.razor` (858 lignes), `Diagram.razor` (703), `Explorer.razor` (508)
Beaucoup d'état et de logique dans le `@code`. Les extraire en composants (ligne de colonne, éditeur de relation, panneau de domaines) faciliterait les tests et limiterait les rendus (`ShouldRender`).

**F7 — Chaînes en dur et validations dupliquées** · client/API
Messages en français dispersés, longueurs et listes (`RelationTypes`, `One-to-Many`…) répétées côté client et côté API ; `Client models mirror the API models` (CLAUDE.md) : toute évolution d'un côté sans l'autre se découvre à l'exécution. Un projet partagé de contrats (ou un schéma OpenAPI généré) réduirait ce risque.

**F8 — Divers**
- `Newtonsoft.Json` est référencé par le client (`GranitDb.Client.csproj`) mais aucun usage n'apparaît dans le code : à retirer si rien ne l'exige.
- Le service worker met en cache le client : après un redéploiement, une ancienne version peut rester affichée jusqu'à fermeture de tous les onglets. Le prévoir dans la procédure de mise à jour (ou afficher un bandeau « nouvelle version »).
- `Swagger` n'est actif qu'en Development (bien), mais le compose lance l'API en `Production` alors que son en-tête et le README (ligne « docker compose up ») renvoient à `http://localhost:5105/swagger`, qui n'y existe pas.

---

## Qualité et tests

- **Couverture** : ≈ 620 tests (API d'intégration sur Kestrel réel, dépôt, migrations, synchronisation, import, connecteurs SQLite/SQL Server via Testcontainers, composants bUnit, modèle de diagramme). Très bon niveau pour la taille du projet.
- **Trous** : pas de test de **charge** ni de **concurrence** (SQLite, imports parallèles), pas de test de **sécurité** (accès non autorisé : sans objet tant qu'il n'y a pas de rôles), pas de test de performance sur une grosse base (un test synthétique de 800 tables/10 000 colonnes sur `TableInfo` et le rendu de page éviterait les régressions vues ici), pas de test de bout en bout navigateur (la vérification dans Chrome reste manuelle).
- **Fragilité** : les tests bUnit supposent des panneaux dépliés (`ExpandAllTables`) — cohérent avec le rendu paresseux, mais un changement de la pagination d'affichage (50 par page) peut les surprendre.
- **Durée** : la suite complète dure ≈ 6 minutes (images SQL Server) ; les tests SQL Server sont ignorés sans Docker hors CI, ce qui est bien documenté.
- **CI** : build, tests (SQL Server obligatoires), images Docker ; pas d'analyse statique de sécurité (CodeQL/`dotnet list package --vulnerable`), pas de vérification de format.

---

## Plan d'action recommandé

| # | Action | Gravité | Effort | Lié à |
|---|---|---|---|---|
| 1 | Lier l'API à `127.0.0.1` dans le compose, remettre `AllowSqlServerSources=false` par défaut, corriger le commentaire de `CatalogOptions` | Élevée | S | E1, E2 |
| 2 | Restreindre CORS aux origines du client (configurable) | Élevée | S | E3 |
| 3 | Authentification OIDC/JWT + rôles, route d'import et de suppression réservées aux administrateurs, liste blanche d'hôtes SQL Server | Élevée | L | E1, E2, M6, jalon 6 |
| 4 | ~~Journaliser les suppressions et les imports ; supprimer le fichier source avec la base ; bouton de suppression avec confirmation~~ **fait** (reste : export avant suppression) | Moyenne | M | M1 |
| 5 | **Priorité confirmée** : filtrer `FillAsync` par table, résumé de tables sans colonnes, chargement des colonnes à l'ouverture, `Virtualize` | Moyenne | M | M3, M4 |
| 6 | SQLite : WAL + `busy_timeout`, test de concurrence | Moyenne | S | M5 |
| 7 | Séparer les clés Data Protection du catalogue et les protéger au repos | Moyenne | M | M2 |
| 8 | Plafonner l'import de documentation, grouper les écritures | Moyenne | S | M6 |
| 9 | En-têtes de sécurité nginx/API, CSP (le journal d'audit est fait) | Faible | M | F1, F2 |
| 10 | Test de performance sur une base synthétique de ≈ 850 tables ; CodeQL en CI | Faible | M | F5, tests |
| 11 | Découper `DatabaseDetail`/`Diagram`/`Explorer` ; contrats partagés API ↔ client | Faible | L | F6, F7 |

_Effort : S = quelques heures, M = quelques jours, L = plusieurs jours à semaines._

## Ce qui n'a pas été audité

Le détail de `SchemaSynchronizer`, `SchemaDiffer` et `ImpactAnalyzer` (logique métier, couverte par des tests), la totalité des pages et composants du client, les scripts de démarrage Docker (`docker-entrypoint.sh`), le contenu exact des migrations SQL, la conformité RGPD des données documentées (colonnes sensibles marquées à la main), l'accessibilité réelle (la roadmap l'indique comme à auditer) et toute analyse dynamique (fuzzing, test d'intrusion).
