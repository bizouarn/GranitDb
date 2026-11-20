# Roadmap fonctionnelle — GranitDb

_Basée sur l'[état des lieux](00-etat-des-lieux.md). Les jalons sont ordonnés par dépendance, sans estimation de durée : à chiffrer avec l'équipe._

Priorités : **P0** bloquant · **P1** cœur de valeur · **P2** confort · **P3** exploratoire.

## Avancement (mis à jour le 2026-11-18)

Jalons **1** (SQLite sans serveur), **2** (fondations : migrations, Docker, CI), **4** (interface de consultation), **3** (API catalogue complète : routes `/api/v1`, entrées validées, vues de sortie, filtres, historique, impact, import et resynchronisation d'une base SQL Server depuis l'interface avec connexion chiffrée), **5** (édition et gouvernance : descriptions et notes modifiables, relations manuelles, étiquettes, domaines et diagrammes par domaine, colonnes sensibles, complétude, aperçu de la synchronisation), **7** (visualisation : diagramme entité-relation interactif, analyse d'impact, exports SVG/PNG/Mermaid) **8** (autres moteurs : connecteurs PostgreSQL et MySQL/MariaDB, import par `import/server`, synchronisation planifiée avec notification `sync.failed`), **9** (exports HTML, Markdown, CSV, Excel natif et PDF imprimable ; client typé généré depuis OpenAPI, adopté par l'interface, projet `interfaces` supprimé) et **6** (sécurité : connexion GitHub et OpenID Connect générique configurable, session par cookie, rôles lecteur/éditeur/administrateur, CORS restreint, journal d'audit réservé aux administrateurs, bases publiques lisibles sans compte, secrets de sources hors fichier, clés chiffrées par DPAPI ou certificat) : terminés à 100 %, retirés de ce document (leur contenu est dans l'historique git).

| Ordre | Jalon | État | Pourquoi à cette place |
|---|---|---|---|
| 1 | 12 — Gestion des rôles et des droits (écran d'administration, droits par base et par domaine, jetons) | ⬜ Non commencé | prolonge le jalon 6 : les rôles sont encore déclarés en configuration et figés à la connexion |
| 2 | 13 — Assistant de documentation par IA et détection assistée des relations (modèle léger local de type Qwen ou Gemma) | 🟡 En cours (lot A : détection des relations, API et écran faits) | s'appuie sur le profilage (jalon 10) et sur les relations ; le lot A (relations) fonctionne sans modèle |
| 3 | 11 — Refonte UI/UX du client | 🟢 Presque terminé | l'espace de travail est en place ; reste la validation avec de vrais utilisateurs et appareils (tests, lecteur d'écran, petits écrans) |
| 4 | 10 — Fonctions avancées | ⬜ Non commencé | exploratoire |

**Prochain jalon : 12 (gestion des rôles et des droits), puis 13 (assistant de documentation par IA), puis 11 (validation de l'interface).** L'API peut exiger une connexion (`Auth:Enabled`, voir le README) mais **reste ouverte tant que cette option est fausse** (valeur par défaut, pour le poste de développement) : ne l'exposer hors poste de dev ou réseau de confiance qu'avec l'authentification activée. Les jalons ci-dessous sont classés dans l'ordre où les traiter ; leurs numéros sont conservés pour ne pas être confondus avec les jalons déjà terminés.

Chiffres : 829 tests sous .NET 10 (les tests SQL Server, PostgreSQL et MySQL nécessitent Docker), CI GitHub Actions, pile `docker compose` vérifiée en SQLite et en SQL Server. Catalogue en version 16. L'interface (espace de travail, diagramme, glisser-déposer, édition en place) est couverte par des tests bUnit et de modèle ; sa vérification dans un vrai navigateur reste manuelle.

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

## Jalon 13 — Assistant de documentation par IA et détection assistée des relations (P2)

Documenter des centaines de tables à la main est le vrai frein à l'adoption. Ce jalon ajoute deux aides qui **proposent** et ne décident jamais : une **détection des relations** que les clés étrangères ne déclarent pas (appuyée sur le profilage du jalon 10) et un **assistant qui rédige les descriptions**. Principes communs :

- **Rien n'est écrit sans validation humaine.** Les propositions arrivent dans une file de relecture (accepter, modifier puis accepter, refuser). Une description déjà saisie n'est jamais remplacée. L'origine est tracée dans l'historique (« proposé par le modèle X, validé par Y »).
- **Local d'abord.** Le modèle par défaut est **léger** (famille Qwen ou Gemma, 1 à 4 milliards de paramètres) et tourne sur la machine ou le serveur de l'application, sans clé ni compte : aucune donnée ne sort. Un service distant reste possible, mais il faut le déclarer explicitement.
- **Métadonnées seulement.** Le modèle ne reçoit que des noms, types, clés, descriptions existantes et les comptes agrégés du profilage ; **jamais de valeurs de lignes**, et jamais les colonnes marquées sensibles.
- **Désactivé par défaut** (`Catalog:AllowAi`, comme `AllowProfiling`), configuré par les administrateurs, utilisable par les éditeurs.
- **Le déterministe d'abord.** La détection de relations fonctionne sans modèle ; l'IA ne sert qu'à départager les cas ambigus et à rédiger.

Ordre interne : **lot A** (relations, sans modèle) → **lot B** (socle IA locale) → **lot C** (assistant de documentation).

### Lot A — Détection assistée des relations (sans modèle)

- [x] **Candidats par nom et par type** (M) : une colonne `client_id`, `id_client`, `clientId`, `fk_client`, `client_code`… est rapprochée de la table `client` / `clients` / `tbl_client` (singulier/pluriel, préfixes de table, français et anglais) quand les types sont compatibles et que la cible est une clé primaire ou une colonne unique. Les paires déjà reliées (clé étrangère ou relation manuelle, dans un sens ou dans l'autre) sont écartées. Utile pour les bases sans clés déclarées (historiques, vues, moteurs qui ne les imposent pas).
- [x] **Vérification par le profilage** (L) : la source calcule elle-même, comme pour le profilage, des **comptes seulement** : combien de valeurs non nulles de la colonne n'existent pas dans la colonne cible (`NOT EXISTS`, taux d'inclusion), la cible est-elle unique (distincts = lignes non nulles), le taux de nuls. Mêmes garde-fous que le profilage (`Catalog:AllowProfiling`, administrateur, lecture seule, lots, délai) ; un échantillon (`TOP n`) est possible pour les très grosses tables. Réutilise `IColumnProfiler`.
- [x] **Score et explication** (M) : un score 0–100 composé (nom, type, unicité de la cible, inclusion, nuls) classé « probable / possible / faible », avec ses raisons lisibles (« 100 % des 12 408 valeurs existent dans `clients.id` »). Écarter les faux amis : colonnes `id`, `status`, `type` génériques, cibles de moins de ~20 valeurs.
- [ ] **File de relecture des relations** (M) — fait : écran `/databases/{id}/relations` (score, raisons, accepter, refuser mémorisé, accepter les « probables » en lot) ; reste : aperçu en mini-diagramme, « ignorer une table », affichage « détectée » dans le diagramme et les exports : un écran « Relations suggérées » par base, triées par score, avec aperçu (mini-diagramme des deux tables) ; **accepter** crée une relation manuelle marquée `detected` (affichée comme « détectée », retirable), **refuser** est mémorisé (table `RelationSuggestions`, statut) pour ne plus la proposer tant que les colonnes ne changent pas, **accepter par lot** au-dessus d'un seuil, **ignorer** une table.
- [ ] **Clés composites** (M) : proposer des paires de colonnes `(a, b) → (a, b)` quand l'inclusion est vraie pour le couple et que la cible est une clé primaire ou un index unique composite.
- [ ] **Relances** (S) : relancer la détection après une resynchronisation ou un profilage, signaler les nouveaux candidats ; la synchronisation planifiée du jalon 8 peut afficher « n relations suggérées ».
- [x] **API et contrat** (S) : `GET DatabaseInfo/{id}/relation-suggestions`, `POST DatabaseInfo/{id}/relation-suggestions/detect` (administrateur), `POST …/{suggestionId}/accept|reject` ; contrat OpenAPI et client généré à jour.
- [x] Tests : base SQLite sans clés déclarées (nommage conforme, faux amis, orphelines), refus mémorisé, aucune valeur de ligne dans les réponses, profilage désactivé → refus clair. Reste : la vérification sur PostgreSQL, MySQL et SQL Server (requête écrite pour les quatre moteurs, testée seulement sur SQLite) et les clés composites.

### Lot B — Socle IA locale

- [x] **Fournisseur abstrait** (M) : `IAiProvider` (génération de texte structurée) sur **un seul protocole**, l'API compatible OpenAI `/v1/chat/completions`, que parlent Ollama, `llama-server` (llama.cpp), LM Studio, vLLM et la plupart des services hébergés. Réglages `Ai:Endpoint`, `Ai:Model`, `Ai:ApiKey` (via `SecretReference`, `env:` / `file:`), `Ai:TimeoutSeconds`, `Ai:MaxConcurrent` (1 par défaut : un petit serveur ne tient pas la charge).
- [x] **Modèle léger par défaut** (réglé sur `qwen3:4b-instruct`, mesuré sur GTX 1650 : 7 s par description contre 3 min 35 pour la variante « thinking » `qwen3:4b` ; les noms exacts et les licences restent à vérifier avant de figer) (S) : un modèle « instruct » de la famille **Qwen3 4B** quantifié (≈ 2,5 Go, tourne sur CPU avec 8 Go de mémoire) ; alternative **Gemma 3 4B** ; pour les machines modestes **Qwen3 1.7B** ou **Gemma 3 1B** ; pour une meilleure qualité avec GPU un modèle de 12 à 14 milliards. Les noms exacts, les tailles et les **conditions de licence** (Gemma a ses propres conditions d'utilisation) sont à vérifier au moment de l'implémentation : les catalogues évoluent vite.
- [x] **Pile `docker compose`** (S) : un profil `ai` avec un service Ollama (volume des modèles, GPU optionnel), les variables `ALLOW_AI` et `AI_MODEL` dans `.env.example` et le README (équivalent PowerShell compris : `$env:ALLOW_AI = "true"; docker compose --profile ai up --build -d`), et le téléchargement du modèle au premier démarrage. Le modèle n'est **pas** dans l'image de l'API (plusieurs Go) ; tant qu'il est absent, l'API répond « modèle en cours de téléchargement » plutôt que d'échouer.
- [x] **Capacités et état** (S) : `GET Capabilities` annonce `ai` (activé, modèle, joignable) ; le client masque les boutons sinon ; un bouton « Tester le modèle » pour l'administrateur.
- [ ] **Garde-fous** (M) — fait : désactivé par défaut, https exigé hors machine locale (`Ai:AllowRemote`), taille de requête et appels simultanés bornés, délai, annulation, journal des jetons sans contenu ; reste, avec le lot C : liste blanche des champs envoyés et exclusion des colonnes sensibles (elles se construisent avec les prompts), limite de cadence, entrée du journal d'audit par appel IA : liste blanche des champs envoyés (noms, types, clé, nullabilité, description existante, comptes agrégés), colonnes sensibles exclues, point d'accès distant refusé s'il n'est pas en https sauf `Ai:AllowRemote`, journal d'audit des appels (qui, base, modèle, nombre de jetons, **sans le contenu**), limites de taille de requête et de cadence, annulation.
- [x] **Sortie structurée** (M) : demander du JSON contraint par un schéma (`response_format` ou grammaire, pris en charge par Ollama et llama.cpp), validation stricte, un réessai, puis un échec propre ; température basse ; langue de sortie réglable (français par défaut). Indispensable avec un petit modèle, qui dérive vite hors format.

### Lot C — Assistant de documentation

- [x] **Proposer une description** (M) de base, de table ou de colonne : contexte = nom, colonnes (nom, type, clé, nullable), relations (tables voisines et leurs descriptions), quelques descriptions déjà saisies de la base pour imiter le style, profil si disponible. Un prompt court par table (≈ 2 000 jetons, colonnes par lots d'une quinzaine), adapté à un petit modèle.
- [x] **Documentation en lot avec file de relecture** (L) : « Documenter les éléments sans description » pour une base ou un domaine, en tâche d'arrière-plan avec progression, annulable et reprenable. Les propositions sont stockées (`DescriptionSuggestions` : cible, texte, modèle, statut, date) et relues dans un écran dédié : accepter, modifier puis accepter, refuser, régénérer, accepter par lot. Une proposition peu sûre (noms cryptiques du type `ZX_T01`) est marquée « incertaine ».
- [ ] **Glossaire et abréviations** (M) : vocabulaire de l'entreprise (`cli` = client, `cde` = commande…) injecté dans le prompt ; les abréviations détectées sont proposées puis validées, et alimentent le glossaire métier du jalon 10.
- [ ] **Domaines suggérés** (M) : regrouper les tables par préfixe de nom et par relations (y compris les relations détectées du lot A), proposer un nom de domaine et ses tables, créer après validation.
- [x] **Colonnes sensibles suggérées** (S) : proposer `IsSensitive` (e-mail, IBAN, nom, date de naissance…) par règles d'abord, par le modèle ensuite, toujours à valider. Fait : règles de nommage seulement (la proposition par le modèle reste à faire).
- [x] **Décrire les relations** (S) : une relation acceptée reçoit une phrase (« une commande appartient à un client ») et enrichit le contexte des descriptions de tables. Fait : une phrase par relation sans description, proposée par lots de dix, dans la même file ; reste : l'injecter dans le contexte des descriptions de tables.
- [ ] **Dans l'éditeur** (M) : un bouton « Suggérer » à côté d'un champ de description (`InlineText`) affiche la proposition à accepter ou modifier ; une entrée dans la palette Ctrl+K. Fait : bouton « Suggérer » sous les descriptions vides de table et de colonne (proposition modifiable, enregistrée seulement à « Accepter ») ; reste : base et relation, entrée de la palette Ctrl+K.
- [ ] **Mesurer avant de figer le modèle par défaut** (M) : un jeu d'évaluation (la base de démo `shop.sqlite` et une base de test annotée avec des descriptions de référence) pour comparer Qwen3 1.7B / 4B et Gemma 3 1B / 4B sur la validité du JSON, la langue, la longueur, les inventions (éléments cités qui n'existent pas) et, en usage réel, le taux acceptées / modifiées / refusées. Le résultat décide du modèle recommandé.
- [ ] Tests : faux serveur compatible OpenAI (JSON invalide → réessai → échec propre), aucune valeur de ligne dans le corps des requêtes, colonnes sensibles exclues, description existante jamais écrasée, 403 hors rôle, désactivé par défaut ; test d'intégration avec un conteneur Ollama facultatif (ignoré sans Docker, comme SQL Server). Fait : faux modèle scripté (JSON invalide → réessai → échec propre, aucune valeur ni colonne sensible dans les prompts, jamais d'écrasement, désactivé par défaut) ; reste : le test d'intégration facultatif avec un conteneur Ollama.
- [x] Documentation : section « Assistant IA » du README (installation du modèle, confidentialité, réglages), paragraphe dans `CLAUDE.md`.

### Risques et limites assumées

- **Qualité** : un modèle de 1 à 4 milliards de paramètres reformule bien des noms explicites (`customer_email`) et se trompe sur les noms cryptiques. La relecture humaine n'est pas une option, et l'interface ne présente jamais une proposition comme fiable.
- **Temps de calcul** : de quelques secondes à quelques dizaines de secondes par table sur CPU → traitement en arrière-plan, une requête à la fois par défaut.
- **Poids** : le modèle se télécharge à part (plusieurs Go) ; l'image de l'API reste légère.
- **Hors périmètre pour l'instant** : questions en langage naturel sur le catalogue, génération de requêtes SQL, ajustement fin (fine-tuning). À envisager ensuite, sur le même socle.

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

## Jalon 10 — Fonctions avancées (P3, exploratoire)

- [ ] Suggestions de descriptions par IA, avec relecture humaine : voir le [jalon 13](#jalon-13--assistant-de-documentation-par-ia-et-détection-assistée-des-relations-p2).
- [ ] Glossaire métier relié aux colonnes.
- [ ] Lignage de données entre bases.
- [x] Profilage léger des colonnes (cardinalité, taux de nuls), sans exposer de données brutes : fait (`Catalog:AllowProfiling`, comptes calculés par la source, page d'une table, API réservée aux administrateurs). Suite possible : profils dans les exports et la complétude, valeurs les plus fréquentes (à décider : ce sont des données), profilage planifié.

---

## Dette connue (issue des jalons 1 et 2)

_La dette soldée (index, position des colonnes, schémas SQL Server, pagination, tests du client…) est dans l'historique git._

| Sujet | Jalon |
|---|---|
| Analyse d'impact limitée à la base de l'élément (une relation manuelle entre deux bases n'est pas suivie) ; le diagramme place les tables par couches sans minimiser les croisements de relations | — |
| Index : seuls les noms de colonnes sont stockés (pas l'ordre ASC/DESC, ni les prédicats d'index partiels, ni les colonnes `INCLUDE`) | — |

---

## Ordre recommandé

```
J1 à J9 terminés → J12 Rôles et droits → J13 Assistant IA → J11 Validation de l'UI/UX
                                                                             └→ J10 Avancé
```

GranitDb sait documenter (descriptions, relations, étiquettes, domaines, colonnes sensibles) et peut exiger une connexion avec des rôles (jalon 6, désactivée par défaut). L'étape suivante la plus utile est **J12** : gérer ces rôles et ces droits depuis l'application plutôt que par la configuration. Vient ensuite **J13** (un assistant IA local et léger qui propose des descriptions, et une détection des relations non déclarées vérifiée par le profilage, le tout relu par un humain), puis **J11 (validation de l'interface avec de vraies personnes)**.
