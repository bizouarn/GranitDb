# Authentification et rôles (section `Auth`)

Désactivée par défaut (`Auth:Enabled` à `false`) : le poste de développement et le catalogue SQLite local restent utilisables sans compte. Activée, **toute route de l'API demande une session** (401 sinon) ; l'interface affiche alors un écran « Se connecter avec… ». La session est un cookie émis par l'API (`HttpOnly`, glissant, `Auth:SessionHours` heures, 8 par défaut) ; le navigateur ne détient aucun jeton lisible par le script de la page.

## Fournisseurs

**Connexion GitHub** (OAuth App : *Settings → Developer settings → OAuth Apps*, adresse de retour `https://<api>/auth/callback/github`) et/ou **connecteur OpenID Connect générique** déclaré par configuration seule (Entra ID, Keycloak, Okta, Google… adresse de retour `https://<api>/auth/callback/<nom>`). Chaque fournisseur configuré donne un bouton sur l'écran de connexion ; plusieurs peuvent coexister.

```json
"Auth": {
  "Enabled": true,
  "GitHub": { "ClientId": "…", "ClientSecret": "…" },
  "Providers": {
    "corp": {
      "DisplayName": "SSO d'entreprise",
      "Authority": "https://sso.exemple.fr/realms/granit",
      "ClientId": "granitdb", "ClientSecret": "…",
      "Scopes": ["openid", "profile", "email"],
      "LoginClaim": "preferred_username", "NameClaim": "name", "GroupsClaim": "groups"
    }
  },
  "Roles": {
    "Admins": ["octocat"], "Editors": ["alice"], "Readers": [],
    "AdminGroups": ["monorg/admins"], "EditorGroups": ["monorg/dev", "corp-editors"], "ReaderGroups": ["monorg"],
    "DefaultRole": "None"
  }
},
"Cors": { "AllowedOrigins": ["https://doc.exemple.fr"] }
```

- **Secrets** : `ClientSecret` par variable d'environnement (`Auth__GitHub__ClientSecret`, `Auth__Providers__corp__ClientSecret`), user-secrets ou coffre, jamais dans le dépôt.
- **Groupes GitHub** : `organisation` pour tout membre, `organisation/equipe` pour une équipe (portée `read:org` demandée seulement si des groupes sont configurés ; les organisations qui restreignent l'accès aux OAuth Apps doivent l'approuver). Au plus 100 organisations et 100 équipes par compte sont lues.
- **Groupes OpenID Connect** : revendication `GroupsClaim` du jeton d'identité (à activer côté fournisseur : mapper de groupes Keycloak, « groups claim » Entra ID).

## Rôles

Lecteur (lit), éditeur (écrit), administrateur (en plus : journal d'audit, webhooks, sources serveur, import par chemin local, planification, profilage).

Une liste de logins (`octocat` ou `github:octocat` / `corp:jdupont` pour viser un fournisseur) et/ou des groupes ; le rôle le plus élevé gagne ; sans correspondance, `DefaultRole` (`None` : accès refusé, le compte retourne à l'écran de connexion avec un message). Le premier administrateur se déclare dans `Admins` (ou `AdminGroups`) : l'API refuse de démarrer sans. Les rôles sont calculés **à la connexion** : un changement de groupe s'applique à la session suivante.

## CORS, cookies et proxy

- **CORS** : déclarez l'adresse de l'interface dans `Cors:AllowedOrigins` (avec cookies). Sans cela, l'API répond à toutes les origines mais sans cookies : seule sa propre origine peut se connecter (un avertissement le dit au démarrage). L'interface ne joint ses identifiants aux appels vers une autre origine que si l'API annonce l'authentification : sans elle, aucune origine n'est à déclarer.
- **SameSite** : interface et API sur le même site : `Lax` (défaut) ; sur deux sites différents : `Auth:CookieSameSite` = `None` (HTTPS obligatoire, cookie `Secure`).
- **Retour de connexion** : l'adresse de retour n'est suivie que si elle est un chemin de l'API ou une origine de `Cors:AllowedOrigins`.
- **Écritures** : avec l'authentification, chaque POST/PUT/DELETE doit porter l'en-tête `X-GranitDb-Client` (l'interface l'ajoute) : un site tiers ne peut pas l'envoyer sans autorisation CORS, ce qui ferme la voie aux requêtes forgées avec le cookie.
- **Derrière un proxy ou un conteneur** : l'API calcule l'adresse de retour du fournisseur depuis la requête ; activez les en-têtes transmis (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`) pour qu'elle reflète l'adresse publique (HTTPS).

## Bases publiques

`IsPublic` (réglage réservé aux administrateurs ; un éditeur qui modifie une base garde sa valeur) : la base est lisible **sans compte**, en lecture seule : fiche, tables, colonnes, relations, domaines, complétude, impact, diagrammes, exports et recherche (limitée à ces bases). L'interface s'ouvre alors en « Visiteur » avec un menu « Se connecter » ; le serveur et la base d'origine ne sont pas montrés. Tout le reste (journal, webhooks, historique, étiquettes globales, écritures) demande une session. `Auth:AllowPublicDatabases` à `false` ferme tout.

## Secrets hors du fichier

La chaîne de connexion d'une source de `Catalog:StartupSources` peut être une référence, `env:NOM` ou `file:/run/secrets/nom` (secret Docker ou Kubernetes). Les clés qui chiffrent les connexions enregistrées peuvent être chiffrées à leur tour : `Catalog:KeysProtection` = `Dpapi` (Windows) ou `Certificate` ; les clés déjà créées restent lisibles, seules les nouvelles sont chiffrées. Un coffre distant (Key Vault…) se branche sur le même point de `Program.cs` mais n'est pas fourni.

## Journal et historique

L'auteur de chaque écriture est le login du compte (`History.ChangedBy`, `GET Audit`).

## API

`GET /api/v1/Auth/me` dit si l'authentification est active, qui est connecté et avec quel rôle ; `GET /api/v1/Auth/login/{provider}?returnUrl=` démarre la connexion ; `POST /api/v1/Auth/logout` ferme la session.
