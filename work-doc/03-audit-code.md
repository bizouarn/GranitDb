# Audit du code — constats ouverts

_Audit statique réalisé le 2026-11-18 (commits `68cbc6e` puis `ea8d061`), allégé le 2026-11-23 : ne restent que les constats **toujours ouverts**, revérifiés dans le code. Les points traités (suppression tracée, journal d'audit, authentification et rôles, CORS configurable, en-têtes de sécurité, identifiant de requête, nettoyage des fichiers orphelins, contrat OpenAPI et client généré, résumé de tables pour l'explorateur) sont dans l'historique git._

Le modèle de menaces suppose un poste de développement ou un réseau de confiance ; la gravité se lit **en cas d'exposition au-delà**. Revue statique seulement, sans test d'intrusion.

## Élevée

**E1 — API ouverte tant que `Auth:Enabled` est faux** · `api/Program.cs`, `docker-compose.yml`
L'authentification existe (voir [docs/authentification.md](../docs/authentification.md)) mais reste désactivée par défaut. Le compose publie `5105:8080` sans `127.0.0.1`. _Recommandation_ : lier le port à `127.0.0.1` dans le compose par défaut, et n'ouvrir l'API qu'avec l'authentification activée.

**E2 — Sources serveur activées par défaut** · `api/appsettings.json`, `docker-compose.yml`
`Catalog:AllowSqlServerSources` vaut `true` (couvre SQL Server, PostgreSQL et MySQL). Sans authentification, tout appelant peut faire ouvrir à l'API une connexion vers n'importe quel hôte atteignable depuis le serveur : balayage de ports et rebond réseau. `TrustServerCertificate` reste au choix de l'appelant. _Recommandation_ : `false` par défaut tant que l'authentification est désactivée, et une liste blanche d'hôtes (`Catalog:AllowedSqlServerHosts`) même avec rôles.

## Moyenne

**M2 — Clés de chiffrement sur le même volume que les données chiffrées** · `Program.cs`, `CatalogOptions.ResolveKeysDirectory`
Par défaut, les clés Data Protection sont dans `DataDirectory/keys`, avec le catalogue qui contient `SourceSecret` (et les secrets de webhooks). Quiconque obtient le volume obtient clés et secrets. `Catalog:KeysProtection` (Dpapi / certificat) existe mais est `None` par défaut. _Recommandation_ : séparer le dossier des clés du catalogue et activer la protection en production.

**M3 — Chaque lecture de table relit la base entière** · `TableInfoController.FillAsync`
`FillAsync` charge colonnes, relations (et paires supplémentaires) et index de toute la base, puis filtre en mémoire. Sur une grosse base (855 tables, 10 900 colonnes), chaque `GET TableInfo/{id}` coûte une lecture complète. _Recommandation_ : filtrer par `TableId IN (…)` pour les pages et les lectures unitaires.

**M4 — La page de la base et le diagramme chargent tout** · `client/api/Client.cs`, `CatalogCache`
L'explorateur et le panneau de détail passent par le résumé (`?summary=true`) et chargent les colonnes à l'ouverture ; la page de la base et le diagramme demandent encore les tables complètes, car ils les affichent toutes. _Recommandation_ : chargement progressif ou résumé + colonnes à la demande pour ces deux pages.

**M5 — SQLite sans réglage de concurrence** · `api/Data/DbConnectionFactory.cs`
Seul `PRAGMA foreign_keys = ON` est posé : ni `journal_mode=WAL`, ni `busy_timeout`. Un import ou une synchronisation (transaction longue) peut bloquer les autres écritures et faire échouer des lectures (« database is locked »), d'autant que chaque écriture ajoute une ligne d'audit. _Recommandation_ : WAL + `busy_timeout` (≈ 5 s) et un test de concurrence.

**M6 — Import de documentation : écriture massive sans plafond** · `api/Controllers/DocumentationImportController.cs`
Pas de plafond sur la taille du corps (limite Kestrel par défaut ≈ 30 Mo) ni sur le nombre de tables, colonnes et relations ; chaque description passe par `ModifyAsync` (≈ 5 000 allers-retours dans la transaction, linéaire) ; `Overwrite` remplace des descriptions saisies à la main. _Recommandation_ : plafonner, grouper les écritures, confirmation explicite de `Overwrite` dans l'interface.

## Faible

- **F1 — CSP** : les en-têtes `X-Content-Type-Options`, `X-Frame-Options` et `Referrer-Policy` sont posés, mais pas de `Content-Security-Policy` (Blazor WASM demande `wasm-unsafe-eval`, MudBlazor des styles en ligne). HSTS relève du proxy TLS ; `AllowedHosts` est resté à `*`.
- **F2 — Journal technique** : le journal d'audit existe, mais pas de journal technique des erreurs 500 ni d'identifiant de corrélation propagé au-delà de `X-Request-Id`.
- **F5 — Diagramme en grande taille** : placement par couches sans minimisation des croisements ; un domaine très dense n'est lisible qu'en zoom (une grosse base s'ouvre sur un domaine plutôt que sur toutes les tables).
- **F6 — Composants Razor volumineux** : `DatabaseDetail.razor`, `Diagram.razor` et `Explorer.razor` dépassent 500 lignes (≈ 900 pour les deux premiers) ; les extraire en composants faciliterait les tests et limiterait les rendus.
- **F7 — Liste des types de relation** (`One-to-Many`…) encore dupliquée côté interface.
- **F8 — Divers** :
  - `Newtonsoft.Json` est référencé par `client/GranitDb.Client.csproj` sans usage apparent : à retirer si rien ne l'exige ;
  - le compose lance l'API en `Production` alors que son en-tête renvoie à `/swagger`, qui n'y existe pas ;
  - le service worker met le client en cache : après un redéploiement, une ancienne version peut rester affichée jusqu'à fermeture des onglets (prévoir un bandeau « nouvelle version »).
- **Export avant suppression** : la suppression d'une base est tracée et demande de retaper le nom, mais n'exporte rien avant.

## Qualité et tests : trous

- Pas de test de **charge** ni de **concurrence** (SQLite, imports parallèles).
- Pas de test de **performance** sur une grosse base (un test synthétique de ≈ 800 tables / 10 000 colonnes sur `TableInfo` et le rendu éviterait les régressions).
- Pas de test de bout en bout dans un navigateur (la vérification reste manuelle).
- **CI** : ni CodeQL, ni `dotnet list package --vulnerable`, ni vérification de format.
- La suite complète dure plusieurs minutes (images de serveurs SQL) ; ces tests sont ignorés sans Docker hors CI.

## Plan d'action

| # | Action | Gravité | Effort | Lié à |
|---|---|---|---|---|
| 1 | Lier l'API à `127.0.0.1` dans le compose, `AllowSqlServerSources=false` par défaut sans authentification | Élevée | S | E1, E2 |
| 2 | Liste blanche d'hôtes pour les sources serveur | Élevée | M | E2 |
| 3 | Filtrer `FillAsync` par table, chargement progressif de la page de la base et du diagramme | Moyenne | M | M3, M4 |
| 4 | SQLite : WAL + `busy_timeout`, test de concurrence | Moyenne | S | M5 |
| 5 | Séparer et protéger les clés Data Protection | Moyenne | M | M2 |
| 6 | Plafonner l'import de documentation, grouper les écritures | Moyenne | S | M6 |
| 7 | CSP, journal technique des 500 | Faible | M | F1, F2 |
| 8 | Test de performance sur une base synthétique ; CodeQL en CI | Faible | M | tests |
| 9 | Découper `DatabaseDetail` / `Diagram` / `Explorer` ; retirer `Newtonsoft.Json` | Faible | L | F6, F8 |

_Effort : S = quelques heures, M = quelques jours, L = plusieurs jours à semaines._

## Non audité

Le détail de `SchemaSynchronizer`, `SchemaDiffer` et `ImpactAnalyzer` (logique métier couverte par des tests), la totalité des pages du client, les scripts de démarrage Docker, le contenu exact des migrations SQL, la conformité RGPD des données documentées, l'accessibilité réelle et toute analyse dynamique.
