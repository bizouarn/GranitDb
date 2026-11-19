# Roadmap fonctionnelle — GranitDb

_Basée sur l'[état des lieux](00-etat-des-lieux.md). Les jalons sont ordonnés par dépendance, sans estimation de durée : à chiffrer avec l'équipe._

Priorités : **P0** bloquant · **P1** cœur de valeur · **P2** confort · **P3** exploratoire.

## Avancement (mis à jour le 2026-11-18)

Jalons **1** (SQLite sans serveur), **2** (fondations : migrations, Docker, CI), **4** (interface de consultation), **3** (API catalogue complète : routes `/api/v1`, entrées validées, vues de sortie, filtres, historique, impact, import et resynchronisation d'une base SQL Server depuis l'interface avec connexion chiffrée), **5** (édition et gouvernance : descriptions et notes modifiables, relations manuelles, étiquettes, domaines et diagrammes par domaine, colonnes sensibles, complétude, aperçu de la synchronisation), **7** (visualisation : diagramme entité-relation interactif, analyse d'impact, exports SVG/PNG/Mermaid) **9** (exports HTML, Markdown, CSV, Excel natif et PDF imprimable ; client typé généré depuis OpenAPI, adopté par l'interface, projet `interfaces` supprimé) et **6** (sécurité : connexion GitHub et OpenID Connect générique configurable, session par cookie, rôles lecteur/éditeur/administrateur, CORS restreint, journal d'audit réservé aux administrateurs, bases publiques lisibles sans compte, secrets de sources hors fichier, clés chiffrées par DPAPI ou certificat) : terminés à 100 %, retirés de ce document (leur contenu est dans l'historique git).

| Ordre | Jalon | État | Pourquoi à cette place |
|---|---|---|---|
| 1 | 12 — Gestion des rôles et des droits (écran d'administration, droits par base et par domaine, jetons) | ⬜ Non commencé | prolonge le jalon 6 : les rôles sont encore déclarés en configuration et figés à la connexion |
| 2 | 11 — Refonte UI/UX du client | 🟢 Presque terminé | l'espace de travail est en place ; reste la validation avec de vrais utilisateurs et appareils (tests, lecteur d'écran, petits écrans) |
| 3 | 8 — Autres moteurs | ⬜ Non commencé | élargit les sources, une fois le cœur stabilisé |
| 4 | 10 — Fonctions avancées | ⬜ Non commencé | exploratoire |

**Prochain jalon : 12 (gestion des rôles et des droits), puis 11 (validation de l'interface).** L'API peut exiger une connexion (`Auth:Enabled`, voir le README) mais **reste ouverte tant que cette option est fausse** (valeur par défaut, pour le poste de développement) : ne l'exposer hors poste de dev ou réseau de confiance qu'avec l'authentification activée. Les jalons ci-dessous sont classés dans l'ordre où les traiter ; leurs numéros sont conservés pour ne pas être confondus avec les jalons déjà terminés.

Chiffres : 611 tests sous .NET 10 hors SQL Server (les tests SQL Server nécessitent Docker), CI GitHub Actions, pile `docker compose` vérifiée en SQLite et en SQL Server. Catalogue en version 12. L'interface (espace de travail, diagramme, glisser-déposer, édition en place) est couverte par des tests bUnit et de modèle ; sa vérification dans un vrai navigateur reste manuelle.

---

## Jalon 12 — Gestion des rôles et des droits (P1)

Le jalon 6 donne trois rôles globaux (lecteur, éditeur, administrateur) **déclarés en configuration** et calculés à la connexion. Ce jalon les rend **gérables depuis l'application** et plus fins.

- [ ] **Écran d'administration des accès** (M) : la liste des comptes qui se sont connectés (login, fournisseur, rôle, dernière connexion, rôle obtenu par liste ou par groupe), réservée aux administrateurs. Les comptes sont enregistrés à la première connexion (table `Users`, migration à ajouter).
- [ ] **Attribuer un rôle dans l'interface** (M) : un administrateur change le rôle d'un compte sans toucher à `appsettings` ni redémarrer ; ces attributions s'ajoutent aux listes et groupes de la configuration (la configuration garde le premier administrateur, jamais retirable depuis l'interface). Même principe pour **bloquer / débloquer** un compte.
- [ ] **Correspondance groupes → rôles éditable** (M) : gérer dans l'interface les groupes GitHub (`organisation`, `organisation/équipe`) et les groupes du fournisseur OpenID Connect qui donnent chaque rôle, avec un aperçu « qui obtiendrait quoi » avant d'enregistrer.
- [ ] **Prise en compte immédiate d'un changement de rôle** (M) : aujourd'hui le rôle vit dans le cookie jusqu'à la prochaine connexion. Relire le rôle (et le blocage) du compte à chaque requête ou toutes les quelques minutes, pour qu'une rétrogradation ou un blocage agisse tout de suite.
- [ ] **Droits par base** (L) : un rôle peut être limité à certaines bases (ex. éditeur de « Boutique », lecteur de « RH »), avec des **groupes de bases** ; l'explorateur et la recherche ne montrent que les bases accessibles. Les bases publiques deviennent un cas particulier (« tout le monde en lecture »).
- [ ] **Droits par domaine** (M) : éditeur d'un domaine seulement (ex. « Ventes »), pour déléguer la documentation à ses responsables métier.
- [ ] **Colonnes sensibles selon le rôle** (M) : masquer les descriptions ou valeurs d'exemple des colonnes marquées sensibles aux lecteurs, les montrer aux éditeurs et administrateurs.
- [ ] **Demande d'accès** (M) : un compte sans rôle peut demander l'accès (message), les administrateurs voient les demandes en attente et acceptent ou refusent depuis l'écran d'administration.
- [ ] **Jetons d'API / comptes de service** (M) : un jeton à portée et rôle limités, révocable, pour la CI ou un script (resynchronisation, import de documentation) sans session de navigateur ; le jeton n'est montré qu'une fois et n'est stocké que haché.
- [ ] **Matrice des droits visible** (S) : une page « Qui peut faire quoi » dans l'interface (rôles × actions), générée depuis les attributs de l'API pour ne pas dériver du code.
- [ ] **Audit des droits** (S) : le journal d'audit inscrit chaque changement de rôle, de blocage et de groupe (qui, pour qui, avant/après), et chaque refus 403.
- [ ] Tests : changement de rôle appliqué sans reconnexion, blocage, droits par base et par domaine (lecture et écriture refusées hors périmètre), jeton révoqué refusé, dernier administrateur non retirable.

## Jalon 11 — Refonte de l'UI/UX du client (P1)

_Suite détaillée : [roadmap pour rendre les interfaces plus intuitives](04-roadmap-interfaces-intuitives.md) (légendes, vocabulaire, démarrage, saisie en lot, fenêtres de saisie, diagramme, accessibilité, tests utilisateurs)._

Fait : un **espace de travail** à la Postman (explorateur à gauche, onglets, panneau de détail à droite, palette Ctrl+K, favoris et domaines en collections, accueil guidé, édition en place à enregistrement automatique, confirmations annulables, thème, raccourcis, repères d'accessibilité). Depuis : **suppression d'une base** (confirmation en retapant son nom, historique, fichier conservé supprimé), **domaines configurables** (tables voisines en option, relations affichées ou masquées par domaine, plan Dataedo importé), **écran Étiquettes et domaines** (`/manage` : renommer, colorer, supprimer) et **aperçu du remplacement d'un fichier SQLite** avant de l'appliquer. Voir le README et [le protocole de validation](02-parcours-utilisateurs.md). Reste ce qui ne peut pas se faire sans utilisateurs ni appareils :

- [ ] **Tests utilisateurs** : dérouler les quatre parcours du [protocole](02-parcours-utilisateurs.md) (importer une base, retrouver une colonne, documenter une table, exporter un diagramme) avec de vraies personnes, puis corriger ce qu'elles n'ont pas trouvé seules.
- [ ] **Accessibilité vérifiée** : la structure est en place (rôles, étiquettes, focus visible, lien d'évitement, mouvement réduit, contrastes choisis pour 4,5:1) et testée par composants, mais pas encore auditée avec un lecteur d'écran réel ni avec un outil de mesure de contraste.
- [ ] **Petits écrans** : les tiroirs deviennent des panneaux superposés sous 960 px (explorateur) et 1280 px (détails), mais la disposition n'a pas été essayée sur de vrais téléphones et tablettes.
- [ ] **Diagramme et barre latérale** : le diagramme s'ouvre dans un onglet et un domaine de l'explorateur l'ouvre sur ce domaine ; la liste des tables à afficher reste dans la page du diagramme (elle n'est pas encore l'arbre de la barre latérale).
- [ ] **Relations affichées par domaine, comme dans Dataedo** : l'import de la documentation masque déjà les relations que le plan d'origine ne trace pas (vérifié sur AYB_MECANIQUE). Reste : (1) pour un domaine neuf, ne rien tracer d'office au-delà des clés étrangères détectées, ou proposer « Masquer les relations indirectes » ; (2) un bouton « Réinitialiser comme dans Dataedo » quand la documentation d'origine est connue.
- [ ] **Fenêtres de saisie** : _faites : relation (nouvelle / modifier), domaine (nouveau / modifier), webhook, confirmation commune (webhook). Restent la table saisie à la main (modèle à décider), la grille de colonnes, l'étiquette, l'assistant d'import et la confirmation commune partout._ Besoin d'origine : créer et modifier dans des fenêtres dédiées plutôt que dans des formulaires insérés dans la page — relation (recherche des colonnes, paires multi-colonnes, aperçu, contrôle avant envoi), domaine (liste à deux colonnes, ajout en lot), colonnes en grille, webhook, assistant d'import, confirmation commune, et, à décider, la saisie d'une table qui n'existe pas dans la source. Règles communes (focus, Échap, validation en ligne, brouillon, accessibilité) et détail au [lot 9](04-roadmap-interfaces-intuitives.md).
- [ ] **Éditer les tables d'un domaine** : l'écran de gestion le permet déjà (membres, retrait, recherche, ajout). Reste le même éditeur dans le diagramme, l'ajout en lot (par préfixe de nom, par table liée) et l'aperçu du nombre de relations que le changement fait apparaître.
- [ ] **Relations multi-colonnes : suite** : le modèle, la synchronisation, l'import, les exports et l'affichage client sont faits. Le surlignage est fait : choisir une colonne d'une clé composite met en évidence toutes les autres dans le diagramme. Reste les analyses d'impact et décomptes de relations masquées comptés par relation (plus par paire).

## Jalon 8 — Autres moteurs (P2)

- [ ] Connecteurs PostgreSQL et MySQL (via `ISchemaConnector`).
- [ ] Synchronisation planifiée et notification des changements de schéma.

## Jalon 10 — Fonctions avancées (P3, exploratoire)

- [ ] Suggestions de descriptions par IA à partir des noms et types, avec relecture humaine.
- [ ] Glossaire métier relié aux colonnes.
- [ ] Lignage de données entre bases.
- [x] Profilage léger des colonnes (cardinalité, taux de nuls), sans exposer de données brutes : fait (`Catalog:AllowProfiling`, comptes calculés par la source, page d'une table, API réservée aux administrateurs). Suite possible : profils dans les exports et la complétude, valeurs les plus fréquentes (à décider : ce sont des données), profilage planifié.

---

## Dette connue (issue des jalons 1 et 2)

_La dette soldée (index, position des colonnes, schémas SQL Server, pagination, tests du client…) est dans l'historique git._

| Sujet | Jalon |
|---|---|
| Analyse d'impact limitée à la base de l'élément (une relation manuelle entre deux bases n'est pas suivie) ; le diagramme place les tables par couches sans minimiser les croisements de relations | — |
| Index : seuls les noms de colonnes sont stockés (pas l'ordre ASC/DESC, ni les prédicats d'index partiels, ni les colonnes `INCLUDE`) | 8 |

---

## Ordre recommandé

```
J1, J2, J3, J4, J5, J6, J7, J9 terminés → J12 Rôles et droits → J11 Validation de l'UI/UX
                                                          └→ J8 Autres moteurs → J10 Avancé
```

GranitDb sait documenter (descriptions, relations, étiquettes, domaines, colonnes sensibles) et peut exiger une connexion avec des rôles (jalon 6, désactivée par défaut). L'étape suivante la plus utile est **J12** : gérer ces rôles et ces droits depuis l'application plutôt que par la configuration. Vient ensuite **J11 (validation de l'interface avec de vraies personnes)**.
