# Roadmap fonctionnelle — GranitDb

_Basée sur l'[état des lieux](00-etat-des-lieux.md). Les jalons sont ordonnés par dépendance, sans estimation de durée : à chiffrer avec l'équipe._

Priorités : **P0** bloquant · **P1** cœur de valeur · **P2** confort · **P3** exploratoire.

## Avancement (mis à jour le 2026-11-18)

Jalons **1** (SQLite sans serveur), **2** (fondations : migrations, Docker, CI), **4** (interface de consultation), **3** (API catalogue complète : routes `/api/v1`, entrées validées, vues de sortie, filtres, historique, impact, import et resynchronisation d'une base SQL Server depuis l'interface avec connexion chiffrée), **5** (édition et gouvernance : descriptions et notes modifiables, relations manuelles, étiquettes, domaines et diagrammes par domaine, colonnes sensibles, complétude, aperçu de la synchronisation) et **7** (visualisation : diagramme entité-relation interactif, analyse d'impact, exports SVG/PNG/Mermaid) : terminés à 100 %, retirés de ce document (leur contenu est dans l'historique git).

| Ordre | Jalon | État | Pourquoi à cette place |
|---|---|---|---|
| 1 | 6 — Sécurité et multi-utilisateurs | ⬜ Non commencé | l'API accepte des uploads sans authentification : à faire avant toute exposition et avant d'ouvrir l'édition |
| 2 | 11 — Refonte UI/UX du client | 🟢 Presque terminé | l'espace de travail est en place ; reste la validation avec de vrais utilisateurs et appareils (tests, lecteur d'écran, petits écrans) |
| 3 | 8 — Autres moteurs | ⬜ Non commencé | élargit les sources, une fois le cœur stabilisé |
| 4 | 9 — Export et intégrations | ⬜ Non commencé | |
| 5 | 10 — Fonctions avancées | ⬜ Non commencé | exploratoire |

**Prochain jalon : 6 (sécurité).** Les jalons ci-dessous sont classés dans l'ordre où les traiter ; leurs numéros sont conservés pour ne pas être confondus avec les jalons 1, 2, 3, 4, 5 et 7 déjà terminés. Depuis les jalons 1 et 2, l'API accepte des uploads de fichiers, expose `docker-compose` sur le réseau et n'a toujours ni authentification ni CORS restreint : elle ne doit pas être joignable hors poste de dev ou réseau de confiance.

Chiffres : 536 tests sous .NET 10 (30 tests SQL Server nécessitent Docker), CI GitHub Actions, pile `docker compose` vérifiée en SQLite et en SQL Server. Catalogue en version 9. L'interface (espace de travail, diagramme, glisser-déposer, édition en place) est couverte par des tests bUnit et de modèle ; sa vérification dans un vrai navigateur reste manuelle.

---

## Jalon 6 — Sécurité et multi-utilisateurs (P1)

À planifier **avant toute exposition hors réseau de confiance** (l'upload de fichiers du jalon 1 en fait partie).

- [ ] Authentification (OIDC / JWT) côté API et client. _L'historique des modifications inscrit déjà l'auteur dès que l'appelant est identifié (`User.Identity.Name`) : il se remplira tout seul._
- [ ] Rôles : lecteur, éditeur, administrateur.
- [ ] Autoriser l'enregistrement de sources SQL Server (`Catalog:AllowSqlServerSources`) selon un rôle (administrateur) plutôt que pour tous les appelants.
- [ ] Mise en œuvre de `IsPublic` : bases publiques lisibles sans compte.
- [ ] Restreindre CORS aux origines configurées.
- [ ] Secrets de connexion hors fichier, chiffrés dans le catalogue. _Fait pour les sources SQL Server enregistrées depuis l'interface (chiffrées, jamais renvoyées par l'API, clés dans `Catalog:KeysDirectory`). Reste `Catalog:StartupSources`, dont la chaîne de connexion reste en clair dans la configuration, et le coffre de clés (Key Vault, DPAPI…) pour ne pas laisser les clés en fichiers._
- [ ] Journal d'audit.

## Jalon 11 — Refonte de l'UI/UX du client (P1)

Fait : un **espace de travail** à la Postman (explorateur à gauche, onglets, panneau de détail à droite, palette Ctrl+K, favoris et domaines en collections, accueil guidé, édition en place à enregistrement automatique, confirmations annulables, thème, raccourcis, repères d'accessibilité). Voir le README et [le protocole de validation](02-parcours-utilisateurs.md). Reste ce qui ne peut pas se faire sans utilisateurs ni appareils :

- [ ] **Tests utilisateurs** : dérouler les quatre parcours du [protocole](02-parcours-utilisateurs.md) (importer une base, retrouver une colonne, documenter une table, exporter un diagramme) avec de vraies personnes, puis corriger ce qu'elles n'ont pas trouvé seules.
- [ ] **Accessibilité vérifiée** : la structure est en place (rôles, étiquettes, focus visible, lien d'évitement, mouvement réduit, contrastes choisis pour 4,5:1) et testée par composants, mais pas encore auditée avec un lecteur d'écran réel ni avec un outil de mesure de contraste.
- [ ] **Petits écrans** : les tiroirs deviennent des panneaux superposés sous 960 px (explorateur) et 1280 px (détails), mais la disposition n'a pas été essayée sur de vrais téléphones et tablettes.
- [ ] **Diagramme et barre latérale** : le diagramme s'ouvre dans un onglet et un domaine de l'explorateur l'ouvre sur ce domaine ; la liste des tables à afficher reste dans la page du diagramme (elle n'est pas encore l'arbre de la barre latérale).
- [ ] **Édition en place des relations et des domaines** : les relations manuelles et les noms de domaine se modifient avec leurs propres champs ; à aligner sur le même geste (clic, saisie, quitter le champ).
- [ ] **Domaines configurables (tables et relations)** : choisir précisément ce qu'un domaine affiche. Aujourd'hui le diagramme d'un domaine montre ses tables **plus** toutes les tables voisines reliées (en pointillés), si bien que le domaine « Article » (20 tables dans la documentation officielle) en dessine bien davantage. À faire : activer ou désactiver les tables voisines par domaine (par défaut seulement les tables du domaine), retenir par domaine les relations affichées ou masquées (cases à cocher ou clic sur un lien), et conserver ce choix avec la place des tables.
- [ ] **Supprimer une base documentée** : bouton (avec confirmation explicite) pour retirer une base du catalogue, avec ses tables, colonnes, relations, domaines et étiquettes liés, depuis la liste des bases et la page de la base. L'API sait déjà supprimer en cascade (`DELETE /DatabaseInfo/{id}`) ; le client n'a pas encore l'action. Prévoir l'annulation (ou un avertissement net : une base très documentée, comme l'import Herakles, représente beaucoup de saisie) et ne jamais toucher à la base source.
- [ ] **Gestion des étiquettes et des domaines** : écran pour renommer, supprimer et colorer les étiquettes et les domaines (l'API sait déjà le faire, le client non).
- [ ] **Aperçu des différences au remplacement d'un fichier** (`PUT /import/sqlite/{id}`) : aujourd'hui seul « Resynchroniser » montre ce qui va changer avant de l'appliquer.

## Jalon 8 — Autres moteurs (P2)

- [ ] Connecteurs PostgreSQL et MySQL (via `ISchemaConnector`).
- [ ] Synchronisation planifiée et notification des changements de schéma.

## Jalon 9 — Export et intégrations (P2/P3)

- [ ] Export de la documentation : Markdown, HTML statique, PDF, CSV/Excel. **Un export HTML/Markdown autonome complète bien le mode sans serveur.**
- [ ] Client typé généré depuis OpenAPI (remplace le projet `interfaces` si jugé redondant).
- [ ] Webhooks sur changement de schéma.
- [ ] Écriture des descriptions vers la base source (propriétés étendues SQL Server / `COMMENT ON`) ; impossible en SQLite, qui n'a pas de commentaires de schéma natifs.

## Jalon 10 — Fonctions avancées (P3, exploratoire)

- [ ] Suggestions de descriptions par IA à partir des noms et types, avec relecture humaine.
- [ ] Glossaire métier relié aux colonnes.
- [ ] Lignage de données entre bases.
- [ ] Profilage léger des colonnes (cardinalité, taux de nuls), sans exposer de données brutes.

---

## Dette connue (issue des jalons 1 et 2)

### Soldée (2026-11-18)

| Sujet | Résolution |
|---|---|
| Index SQLite non lus | Lecture des index créés et des contraintes `UNIQUE` (SQLite via `pragma_index_list`, SQL Server via `sys.indexes`, colonnes `INCLUDE` exclues, clé primaire exclue), stockés dans une table `Indexes` (migration 6), synchronisés (ajout, mise à jour, suppression, cascade), exposés par `TableInfo` et affichés dans le client. Les descriptions d'index saisies sont conservées. |
| Ordre des colonnes non conservé | Colonne `Position` (migration 5), mise à jour par la synchro sans recréer les colonnes ; l'API et le client trient par position. |
| `SqlServerSchemaConnector` ignorait le schéma SQL | Les tables hors `dbo` sont préfixées (`sales.Orders`) : deux tables de même nom dans deux schémas ne se confondent plus, clés étrangères comprises. Testé sur SQL Server. |
| `GetAll` sans pagination | `GET /DatabaseInfo?skip=0&take=50` (take de 1 à 1000, tri par nom) et en-tête `X-Total-Count` (exposé en CORS). Sans paramètre, le comportement est inchangé. Le client n'utilise pas encore la pagination. |
| Glisser-déposer non vérifié | Vérifié dans le navigateur avec un vrai `File`/`DataTransfer` : dépôt d'une base (redirection vers le détail, colonnes dans l'ordre, index, relations) et dépôt d'un faux fichier (message du serveur affiché, pas de redirection). |
| Aucun test du client | `ApiClientTests` (client HTTP : listes, 404, multipart, erreurs `ProblemDetails`, repli sur le code HTTP) et `ClientPagesTests` (bUnit : liste, zone de dépôt, upload, erreur d'upload, détail, filtre, resynchronisation). |
| Extension du fichier importé non contrôlée | **Abandonné volontairement** : l'en-tête `SQLite format 3` est le vrai contrôle ; refuser une extension inhabituelle gênerait sans protéger davantage. |
| Migration 4 : lignes orphelines supprimées ; `Gabi.Base` / `config.json` supprimés | Comportements documentés (README, `CLAUDE.md`), pas de correction à prévoir. |

### Restante

| Sujet | Jalon |
|---|---|
| Aucune authentification, CORS ouvert à toutes les origines, upload de fichiers accessible à tous | 6 |
| `StartupSources` stocke les chaînes de connexion SQL Server en clair dans la configuration (une source enregistrée depuis l'interface, elle, est chiffrée) | 6 |
| L'aperçu des différences n'existe que pour la resynchronisation (`POST /DatabaseInfo/{id}/sync/preview`), pas pour le remplacement d'un fichier (`PUT /import/sqlite/{id}`) | 11 |
| Les étiquettes et domaines n'ont pas d'écran de gestion complet : pas de renommage ni de couleur d'étiquette dans le client (l'API les gère), ni de couleur de domaine | 11 |
| Analyse d'impact limitée à la base de l'élément (une relation manuelle entre deux bases n'est pas suivie) ; le diagramme place les tables par couches sans minimiser les croisements de relations | — |
| Index : seuls les noms de colonnes sont stockés (pas l'ordre ASC/DESC, ni les prédicats d'index partiels, ni les colonnes `INCLUDE`) | 8 |

---

## Ordre recommandé

```
J1, J2, J3, J4, J5, J7 terminés (J11 : validation utilisateurs à faire) → J6 Sécurité
                                                        └→ J8 Autres moteurs → J9 Export/intégrations → J10 Avancé
```

Le livrable « documenter un fichier SQLite sans serveur » (jalon 1) est en place et déployable en une commande (jalon 2). Pour qu'il soit utilisable par d'autres personnes que son auteur, l'étape suivante la plus utile est **J6 (sécurité)** : GranitDb sait désormais documenter (descriptions, relations, étiquettes, domaines, colonnes sensibles) mais n'a toujours ni compte ni droits, donc personne ne devrait pouvoir l'ouvrir à d'autres. Vient ensuite **J11 (refonte de l'UI/UX)**.
