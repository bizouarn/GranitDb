# Roadmap — rendre les interfaces plus intuitives

_Ne contient que ce qui reste à faire (le reste est dans l'historique git). Complète le jalon 11 de la [roadmap](01-roadmap.md) et le protocole de [validation](02-parcours-utilisateurs.md)._

## Principes

1. **Dire ce qui se passe, pas seulement le montrer.** Tout état visuel (sélection, surlignage, filtre actif, élément masqué) s'accompagne d'une phrase ou d'une légende, et d'un moyen évident de le retirer.
2. **Un geste, un résultat.** Cliquer édite en place, quitter enregistre, Échap annule, « Annuler » rétablit : la même règle partout.
3. **Le vocabulaire de l'utilisateur, pas celui du modèle.** Les termes techniques restent mais s'expliquent au survol.
4. **Rien d'important caché derrière une icône seule.** Chaque écran vide dit quoi faire ensuite.
5. **Mesurer avant de croire.** Un lot n'est « fini » que si des personnes réussissent les parcours du protocole sans aide.

Priorités : **P1** gêne constatée ou probable pour tous · **P2** confort · **P3** exploratoire. Effort : **S** (≤ 1 jour) · **M** (quelques jours) · **L** (une à deux semaines).

---

## Lot 3 — Bien démarrer (P1)

- [ ] **Accueil guidé selon l'état du catalogue** (M) : aucun contenu → « Importez votre première base » en grand, avec les trois voies (fichier SQLite, serveur, démonstration) ; une base sans description → « Documentez vos tables les plus utilisées » avec la complétude et un lien vers les tables à décrire en premier ; une base documentée → derniers changements et tables à revoir.
- [ ] **Visite en quatre étapes** (M) : explorateur, onglets, palette, diagramme ; non bloquante, relançable depuis l'aide, mémorisée.
- [ ] **États vides utiles** (S) : repasser sur les listes restantes (explorateur filtré, tables, envois d'un webhook).
- [ ] **Messages d'erreur actionnables** (M) : chaque erreur dit quoi faire ; harmoniser les erreurs du serveur (ProblemDetails ou texte, aujourd'hui mélangés) pour que le client les affiche toutes de la même façon.

## Lot 4 — Documenter plus vite (P2)

- [ ] **Saisie au clavier de bout en bout** (M) : Tab / Maj+Tab passent à la colonne suivante en enregistrant, comme dans un tableur ; Entrée valide et descend d'une ligne.
- [ ] **Édition en lot** (M) : sélectionner plusieurs colonnes (même nom dans plusieurs tables) et leur appliquer une description, une étiquette ou un marquage « sensible » en une fois.
- [ ] **Propagation proposée** (M) : quand une colonne décrite a le même nom et le même type ailleurs, proposer « Appliquer cette description aux 7 colonnes identiques ? ».
- [ ] **File « à documenter »** (M) : une vue des éléments sans description classés par utilité (tables les plus reliées d'abord).
- [ ] **Modèles de description** (S) : quelques tournures proposées (« Identifiant de… », « Date de… ») insérables d'un clic.

## Lot 5 — Le diagramme sans effort (P2)

- [ ] **Clic sur un lien** (S) : cliquer une relation la sélectionne, surligne ses colonnes des deux côtés et ouvre ses détails (type, description modifiables en place).
- [ ] **Minicarte, recentrage et « tout voir »** (M) : « Ajuster à l'écran », minicarte pour les grands plans, centrage automatique sur la sélection.
- [ ] **Dessin qui s'adapte** (M) : masquage automatique des colonnes sans relation au-delà d'un seuil, repli d'une table d'un clic.
- [ ] **Annuler / rétablir** (M) : déplacements de tables et changements de domaine (Ctrl+Z).
- [ ] **Changer la couleur d'un domaine existant** (S) : choix à la modification du domaine, depuis le panneau des domaines du diagramme et depuis « Étiquettes et domaines », avec annulation et retour à la couleur par défaut.
- [ ] **Réglages de domaine compris** (S) : un petit schéma qui montre l'effet de « Tables voisines » et des relations masquées.

## Lot 6 — Confiance et sécurité d'usage (P2)

- [ ] **Confirmations proportionnées** (S) : rien pour ce qui s'annule, une confirmation claire (avec le nombre d'éléments perdus) pour ce qui ne s'annule pas ; harmoniser sur le modèle de la suppression d'une base (retaper le nom).
- [ ] **Historique lisible** (M) : une vue « Qui a changé quoi » par table et par colonne (avant / après, auteur, date), avec « Restaurer cette version ».
- [ ] **Brouillon conservé** (S) : une saisie non enregistrée (formulaire de relation, webhook, description) survit à un changement d'onglet.
- [ ] **Indication de synchronisation** (S) : alerte discrète quand la source a changé depuis la dernière synchronisation (la date et le résultat sont déjà affichés).

## Lot 7 — Accessibilité et petits écrans (P1, à valider)

- [ ] **Audit avec lecteur d'écran** (M) : NVDA et VoiceOver sur les quatre parcours ; corriger les annonces (états de sélection du diagramme, résultats de recherche, toasts).
- [ ] **Contrastes mesurés** (S) : outil de mesure sur les deux thèmes, y compris les couleurs de domaines et d'étiquettes choisies par les utilisateurs (avertir quand une couleur est illisible).
- [ ] **Diagramme sans souris** (M) : déplacer une table et sélectionner une relation au clavier ; alternative textuelle (liste des relations) toujours disponible.
- [ ] **Téléphone et tablette** (M) : essai sur de vrais appareils ; sous 960 px, un mode lecture du diagramme (zoom tactile, liste des tables liées) et des cibles tactiles de 44 px.
- [ ] **Préférences** (S) : taille du texte, mouvement réduit respecté partout, thème clair / sombre / système rappelé dans un menu unique.

## Lot 9 — Fenêtres de saisie (P1)

Règles communes (déjà posées dans les fenêtres existantes) : titre qui dit l'action, focus sur le premier champ, Échap ou « Annuler » sans rien enregistrer, validation en ligne, aperçu de ce qui sera créé, accessibilité (piège à focus, `role="dialog"`, plein écran sur téléphone). Préférer un tableau (`EditableGrid`) à un formulaire pour des éléments de même nature.

- [ ] **Grille commune : suites** (S) : colonne « liste avec recherche » et couleur, brouillon conservé.
- [ ] **Relations en tableau** (M) : remplacer, dans « Nouvelle relation », la liste de paires de colonnes par une grille pour saisir plusieurs relations d'un coup, y compris les clés composites.
- [ ] **Nouvelle relation : autres points d'entrée** (S) : depuis le diagramme (glisser d'une colonne à l'autre) et la palette.
- [ ] **Étiquette : portée** (S) : pose sur des colonnes ou sur plusieurs bases à la fois.
- [ ] **Webhook : essai** (S) : « Envoyer un essai » avant d'enregistrer (l'API ne teste aujourd'hui qu'un webhook déjà enregistré).
- [ ] **Assistant d'import : suites** (M) : le fichier SQLite (la zone de dépôt reste une page) et le choix de tables dans un schéma.
- [ ] **Confirmation commune** (S) : migrer la suppression d'une base, le remplacement d'un fichier et la resynchronisation vers `ConfirmDialog` (sans urgence : déjà protégées en place).

## Lot 8 — Mesurer et boucler (P1, en continu)

- [ ] **Tests utilisateurs** (M) : dérouler le [protocole](02-parcours-utilisateurs.md) avec 5 personnes au premier tour, noter les blocages, corriger, refaire un tour ; garder les enregistrements d'écran et les verbatims dans un dossier daté.
- [ ] **Indicateurs simples** (S) : taux de réussite sans aide par parcours, temps pour la première description publiée, nombre d'actions annulées par session. Collectés sans données personnelles, avec consentement, ou lors des séances d'essai seulement.
- [ ] **Bouton « Un problème ? »** (S) : retour en deux clics depuis n'importe quel écran, avec capture et page courante, vers le dépôt d'issues.
- [ ] **Revue de cohérence** (S) : une fois par jalon, relire tous les écrans avec les principes ci-dessus ; tenir un petit guide de style (vocabulaire, couleurs, gestes).

---

## Ordre recommandé

1. **D'abord le test à cinq personnes** (lot 8) sur l'interface actuelle : il classera les lots par gêne réelle.
2. **Le démarrage** (lot 3) : c'est ce qui décide si quelqu'un revient après la première visite.
3. **Documenter et dessiner plus vite** (lots 4 et 5), avec les fenêtres de saisie restantes (lot 9).
4. **La confiance** (lot 6), plus utile avec de vrais auteurs dans l'historique.
5. **L'accessibilité** (lot 7) en parallèle, écran par écran.

## Critères de réussite

- Quatre personnes sur cinq réussissent chaque parcours **sans aide** et en moins de deux minutes pour les trois premiers.
- Aucune action n'est lancée en croyant faire autre chose : zéro annulation « je ne voulais pas ça » constatée pendant les séances.
- Tout état visuel du diagramme est expliqué sans lire de documentation, et se retire en un geste.
- Les quatre parcours sont réalisables au clavier seul et au lecteur d'écran, et sur un écran de 375 px de large pour la consultation.
