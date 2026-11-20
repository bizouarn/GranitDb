# Roadmap — rendre les interfaces plus intuitives

_Écrite le 2026-11-18 à partir de l'interface telle qu'elle est livrée (explorateur, onglets, page d'une base, diagramme, écran de gestion, audit, webhooks) et de ce que l'usage a déjà montré : le surlignage du diagramme n'était pas compris, les sauts de vocabulaire (« référencée », « qui référence », FK, impact) demandent un effort, et plusieurs actions n'ont qu'une icône. Elle complète le jalon 11 de la [roadmap](01-roadmap.md) et le protocole de [validation](02-parcours-utilisateurs.md), sans le remplacer._

## Principes

1. **Dire ce qui se passe, pas seulement le montrer.** Tout état visuel (sélection, surlignage, filtre actif, élément masqué) s'accompagne d'une phrase ou d'une légende, et d'un moyen évident de le retirer.
2. **Un geste, un résultat.** Cliquer édite en place, quitter enregistre, Échap annule, « Annuler » rétablit : la même règle partout (déjà vraie pour les textes et les relations, à étendre au reste).
3. **Le vocabulaire de l'utilisateur, pas celui du modèle.** « Cette colonne pointe vers… » plutôt que « SecondId / FirstId » ; les termes techniques (clé étrangère, cardinalité) restent mais s'expliquent au survol.
4. **Rien d'important caché derrière une icône seule.** Chaque icône a un libellé visible ou au moins une infobulle claire ; chaque écran vide dit quoi faire ensuite.
5. **Mesurer avant de croire.** Chaque lot se valide avec les parcours du protocole (importer, retrouver, documenter, exporter) : un lot n'est « fini » que si des personnes réussissent sans aide.

Priorités : **P1** gêne constatée ou probable pour tous · **P2** confort · **P3** exploratoire. Effort : **S** (≤ 1 jour) · **M** (quelques jours) · **L** (une à deux semaines).

---

## Lot 1 — Comprendre ce que l'on voit (P1)

Le diagramme et la page d'une base affichent beaucoup d'information ; la première raison d'abandon est de ne pas savoir ce qu'on regarde.

- [x] **Légende permanente du diagramme** (S) : fait — bouton « Légende » dans la barre du diagramme (`1` / `N`, `PK` / `FK`, tables en pointillés, trait multi-colonnes, gestes), en plus de la légende de sélection.
- [ ] **Échap et clic dans le vide partout** (S) : _fait dans le diagramme (clic dans le vide et Échap)._ Fait aussi pour le panneau de détails (Échap désélectionne, sauf pendant une saisie, où Échap l'annule d'abord). Reste le panneau d'impact.
- [ ] **Indicateurs d'état explicites** (S) : _fait pour les relations masquées : bandeau « N relations masquées dans ce domaine [Tout afficher] » sur le diagramme._ Fait aussi : bandeau « Filtre actif : … [Effacer le filtre] » (`ActiveFilterBar`) sur l'explorateur, les tables d'une base (texte + domaine) et le journal d'audit (filtre appliqué, pas la saisie en cours). Reste les listes de l'écran de gestion.
- [x] **Phrases à la place des champs techniques** (M) : _fait : l'analyse d'impact (« Si vous modifiez cette table, 5 éléments peuvent être touchés »), l'aperçu de resynchronisation (« 2 colonnes ajoutées, 1 table supprimée » et l'avertissement de documentation perdue), via `Phrases` ; le formulaire de relation disait déjà « pointe vers »._ Besoin d'origine : formulaire d'ajout de relation (« Cette colonne **pointe vers** cette colonne d'une autre table » avec un aperçu en clair : « customers.id est référencée par orders.customer_id »), analyse d'impact (« Si vous modifiez cette colonne, ces 3 éléments peuvent être touchés »), aperçu de resynchronisation (« 2 colonnes ajoutées, 1 supprimée avec sa description : à vérifier »).
- [ ] **Glossaire au survol** (S) : _commencé : infobulles sur « manuelle » et « signé »._ Reste clé étrangère, cardinalité, domaine, étiquette, colonne sensible, relation masquée, et un lien « En savoir plus » vers une page d'aide unique.
- [ ] **Aperçu avant, résultat après** (M) : pour les actions qui touchent beaucoup d'éléments (retrait en lot d'un domaine, masquer toutes les relations, resynchronisation, import de documentation), annoncer le nombre d'éléments touchés avant, et afficher un récapitulatif après avec « Annuler » quand c'est possible.

## Lot 2 — Se repérer et trouver (P1)

- [x] **Libeller les icônes de navigation** (S) : fait — un menu « Gérer » (étiquettes et domaines, webhooks, journal d'audit) remplace les icônes seules de l'en-tête de l'explorateur ; seul le rechargement reste une icône (avec infobulle).
- [x] **Navigation uniforme** (M) : fait — `PageCrumbs` (Bases › base › table › colonne › page, dernier maillon sans lien) sur toutes les pages ; une relation ouvre la table de chacun de ses côtés (colonne mise en évidence) ; l'analyse d'impact du diagramme lie chaque résultat et la table analysée à sa fiche. _Reste : le retour à l'endroit précédent dans l'explorateur, et le clic sur une table du dessin qui ouvre sa fiche (aujourd'hui il sélectionne)._
- [x] **Recherche mieux guidée** (M) : fait — suggestions sous la barre pendant la frappe (↑ ↓ Entrée Échap, base et raison du résultat, « Voir tous les résultats »), exemples dans le champ et sur la page vide, limitation à une base en un clic (pastilles avec décompte), raison du résultat (nom, description, colonnes de l'index). _Reste : domaines et étiquettes dans la recherche (l'API ne les cherche pas encore) et la limitation à la base courante d'office._
- [x] **Raccourcis découvrables** (S) : fait — feuille « Raccourcis clavier » (touche `?`, palette, page d'aide), rappel sur l'accueil. _Reste les raccourcis dans les infobulles._
- [ ] **Diagramme dans la barre latérale** (M) : reprendre l'item du jalon 11 — la liste des tables à afficher devient l'arbre de l'explorateur, pour ne plus avoir deux endroits où choisir.

## Lot 3 — Bien démarrer (P1)

- [ ] **Accueil guidé selon l'état du catalogue** (M) : aucun contenu → « Importez votre première base » en grand, avec les trois voies (fichier SQLite, SQL Server, démonstration) ; une base sans description → « Documentez vos tables les plus utilisées » avec la complétude et un lien vers les tables à décrire en premier ; une base documentée → derniers changements et tables à revoir.
- [x] **Base de démonstration** (S) : un bouton « Essayer avec un exemple » qui importe la base d'exemple, pour découvrir sans rien préparer.
- [ ] **Visite en quatre étapes** (M) : explorateur, onglets, palette, diagramme ; non bloquante, relançable depuis l'aide, mémorisée.
- [ ] **États vides utiles** (S) : _fait pour les webhooks, le journal d'audit, les relations d'une table et la recherche ; les domaines, étiquettes et bases le disaient déjà._ Reste à repasser sur les autres listes (explorateur filtré, tables, envois d'un webhook).
- [ ] **Messages d'erreur actionnables** (M) : chaque erreur dit quoi faire (« Le serveur SQL n'a pas répondu : vérifiez le nom du serveur et le port ») ; harmoniser les erreurs du serveur (ProblemDetails ou texte, aujourd'hui mélangés) pour que le client les affiche toutes de la même façon.

## Lot 4 — Documenter plus vite (P2)

- [ ] **Saisie au clavier de bout en bout** (M) : Tab / Maj+Tab passent à la colonne suivante en enregistrant, comme dans un tableur ; Entrée valide et descend d'une ligne.
- [ ] **Édition en lot** (M) : sélectionner plusieurs colonnes (même nom dans plusieurs tables, par exemple `id_client`) et leur appliquer une description, une étiquette ou un marquage « sensible » en une fois.
- [ ] **Propagation proposée** (M) : quand une colonne décrite a le même nom et le même type ailleurs, proposer « Appliquer cette description aux 7 colonnes identiques ? ». _Prépare les suggestions du jalon 10._
- [ ] **File « à documenter »** (M) : une vue des éléments sans description classés par utilité (tables les plus reliées d'abord), pour savoir par où commencer et voir la complétude monter.
- [ ] **Modèles de description** (S) : quelques tournures proposées (« Identifiant de… », « Date de… », « Code de… ») insérables d'un clic.

## Lot 5 — Le diagramme sans effort (P2)

- [ ] **Éditeur de domaine dans le diagramme** (M) : item du jalon 11 — ajouter ou retirer des tables sans quitter le dessin, ajout en lot (par préfixe, par table liée) avec aperçu du nombre de relations qui apparaîtront.
- [ ] **Clic sur un lien** (S) : cliquer une relation la sélectionne, surligne ses colonnes des deux côtés et ouvre ses détails (type, description modifiables en place).
- [ ] **Minicarte, recentrage et « tout voir »** (M) : un bouton « Ajuster à l'écran », une minicarte pour les grands plans, le centrage automatique sur la sélection.
- [ ] **Dessin qui s'adapte** (M) : mise en page qui réduit les croisements, masquage automatique des colonnes sans relation au-delà d'un seuil, repli d'une table d'un clic.
- [ ] **Annuler / rétablir** (M) : pour les déplacements de tables et les changements de domaine (Ctrl+Z).
- [ ] **Changer la couleur d'un domaine existant** (S) : la couleur se choisit aujourd'hui à la création (`DomainDialog`) ; ajouter le choix de la couleur à la modification du domaine, depuis le panneau des domaines du diagramme (pastille cliquable avec sélecteur et palette de teintes lisibles sur les deux thèmes, contraste vérifié) et depuis « Étiquettes et domaines ». Le changement se voit tout de suite dans le diagramme, l'explorateur et l'export HTML. Prévoir l'annulation (toast « Annuler ») et une valeur vide pour revenir à la couleur par défaut.
- [ ] **Une couleur par table dans un domaine** (M) : dans la vue d'un domaine, donner à une table sa propre couleur d'en-tête (sinon celle du domaine), par un menu sur la table dessinée ou dans la liste des tables du domaine ; la couleur est mémorisée **par domaine** (une table peut avoir une couleur dans « Ventes » et une autre dans « Clients »), à côté de sa place (`DomainTables`, nouvelle colonne de migration, `PUT DomainInfo/{id}/tables/{tableId}` accepte la couleur). Utile pour distinguer d'un coup d'œil les tables de référence, les tables de faits ou celles à revoir. Reprise dans l'export HTML (en-tête de la table et diagramme), les exports SVG/PNG et la légende, avec « Rétablir la couleur du domaine » et la possibilité de colorer plusieurs tables sélectionnées d'un coup. Tests : couleur par domaine indépendante, retour à la couleur du domaine, export fidèle.
- [x] **Réglages de domaine compris** (S) : fait pour le texte (phrase d'aide sous « Relations du domaine » et sous « Tables voisines ») et pour « Réinitialiser comme dans Dataedo » ; _reste le petit schéma._

## Lot 6 — Confiance et sécurité d'usage (P2)

- [ ] **Confirmations proportionnées** (S) : rien pour ce qui s'annule, une confirmation claire (avec le nombre d'éléments perdus) pour ce qui ne s'annule pas ; harmoniser sur le modèle de la suppression d'une base (retaper le nom).
- [ ] **Historique lisible** (M) : une vue « Qui a changé quoi » par table et par colonne (description avant / après, auteur, date), avec « Restaurer cette version ».
- [ ] **Brouillon conservé** (S) : une saisie non enregistrée (formulaire de relation, webhook, description) survit à un changement d'onglet.
- [ ] **Indication de synchronisation** (S) : date et résultat de la dernière synchronisation sur chaque base, et alerte discrète quand la source a changé depuis (webhooks et événements déjà disponibles côté API).

## Lot 7 — Accessibilité et petits écrans (P1, à valider)

Repris du jalon 11 car « en place mais non audité » ne suffit pas à dire que c'est intuitif pour tous.

- [ ] **Audit avec lecteur d'écran** (M) : NVDA et VoiceOver sur les quatre parcours ; corriger les annonces (états de sélection du diagramme, résultats de recherche, toasts).
- [ ] **Contrastes mesurés** (S) : _fait pour l'export HTML : le texte de l'en-tête d'une table passe en blanc ou en presque noir selon la couleur du domaine (meilleur contraste WCAG)._ Reste l'outil de mesure sur les deux thèmes, y compris les couleurs de domaines et d'étiquettes choisies par les utilisateurs (avertir quand une couleur est illisible).
- [ ] **Diagramme sans souris** (M) : déplacer une table et sélectionner une relation au clavier ; alternative textuelle (liste des relations) toujours disponible.
- [ ] **Téléphone et tablette** (M) : essai sur de vrais appareils ; en dessous de 960 px, un mode lecture du diagramme (zoom tactile, liste des tables liées) et des cibles tactiles de 44 px.
- [ ] **Préférences** (S) : taille du texte, mouvement réduit respecté partout, thème clair / sombre / système (déjà en place) rappelé dans un menu unique.

## Lot 9 — Fenêtres de saisie (P1)

Aujourd'hui la saisie se fait dans des formulaires qui s'insèrent dans la page (ajout d'une relation sous la liste, domaines et webhooks dans des cartes) : il faut deviner où ils apparaissent, le reste de la page continue de bouger, et rien ne guide la saisie. Une **fenêtre** (boîte de dialogue) dédiée à chaque création ou modification complexe rassemble tous les champs au même endroit, valide avant d'enregistrer et laisse la page intacte derrière.

**Règles communes à toutes les fenêtres** (à poser une fois, dans un composant de base) :
- ouverture par un bouton libellé, titre qui dit l'action (« Nouvelle relation »), focus sur le premier champ, **Échap** ou « Annuler » ferment sans rien enregistrer, **Entrée** valide quand c'est sans ambiguïté, le focus revient au bouton d'origine à la fermeture ;
- champs étiquetés avec une aide courte, **validation en ligne** (message sous le champ, bouton « Enregistrer » grisé tant que c'est incomplet, jamais d'erreur seulement après l'envoi) ;
- un **aperçu** de ce qui va être créé quand c'est utile (une phrase, un mini-schéma), et un récapitulatif après (« Relation ajoutée » avec « Annuler ») ;
- brouillon conservé si la fenêtre est fermée par erreur (voir lot 6), pas de perte de saisie sur une erreur du serveur ;
- accessible : piège à focus, `role="dialog"`, titre annoncé, plein écran sur téléphone (voir lot 7).

**Préférer un tableau à un formulaire** quand on saisit des éléments de même nature (tables, colonnes, relations, paires de colonnes, étiquettes…) : une ligne par élément, une colonne par champ, plutôt qu'une pile de champs répétée à chaque ajout. Le tableau montre d'un coup d'œil ce qui est déjà saisi, permet d'enchaîner les lignes sans rouvrir de fenêtre et rapproche la saisie de ce que les gens font déjà dans un tableur. Le formulaire reste pour un élément unique aux champs hétérogènes (webhook, connexion à une source).
- Navigation au clavier comme dans un tableur (Tab / Maj+Tab entre cellules, Entrée valide et passe à la ligne suivante, ligne vide en fin de tableau pour ajouter), **collage depuis un tableur** (plusieurs lignes d'un coup, avec aperçu des lignes reconnues et des erreurs) et suppression d'une ligne sans quitter la grille.
- Validation **par cellule** (message sous la cellule, ligne en erreur surlignée), doublons signalés avant l'envoi, enregistrement de l'ensemble en une seule fois (atomique) avec récapitulatif et « Annuler ».
- Un composant de grille commun (`EditableGrid`) posé une fois, réutilisé par les relations, les colonnes, les tables saisies à la main et les étiquettes ; accessible (`role="grid"`, annonce de la cellule courante) et repliée en cartes empilées sur téléphone.
- [x] **Grille de saisie commune** (M) : fait — `EditableGrid` (colonnes texte, case à cocher ou liste ; ligne vide en fin de tableau ; Tab entre cellules, Entrée descend ; validation par cellule ; suppression d'une ligne ; collage depuis un tableur avec aperçu ; cartes empilées sous 600 px ; `role="grid"`). _Reste : colonne « liste avec recherche » et couleur, brouillon conservé._
- [ ] **Relations en tableau** (M) : remplacer, dans « Nouvelle relation », la liste de paires de colonnes par une grille (table et colonne de départ → table et colonne d'arrivée, type, description) pour saisir plusieurs relations d'un coup, y compris les clés composites ; l'aperçu en clair reste sous la grille.
- [x] **Fenêtre « Nouvelle relation »** (M) : fait — `RelationDialog` : table de départ et d'arrivée avec recherche, paires de colonnes (clé composite : « Ajouter une paire »), type expliqué par une phrase, description, aperçu « a.x pointe vers b.y », doublons et colonnes identiques signalés avant l'envoi, erreurs du serveur affichées dans la fenêtre. Ouverte depuis la liste des relations d'une table. _Reste l'ouverture depuis le diagramme (glisser d'une colonne à l'autre) et la palette._
- [x] **Fenêtre « Modifier la relation »** (S) : fait — même fenêtre, préremplie, pour les relations manuelles (bouton crayon à côté de la corbeille) ; l'édition en place du type et de la description reste pour les corrections rapides.
- [x] **Fenêtre « Nouvelle table »** (L) : fait — `TableDialog` : nom (doublon refusé), type, description, domaine et colonnes en grille (nom, type, clé, nul permis, sensible, description) ; `POST TableInfo/manual` crée tout ou rien. Modèle tranché : `Tables.IsManual` (migration 18) ; la synchronisation ne la supprime pas (ni ses colonnes, ni ses relations manuelles) et ne l'annonce pas comme supprimée dans l'aperçu ; si la source gagne une table du même nom, **la source l'emporte** : la table garde son identifiant et sa documentation, le marquage est levé et les colonnes saisies à la main absentes de la source disparaissent.
- [x] **Fenêtre « Ajouter / modifier des colonnes »** (M) : fait — `ColumnsDialog` (grille) : pour une table saisie à la main, colonnes ajoutées à la suite (`POST TableInfo/{id}/columns`, atomique, refusé pour une table de la source) ; pour toute table, descriptions complétées en lot (nom et type verrouillés quand ils viennent de la source). Bouton sur chaque table de la page d'une base.
- [x] **Fenêtre « Nouveau domaine »** (M) : fait — `DomainDialog` : nom (doublon signalé), couleur, deux listes (disponibles / dans le domaine) avec recherche, « Ajouter les N tables trouvées » (par préfixe de nom) et nombre de relations qui apparaîtront. Sert à créer (écran de gestion, diagramme avec les tables affichées proposées d'office) et à modifier (« Modifier le domaine » remplace l'éditeur inséré dans la page). _Reste l'ajout « par table liée »._
- [x] **Fenêtre « Étiquette »** (S) : fait — `TagDialog` (écran de gestion : « Nouvelle étiquette », « Poser sur des tables ») : nom (doublon signalé), couleur, choix d'une base puis de tables avec recherche et « cocher les N trouvées ». _Reste la pose sur des colonnes ou plusieurs bases à la fois._
- [x] **Fenêtre « Webhook »** (S) : fait — `WebhookDialog` : adresse validée, secret, base, événements expliqués, actif, description. _Reste un « Envoyer un essai » avant d'enregistrer (l'API ne teste aujourd'hui qu'un webhook déjà enregistré)._
- [x] **Assistant d'import** (M) : fait — `ImportWizardDialog` : type de base → connexion (testée avant de continuer, l'erreur reste sur l'étape) → schémas, nom et description → récapitulatif en une phrase, puis import ; « Suivant » grisé tant que l'étape est incomplète, « Précédent » et Échap sans rien envoyer. « Modifier la connexion » ouvre la même fenêtre sans l'étape du type ni le nom. Ouverte depuis la page des bases et la page d'une base ; la page `/import/server` (palette Ctrl+K, liens existants) reste. _Reste : le fichier SQLite (la zone de dépôt reste une page), le choix de tables dans un schéma._
- [ ] **Fenêtre de confirmation commune** (S) : _`ConfirmDialog` sert maintenant aux webhooks, aux étiquettes (« retirée de toutes les bases, tables et colonnes ») et aux domaines (nombre de tables rangées, ce qui est perdu) via `DialogHelper.ConfirmAsync`._ Restent à migrer, sans urgence car déjà protégées en place : la suppression d'une base (nom à retaper, avec erreur serveur dans la zone), le remplacement d'un fichier et la resynchronisation (aperçu des pertes avant d'appliquer).

## Lot 8 — Mesurer et boucler (P1, en continu)

- [ ] **Tests utilisateurs** (M) : dérouler le [protocole](02-parcours-utilisateurs.md) avec 5 personnes au premier tour, noter les blocages, corriger, refaire un tour ; garder les enregistrements d'écran et les verbatims dans un dossier daté.
- [ ] **Indicateurs simples** (S) : taux de réussite sans aide par parcours, temps pour la première description publiée, nombre d'actions annulées par session (trop d'annulations = geste mal compris). Collectés sans données personnelles, avec consentement, ou lors des séances d'essai seulement.
- [ ] **Bouton « Un problème ? »** (S) : retour en deux clics depuis n'importe quel écran, avec capture et page courante, vers le dépôt d'issues.
- [ ] **Revue de cohérence** (S) : une fois par jalon, relire tous les écrans avec la liste des principes ci-dessus ; tenir un petit guide de style (vocabulaire, couleurs, gestes) pour que les nouvelles pages n'inventent rien.

---

## Ordre recommandé

```
Lot 8 (premier tour de tests) ─┐
Lot 1 ── Lot 3 ── Lot 2 ───────┴→ Lot 4 ── Lot 5 ── Lot 6
Lot 7 en parallèle dès le lot 1 (chaque nouvel écran est vérifié au fil de l'eau)
Lot 9 (fenêtres de saisie) : composant de base puis « Nouvelle relation », avec les lots 4 et 5
```

0. **Poser le composant de fenêtre de base** (règles du lot 9) avant d'ajouter la première fenêtre : « Nouvelle relation » est la plus utile et la moins risquée pour l'inaugurer ; « Nouvelle table » attend la décision sur le modèle.
1. **D'abord le test à cinq personnes** (lot 8) sur l'interface actuelle : il classera les lots 1 à 3 par gêne réelle et évitera de corriger ce qui ne gêne personne.
2. **Les gains rapides** : légende permanente, Échap, indicateurs d'état, libellés des icônes, états vides, glossaire (tous de taille S, aucun changement de modèle).
3. **Puis le démarrage** (lot 3) : c'est ce qui décide si quelqu'un revient après la première visite.
4. **Ensuite documenter et dessiner plus vite** (lots 4 et 5), qui réutilisent des briques existantes (édition en place, domaines, annulation).
5. **Enfin la confiance** (lot 6), plus utile une fois l'authentification en place (jalon 6) : l'historique y prend tout son sens avec de vrais auteurs.

## Critères de réussite

- Quatre personnes sur cinq réussissent chaque parcours **sans aide** et en moins de deux minutes pour les trois premiers.
- Aucune action n'est lancée en croyant faire autre chose : zéro annulation « je ne voulais pas ça » constatée pendant les séances.
- Tout état visuel du diagramme est expliqué sans lire de documentation, et se retire en un geste.
- Les quatre parcours sont réalisables au clavier seul et au lecteur d'écran, et sur un écran de 375 px de large pour la consultation.
