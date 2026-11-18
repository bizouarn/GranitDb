# Interface de GranitDb — maquette et protocole de validation

_Écrit avec le jalon 11 (refonte de l'interface). La maquette décrit ce qui est livré ; le protocole sert à le valider avec de vraies personnes, ce qui n'a pas encore été fait._

## Maquette

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│ ☰  Granit DB      [ 🔍 Rechercher une table, une colonne… ]  [Ctrl K]            ▥  ☾       │  barre du haut
├───────────────────┬──────────────────────────────────────────────────────┬───────────────────┤
│ EXPLORATEUR       │ ┌Boutique┐┌customers ✕┐┌Diagramme · Boutique┐        │ DÉTAILS           │
│ [Filtrer…      ⟳] │ │        ││▔▔▔▔▔▔▔▔▔▔▔││                    │        │                   │
│                   │ └────────┘└───────────┘└────────────────────┘        │ customers         │
│ FAVORIS           │                                                      │ Boutique · Table  │
│ ★ customers (B…)  │  Accueil › Boutique › customers                      │                   │
│ ★ Ventes (B…)     │                                                      │ Description       │
│                   │  Les clients  ✎                                      │ Les clients ✎     │
│ ▾ Boutique    ★ ◇ │  Notes… ✎                                            │                   │
│   ▸ customers  ★  │                                                      │ Étiquettes        │
│   ▾ orders        │  Colonne   Type    Description        Étiquettes  🛡 │ [RGPD ✕] [🏷]     │
│       id          │  id        INT     Clé interne ✎                     │                   │
│       customer_id │  email     TEXT    Adresse ✎          [RGPD]      🛡 │ Documentation     │
│   ▸ tags          │                                                      │ 33 % ▓▓▓░░░░░░░   │
│   DOMAINES        │  Relations                                           │ 1 colonne sensible│
│   ▸ ● Ventes   1  │   customers.id → orders.customer_id (One-to-Many)    │ sans description  │
│                   │   [+ Ajouter une relation]                           │                   │
│ ▸ RH              │                                                      │ Impact            │
│                   │                                                      │ 1 élément en dépend│
│                   │                                                      │ [Ouvrir][Diagramme]│
└───────────────────┴──────────────────────────────────────────────────────┴───────────────────┘
        ┌───────────────────────────────────────────┐
        │ ✓ Modification enregistrée.     [Annuler] │   confirmation, disparaît seule
        └───────────────────────────────────────────┘
```

Palette (Ctrl+K) :

```
        ┌────────────────────────────────────────────────────┐
        │ Tapez une commande ou cherchez une table…          │
        ├────────────────────────────────────────────────────┤
        │ ACTIONS                                            │
        │ ▸ Importer une base SQLite                accueil  │
        │   Fermer l'onglet                           Alt+W  │
        │ BASES                                              │
        │   Ouvrir Boutique                           Sqlite │
        │ TABLES                                             │
        │   customers                                Boutique│
        ├────────────────────────────────────────────────────┤
        │ ↑ ↓ pour choisir · Entrée pour ouvrir · Échap      │
        └────────────────────────────────────────────────────┘
```

Principes retenus : une seule façon de naviguer (l'arbre et les onglets), jamais de page « perdue » ; documenter là où l'on regarde (clic sur le texte, pas de formulaire) ; tout ce qui s'enregistre seul peut s'annuler ; le clavier fait tout ce que fait la souris.

## Protocole de validation

À dérouler avec 5 personnes de profils variés (au moins une qui ne connaît ni GranitDb ni Postman, une qui administre des bases de données). Ne rien expliquer avant : donner la consigne, observer, noter ce qui est cherché, ce qui est dit à voix haute, le temps passé. Un parcours est **réussi** s'il est terminé seul, sans aide, en moins de deux minutes.

Prérequis : une base SQLite d'exemple (celle des tests convient : `TestCatalog.SampleSql` crée clients, commandes, lignes de commande et une vue).

| # | Consigne donnée | Réussite | À observer |
|---|---|---|---|
| 1 | « Ajoutez cette base à l'outil et dites-moi combien elle a de tables. » | La base est importée (glisser-déposer ou clic) et le nombre de tables est lu dans l'arbre ou la page. | Trouvent-elles la zone de dépôt ? Comprennent-elles la confirmation de fin d'import ? |
| 2 | « Retrouvez la colonne qui contient l'adresse e-mail des clients et dites de quel type elle est. » | La colonne est trouvée (arbre, filtre, recherche ou palette) et son type lu. | Quel moyen choisissent-elles (arbre, barre de recherche, Ctrl+K) ? Le filtre de l'arbre est-il découvert ? |
| 3 | « Documentez la table des clients : une phrase pour la table, une pour l'e-mail, et indiquez que l'e-mail est une donnée personnelle. » | Description de la table et de la colonne enregistrées, colonne marquée sensible. | Comprennent-elles que le texte est modifiable en cliquant ? Que l'enregistrement est automatique ? Trouvent-elles le bouclier ? Utilisent-elles « Annuler » après une erreur de frappe ? |
| 4 | « Faites un schéma des tables liées aux commandes et enregistrez-le en image. » | Un diagramme (domaine ou sélection de tables) est exporté en PNG ou SVG. | Trouvent-elles « Diagramme » ? La notion de domaine est-elle comprise sans explication ? Le glisser-déposer d'une table vers un domaine est-il tenté spontanément ? |

Questions à poser à la fin : « Qu'est-ce qui vous a le plus gêné ? », « Qu'avez-vous cherché sans le trouver ? », « Qu'est-ce que vous referiez d'une autre façon ? », « Sur une échelle de 1 à 5, quelle confiance pour ne pas perdre votre travail ? » (cette dernière mesure l'effet de l'enregistrement automatique et de « Annuler »).

À consigner pour chaque séance : parcours, réussite oui/non, durée, blocages, phrases marquantes, version testée. Les corrections issues des séances se rangent dans la roadmap (jalon 11), triées par fréquence : un blocage vu chez trois personnes sur cinq passe avant tout le reste.

## Accessibilité : vérifications à faire

- Parcourir tout l'espace de travail **au clavier seul** (Tab, flèches de l'arbre, Ctrl+K, Alt+W) sans jamais perdre le focus.
- Essayer avec un lecteur d'écran (NVDA ou VoiceOver) : l'arbre est annoncé comme un arbre à niveaux, les onglets comme une liste d'onglets, les confirmations sont lues sans voler le focus.
- Mesurer les contrastes des deux thèmes (les couleurs sont choisies pour 4,5:1, à confirmer avec un outil).
- Zoom du navigateur à 200 % et affichage sur téléphone : rien ne doit être coupé, les tiroirs doivent se superposer au contenu.
