# L'interface

L'interface est un espace de travail, pas une suite de pages. Pour un parcours guidé, voir [guide-utilisateur.md](guide-utilisateur.md).

## Espace de travail

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

Un clic sur une description ou des notes (page d'une base, onglet d'une table ou panneau de détail) l'ouvre en édition : le texte s'enregistre en quittant le champ ou avec Entrée (Maj+Entrée va à la ligne), Échap annule, et « Annuler » dans la confirmation rétablit l'ancien texte ; les modifications sont historisées.

- Chaque colonne peut être marquée **sensible** (bouclier) ; chaque base, table et colonne porte des **étiquettes** (ex : « RGPD »).
- Un indicateur de **complétude** montre la part des tables et colonnes décrites et signale d'abord les colonnes sensibles sans description.
- Les relations détectées (clés étrangères) restent corrigeables ; on peut ajouter ses propres relations (1-1, 1-N, N-N), qu'aucune synchronisation ne touche.
- **Resynchroniser** commence par un **aperçu** des différences (ajouts, modifications, suppressions) et prévient quand une suppression ferait perdre du travail saisi (description, notes, marquage sensible, étiquettes, relation manuelle) ; rien n'est appliqué avant la confirmation.

## Diagramme et impact

Depuis une base, le bouton **Diagramme** ouvre un diagramme entité-relation interactif : choisissez les tables affichées (ou « Voisines » pour ne garder que la sélection et ses tables liées), glissez-les pour les placer, zoomez. Cliquer une table ou une colonne affiche ce qui dépend d'elle (rouge : « modifier cet élément peut casser ») et ce dont elle dépend (bleu), via `GET /Impact/…`.

Les **domaines** regroupent des tables (une table peut appartenir à plusieurs domaines) pour des diagrammes lisibles par sujet : créez un domaine, puis cochez ses tables ou glissez-les sur lui (depuis la liste, ou en sortant la table du dessin vers la liste des domaines). La vue d'un domaine montre ses tables et, en option, en pointillés et réduites aux colonnes de leurs relations, les tables des autres domaines qui leur sont liées ; la place de chaque table est mémorisée par domaine.

Le diagramme s'exporte en **SVG**, en **PNG** et en **Mermaid** (`erDiagram`, à coller dans Markdown GitHub/GitLab ou mermaid.live) ; l'export ne contient que les tables affichées.

## Recherche et thème

La barre de recherche de l'en-tête cherche dans les noms et descriptions des bases, tables, colonnes, index et domaines (Entrée pour lancer) ; un résultat ouvre la base avec la table concernée dépliée. Le bouton à droite de l'en-tête bascule entre thème clair et sombre (choix mémorisé). API : `GET /api/v1/Search?q=terme&limit=25`.
