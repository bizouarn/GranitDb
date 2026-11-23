# Roadmap fonctionnelle — GranitDb

_Ne contient que ce qui reste à faire ; l'historique de ce qui est fait est dans git. Les jalons sont ordonnés par dépendance, sans estimation de durée._

Priorités : **P0** bloquant · **P1** cœur de valeur · **P2** confort · **P3** exploratoire. Effort : **S** (≤ 1 jour) · **M** (quelques jours) · **L** (une à deux semaines).

## Où on en est

Les jalons **1** à **9** sont terminés et retirés de ce document : SQLite sans serveur, fondations (migrations, Docker, CI), interface de consultation, API catalogue complète, édition et gouvernance, sécurité (GitHub et OpenID Connect, rôles, audit, bases publiques), visualisation, autres moteurs (PostgreSQL, MySQL / MariaDB, synchronisation planifiée), exports et client généré depuis OpenAPI. Le jalon **13** (détection des relations, socle IA, assistant de documentation) et le jalon **11** (refonte de l'interface) sont en grande partie faits ; leurs restes sont ci-dessous.

| Ordre | Jalon | État |
|---|---|---|
| 1 | 12 — Gestion des rôles et des droits | ⬜ Non commencé : les rôles sont encore déclarés en configuration et figés à la connexion |
| 2 | 13 — Assistant IA et détection des relations : restes | 🟡 En cours |
| 3 | 11 — Refonte UI/UX : validation | 🟢 Presque terminé : reste ce qui demande de vraies personnes et de vrais appareils |
| 4 | 10 — Fonctions avancées | ⬜ Non commencé, exploratoire |

L'API peut exiger une connexion (`Auth:Enabled`, voir [docs/authentification.md](../docs/authentification.md)) mais **reste ouverte tant que cette option est fausse** (valeur par défaut, pour le poste de développement) : ne l'exposer hors poste de dev ou réseau de confiance qu'avec l'authentification activée.

---

## Jalon 12 — Gestion des rôles et des droits (P1)

Les trois rôles globaux (lecteur, éditeur, administrateur) sont **déclarés en configuration** et calculés à la connexion. Ce jalon les rend **gérables depuis l'application** et plus fins.

- [ ] **Écran d'administration des accès** (M) : la liste des comptes qui se sont connectés (login, fournisseur, rôle, dernière connexion, rôle obtenu par liste ou par groupe), réservée aux administrateurs. Les comptes sont enregistrés à la première connexion (table `Users`, migration à ajouter).
- [ ] **Attribuer un rôle dans l'interface** (M) : un administrateur change le rôle d'un compte sans toucher à `appsettings` ni redémarrer ; ces attributions s'ajoutent aux listes et groupes de la configuration (la configuration garde le premier administrateur, jamais retirable depuis l'interface). Même principe pour **bloquer / débloquer** un compte.
- [ ] **Correspondance groupes → rôles éditable** (M) : gérer dans l'interface les groupes GitHub et les groupes du fournisseur OpenID Connect qui donnent chaque rôle, avec un aperçu « qui obtiendrait quoi » avant d'enregistrer.
- [ ] **Prise en compte immédiate d'un changement de rôle** (M) : le rôle vit dans le cookie jusqu'à la prochaine connexion. Relire le rôle (et le blocage) à chaque requête ou toutes les quelques minutes, pour qu'une rétrogradation ou un blocage agisse tout de suite.
- [ ] **Droits par base** (L) : un rôle peut être limité à certaines bases, avec des **groupes de bases** ; l'explorateur et la recherche ne montrent que les bases accessibles. Les bases publiques deviennent un cas particulier (« tout le monde en lecture »).
- [ ] **Droits par domaine** (M) : éditeur d'un domaine seulement, pour déléguer la documentation à ses responsables métier.
- [ ] **Colonnes sensibles selon le rôle** (M) : masquer les descriptions ou valeurs d'exemple des colonnes sensibles aux lecteurs.
- [ ] **Demande d'accès** (M) : un compte sans rôle peut demander l'accès ; les administrateurs acceptent ou refusent depuis l'écran d'administration.
- [ ] **Jetons d'API / comptes de service** (M) : un jeton à portée et rôle limités, révocable, pour la CI ou un script, sans session de navigateur ; montré une seule fois, stocké haché.
- [ ] **Matrice des droits visible** (S) : une page « Qui peut faire quoi » (rôles × actions), générée depuis les attributs de l'API.
- [ ] **Audit des droits** (S) : le journal d'audit inscrit chaque changement de rôle, de blocage et de groupe (qui, pour qui, avant/après), et chaque refus 403.
- [ ] Tests : changement de rôle sans reconnexion, blocage, droits par base et par domaine, jeton révoqué refusé, dernier administrateur non retirable.

## Jalon 13 — Assistant IA et détection des relations : restes (P2)

Principes (en place) : rien n'est écrit sans validation humaine, modèle local et léger par défaut, métadonnées seulement (jamais de valeur de ligne ni de colonne sensible), désactivé par défaut. Voir [docs/aides-documentation.md](../docs/aides-documentation.md).

### Détection des relations

- [ ] **File de relecture** (M) : aperçu en mini-diagramme des deux tables, « ignorer une table », affichage « détectée » dans le diagramme et les exports.
- [ ] **Clés composites** (M) : proposer des paires de colonnes `(a, b) → (a, b)` quand l'inclusion est vraie pour le couple et que la cible est une clé primaire ou un index unique composite.
- [ ] **Relances** (S) : relancer la détection après une resynchronisation ou un profilage et signaler les nouveaux candidats (« n relations suggérées » dans la synchronisation planifiée).
- [ ] Tests : vérification sur PostgreSQL, MySQL et SQL Server (requête écrite pour les quatre moteurs, testée seulement sur SQLite) et clés composites.

### Socle IA

- [ ] **Garde-fous** (M) : limite de cadence et entrée du journal d'audit par appel IA (qui, base, modèle, nombre de jetons, sans le contenu).

### Assistant de documentation

- [ ] **Glossaire et abréviations** (M) : vocabulaire de l'entreprise (`cli` = client…) injecté dans le prompt ; abréviations détectées proposées puis validées, qui alimentent le glossaire métier du jalon 10.
- [ ] **Domaines suggérés** (M) : regrouper les tables par préfixe de nom et par relations (y compris détectées), proposer un nom de domaine et ses tables, créer après validation.
- [ ] **Colonnes sensibles par le modèle** (S) : aujourd'hui par règles de nommage seulement.
- [ ] **Décrire les relations** (S) : injecter la phrase d'une relation acceptée dans le contexte des descriptions de tables.
- [ ] **Dans l'éditeur** (M) : bouton « Suggérer » pour la base et la relation (déjà fait pour tables et colonnes), entrée de la palette Ctrl+K.
- [ ] **Mesurer avant de figer le modèle par défaut** (M) : jeu d'évaluation (`shop.sqlite` et une base annotée) pour comparer Qwen3 1.7B / 4B et Gemma 3 1B / 4B : validité du JSON, langue, longueur, inventions et, en usage réel, taux acceptées / modifiées / refusées.
- [ ] Tests : test d'intégration facultatif avec un conteneur Ollama (ignoré sans Docker).

### Risques et limites assumées

- **Qualité** : un modèle de 1 à 4 milliards de paramètres reformule bien des noms explicites et se trompe sur les noms cryptiques. La relecture humaine n'est pas une option, et l'interface ne présente jamais une proposition comme fiable.
- **Temps de calcul** : de quelques secondes à quelques dizaines de secondes par table sur CPU → traitement en arrière-plan, une requête à la fois.
- **Hors périmètre pour l'instant** : questions en langage naturel sur le catalogue, génération de requêtes SQL, ajustement fin.

## Jalon 11 — Refonte de l'UI/UX : validation (P1)

_Suite détaillée : [roadmap pour rendre les interfaces plus intuitives](04-roadmap-interfaces-intuitives.md). Protocole : [parcours utilisateurs](02-parcours-utilisateurs.md)._

Ce qui reste ne peut pas se faire sans utilisateurs ni appareils :

- [ ] **Tests utilisateurs** : dérouler les quatre parcours du protocole (importer une base, retrouver une colonne, documenter une table, exporter un diagramme) avec de vraies personnes, puis corriger ce qu'elles n'ont pas trouvé seules.
- [ ] **Accessibilité vérifiée** : structure en place et testée par composants, mais pas auditée avec un lecteur d'écran réel ni avec un outil de mesure de contraste.
- [ ] **Petits écrans** : les tiroirs deviennent des panneaux superposés sous 960 px (explorateur) et 1280 px (détails), mais la disposition n'a pas été essayée sur de vrais téléphones et tablettes.
- [ ] **Fenêtres de saisie** : restent le fichier SQLite (la zone de dépôt reste une page), la page `ConnectSqlServer` pour la palette, et la migration de la suppression d'une base, du remplacement d'un fichier et de la resynchronisation vers la confirmation commune (déjà protégées en place).

## Jalon 10 — Fonctions avancées (P3, exploratoire)

- [ ] Suggestions de descriptions par IA : voir le jalon 13.
- [ ] Glossaire métier relié aux colonnes.
- [ ] Lignage de données entre bases.
- [ ] Profilage : profils dans les exports et la complétude, valeurs les plus fréquentes (à décider : ce sont des données), profilage planifié.

---

## Dette connue

| Sujet |
|---|
| Analyse d'impact limitée à la base de l'élément (une relation manuelle entre deux bases n'est pas suivie) ; le diagramme place les tables par couches sans minimiser les croisements de relations |
| Index : seuls les noms de colonnes sont stockés (pas l'ordre ASC/DESC, ni les prédicats d'index partiels, ni les colonnes `INCLUDE`) |

Les constats de sécurité et de performance ouverts sont dans l'[audit du code](03-audit-code.md).

## Ordre recommandé

```
J12 Rôles et droits → J13 restes de l'assistant IA → J11 Validation de l'UI/UX
                                                                └→ J10 Avancé
```
