# Jeu de test : boutique

`shop.sqlite` (à la racine) est une base SQLite de boutique en ligne : catégories, fournisseurs, produits, entrepôts/stock, clients, adresses, commandes, lignes, paiements, avis (11 tables, clés étrangères, index, clé primaire composite).

Pour l'importer : page Bases > déposer `shop.sqlite`, ou `Catalog:StartupSources`.

Pour la régénérer (données déterministes) :

    dotnet run samples/shop/build-shop.cs -- shop.sqlite samples/shop/schema.sql
