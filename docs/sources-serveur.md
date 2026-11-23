# Documenter une base SQL Server, PostgreSQL ou MySQL

Pour un fichier SQLite, il suffit de le glisser dans l'interface ([guide-utilisateur.md](guide-utilisateur.md)). Cette page traite des bases hébergées sur un serveur.

> **API dans Docker et serveur sur votre poste :** `localhost` et les adresses IPv6 locales (`fe80::…%11`) désignent le conteneur, pas votre machine. Saisissez `host.docker.internal` comme serveur (et le port TCP de l'instance ; activez « Faire confiance au certificat » pour un certificat auto-signé). Le serveur doit accepter TCP/IP (SQL Server Configuration Manager) et le pare-feu Windows laisser passer le port.

## Utilisation

Activé par défaut (désactivable : `Catalog:AllowSqlServerSources=false`, en Docker `ALLOW_SQLSERVER_SOURCES=false`) : « Connecter un serveur SQL Server » depuis l'accueil (ou Ctrl+K) : type de base, serveur, port, base, authentification (identifiant et mot de passe, ou compte Windows du processus de l'API pour SQL Server), chiffrement.

1. **Tester la connexion** dit ce qui échoue : identifiants refusés, base absente, serveur injoignable, certificat non approuvé, droits insuffisants.
2. Une fois le test réussi, on choisit les schémas à documenter (SQL Server, PostgreSQL).
3. **Importer.**

La base se resynchronise ensuite comme un fichier SQLite (aperçu des différences compris) ; **Modifier la connexion** change le mot de passe ou les schémas. Le moteur d'une base existante ne peut pas changer.

## Ce qui est lu

**Seul le schéma est lu** (tables, colonnes, clés, index), jamais les données : un compte qui n'a que `VIEW DEFINITION` suffit sous SQL Server. Créez-en un dédié :

```sql
CREATE LOGIN granitdb WITH PASSWORD = '…';
USE MaBase;
CREATE USER granitdb FOR LOGIN granitdb;
GRANT VIEW DEFINITION TO granitdb;
```

La connexion déclare une intention de lecture seule. Seule exception : le [profilage](aides-documentation.md), désactivé par défaut, qui demande de pouvoir lire les tables (`db_datareader`).

## Moteurs

| Moteur | Compte nécessaire | Particularités |
|---|---|---|
| SQL Server (`SqlServer`) | `VIEW DEFINITION` | Tables hors `dbo` nommées `schéma.table` ; authentification intégrée possible |
| PostgreSQL (`PostgreSql`) | connexion à la base et `USAGE` sur les schémas | Tables de `public` sans préfixe, les autres `schéma.table` ; vues et vues matérialisées comprises ; transactions ouvertes en lecture seule (`default_transaction_read_only`) |
| MySQL / MariaDB (`MySql`) | `SELECT` sur la base | « Schéma » et « base » sont la même chose : on documente la base de la connexion ; clés étrangères vers une autre base ignorées ; « Chiffrer » exige un certificat vérifiable (`VerifyCA`) sauf si « Faire confiance au certificat » est coché |

Authentification : identifiant et mot de passe ; le compte Windows de l'API (`Integrated`) n'existe que pour SQL Server. Routes : `import/server` (tous moteurs, champ `engine`) et `import/sqlserver` (force SQL Server). Le même interrupteur `Catalog:AllowSqlServerSources` couvre les trois moteurs.

## Sécurité des connexions enregistrées

- **La connexion est chiffrée** (ASP.NET Core Data Protection) avant d'être enregistrée dans le catalogue ; seul un libellé « serveur / base » est affichable, et aucune réponse de l'API ne renvoie le mot de passe ni la connexion.
- Les clés sont des fichiers de `KeysDirectory` : **sauvegardez-les avec le catalogue** et protégez ce dossier. Sans elles, une connexion enregistrée n'est plus lisible et l'interface demande de la saisir de nouveau.
- **Activé par défaut** (le catalogue peut être SQLite) : l'API se connecte alors à un serveur choisi par l'appelant, ce qui revient à lui prêter le réseau du serveur. Sans authentification, désactivez ceci hors d'un poste ou d'un réseau de confiance.
