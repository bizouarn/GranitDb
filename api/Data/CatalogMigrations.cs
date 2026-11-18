namespace GranitDB.API.Data;

/// <summary>
///     Une migration du catalogue : une liste d'instructions SQL par fournisseur, exécutées une par une dans une
///     transaction. Une migration appliquée n'est jamais modifiée : on en ajoute une nouvelle.
/// </summary>
public record Migration(int Version, string Name, string[] Sqlite, string[] SqlServer);

/// <summary>Historique des migrations du catalogue (SQLite et SQL Server).</summary>
public static class CatalogMigrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "Schéma initial",
            Sqlite:
            [
                """
                CREATE TABLE IF NOT EXISTS "Databases" (
                    "Id" TEXT PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "Type" TEXT NOT NULL,
                    "IsPublic" INTEGER NOT NULL DEFAULT 0,
                    "Description" TEXT,
                    "ProjectUrl" TEXT,
                    "DatabaseUrl" TEXT,
                    "SourcePath" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "Tables" (
                    "Id" TEXT PRIMARY KEY,
                    "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "Name" TEXT NOT NULL,
                    "TableType" TEXT NOT NULL DEFAULT 'Table',
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "Columns" (
                    "Id" TEXT PRIMARY KEY,
                    "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "TableId" TEXT NOT NULL REFERENCES "Tables"("Id"),
                    "Name" TEXT NOT NULL,
                    "DataType" TEXT,
                    "IsNullable" INTEGER NOT NULL DEFAULT 1,
                    "IsPrimaryKey" INTEGER NOT NULL DEFAULT 0,
                    "DefaultValue" TEXT,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """
                CREATE TABLE IF NOT EXISTS "Relations" (
                    "Id" TEXT PRIMARY KEY,
                    "FirstId" TEXT NOT NULL REFERENCES "Columns"("Id"),
                    "SecondId" TEXT NOT NULL REFERENCES "Columns"("Id"),
                    "RelationType" TEXT,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """
            ],
            // Reprend aussi les catalogues SQL Server créés avec l'ancien script InitV1.sql.
            SqlServer:
            [
                """
                IF OBJECT_ID(N'dbo.Databases', N'U') IS NULL
                CREATE TABLE dbo.Databases (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY, Name VARCHAR(60), Type VARCHAR(50), IsPublic BIT,
                    Description VARCHAR(MAX), ProjectUrl VARCHAR(MAX), DatabaseUrl VARCHAR(MAX), MetaInfo VARCHAR(MAX))
                """,
                """
                IF OBJECT_ID(N'dbo.Tables', N'U') IS NULL
                CREATE TABLE dbo.Tables (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY, Name VARCHAR(255) NOT NULL, DatabaseId VARCHAR(36) NOT NULL,
                    Description VARCHAR(MAX), MetaInfo VARCHAR(MAX))
                """,
                """
                IF OBJECT_ID(N'dbo.Columns', N'U') IS NULL
                CREATE TABLE dbo.Columns (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY, DatabaseId VARCHAR(36), TableId VARCHAR(36), Name VARCHAR(60),
                    DataType VARCHAR(50), IsNullable BIT, Description VARCHAR(MAX), MetaInfo VARCHAR(MAX))
                """,
                """
                IF OBJECT_ID(N'dbo.Relations', N'U') IS NULL
                CREATE TABLE dbo.Relations (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY, FirstId VARCHAR(36), SecondId VARCHAR(36),
                    RelationType VARCHAR(50), Description VARCHAR(MAX), MetaInfo VARCHAR(MAX))
                """,
                "IF COL_LENGTH('dbo.Databases', 'SourcePath') IS NULL ALTER TABLE dbo.Databases ADD SourcePath VARCHAR(MAX) NULL",
                "IF COL_LENGTH('dbo.Tables', 'TableType') IS NULL ALTER TABLE dbo.Tables ADD TableType VARCHAR(20) NOT NULL CONSTRAINT DF_Tables_TableType DEFAULT 'Table'",
                "IF COL_LENGTH('dbo.Columns', 'IsPrimaryKey') IS NULL ALTER TABLE dbo.Columns ADD IsPrimaryKey BIT NOT NULL CONSTRAINT DF_Columns_IsPrimaryKey DEFAULT 0",
                "IF COL_LENGTH('dbo.Columns', 'DefaultValue') IS NULL ALTER TABLE dbo.Columns ADD DefaultValue VARCHAR(MAX) NULL"
            ]),

        new(2, "Index de recherche",
            Sqlite:
            [
                """CREATE INDEX IF NOT EXISTS "IX_Tables_DatabaseId" ON "Tables"("DatabaseId")""",
                """CREATE INDEX IF NOT EXISTS "IX_Columns_TableId" ON "Columns"("TableId")""",
                """CREATE INDEX IF NOT EXISTS "IX_Columns_DatabaseId" ON "Columns"("DatabaseId")""",
                """CREATE INDEX IF NOT EXISTS "IX_Relations_FirstId" ON "Relations"("FirstId")""",
                """CREATE INDEX IF NOT EXISTS "IX_Relations_SecondId" ON "Relations"("SecondId")"""
            ],
            SqlServer:
            [
                Index("IX_Tables_DatabaseId", "Tables", "DatabaseId"),
                Index("IX_Columns_TableId", "Columns", "TableId"),
                Index("IX_Columns_DatabaseId", "Columns", "DatabaseId"),
                Index("IX_Relations_FirstId", "Relations", "FirstId"),
                Index("IX_Relations_SecondId", "Relations", "SecondId")
            ]),

        // SQLite stocke déjà tout en Unicode avec les bonnes contraintes : rien à faire de ce côté.
        new(3, "Unicode et largeurs de colonnes (SQL Server)",
            Sqlite: [],
            SqlServer:
            [
                "UPDATE dbo.Databases SET Name = '' WHERE Name IS NULL",
                "UPDATE dbo.Databases SET Type = '' WHERE Type IS NULL",
                "UPDATE dbo.Databases SET IsPublic = 0 WHERE IsPublic IS NULL",
                "UPDATE dbo.Columns SET Name = '' WHERE Name IS NULL",
                "UPDATE dbo.Columns SET IsNullable = 1 WHERE IsNullable IS NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN Name NVARCHAR(255) NOT NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN Type NVARCHAR(50) NOT NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN IsPublic BIT NOT NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN Description NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN ProjectUrl NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN DatabaseUrl NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN SourcePath NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Databases ALTER COLUMN MetaInfo NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Tables ALTER COLUMN Name NVARCHAR(255) NOT NULL",
                "ALTER TABLE dbo.Tables ALTER COLUMN Description NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Tables ALTER COLUMN MetaInfo NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN Name NVARCHAR(255) NOT NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN DataType NVARCHAR(100) NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN IsNullable BIT NOT NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN DefaultValue NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN Description NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Columns ALTER COLUMN MetaInfo NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Relations ALTER COLUMN RelationType NVARCHAR(50) NULL",
                "ALTER TABLE dbo.Relations ALTER COLUMN Description NVARCHAR(MAX) NULL",
                "ALTER TABLE dbo.Relations ALTER COLUMN MetaInfo NVARCHAR(MAX) NULL"
            ]),

        // SQLite a ses clés étrangères depuis le schéma initial. Côté SQL Server on supprime d'abord les lignes
        // orphelines (inaccessibles depuis l'API) pour que les contraintes puissent être créées.
        new(4, "Clés étrangères (SQL Server)",
            Sqlite: [],
            SqlServer:
            [
                """
                DELETE FROM dbo.Relations WHERE FirstId IN (SELECT Id FROM dbo.Columns WHERE
                    DatabaseId NOT IN (SELECT Id FROM dbo.Databases)
                    OR TableId NOT IN (SELECT Id FROM dbo.Tables WHERE DatabaseId IN (SELECT Id FROM dbo.Databases)))
                OR SecondId IN (SELECT Id FROM dbo.Columns WHERE
                    DatabaseId NOT IN (SELECT Id FROM dbo.Databases)
                    OR TableId NOT IN (SELECT Id FROM dbo.Tables WHERE DatabaseId IN (SELECT Id FROM dbo.Databases)))
                """,
                """
                DELETE FROM dbo.Columns WHERE DatabaseId NOT IN (SELECT Id FROM dbo.Databases)
                    OR TableId NOT IN (SELECT Id FROM dbo.Tables WHERE DatabaseId IN (SELECT Id FROM dbo.Databases))
                """,
                "DELETE FROM dbo.Tables WHERE DatabaseId NOT IN (SELECT Id FROM dbo.Databases)",
                "DELETE FROM dbo.Relations WHERE FirstId NOT IN (SELECT Id FROM dbo.Columns) OR SecondId NOT IN (SELECT Id FROM dbo.Columns)",
                ForeignKey("FK_Tables_Databases", "Tables", "DatabaseId", "Databases"),
                ForeignKey("FK_Columns_Databases", "Columns", "DatabaseId", "Databases"),
                ForeignKey("FK_Columns_Tables", "Columns", "TableId", "Tables"),
                ForeignKey("FK_Relations_First", "Relations", "FirstId", "Columns"),
                ForeignKey("FK_Relations_Second", "Relations", "SecondId", "Columns")
            ]),

        // Les lignes existantes reçoivent la position 0 ; la prochaine synchronisation les met à jour.
        new(5, "Position des colonnes",
            Sqlite: ["""ALTER TABLE "Columns" ADD COLUMN "Position" INTEGER NOT NULL DEFAULT 0"""],
            SqlServer:
            [
                "IF COL_LENGTH('dbo.Columns', 'Position') IS NULL ALTER TABLE dbo.Columns ADD Position INT NOT NULL CONSTRAINT DF_Columns_Position DEFAULT 0"
            ]),

        new(6, "Index des tables",
            Sqlite:
            [
                """
                CREATE TABLE IF NOT EXISTS "Indexes" (
                    "Id" TEXT PRIMARY KEY,
                    "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "TableId" TEXT NOT NULL REFERENCES "Tables"("Id"),
                    "Name" TEXT NOT NULL,
                    "IsUnique" INTEGER NOT NULL DEFAULT 0,
                    "ColumnNames" TEXT NOT NULL,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """CREATE INDEX IF NOT EXISTS "IX_Indexes_TableId" ON "Indexes"("TableId")""",
                """CREATE INDEX IF NOT EXISTS "IX_Indexes_DatabaseId" ON "Indexes"("DatabaseId")"""
            ],
            SqlServer:
            [
                """
                IF OBJECT_ID(N'dbo.Indexes', N'U') IS NULL
                CREATE TABLE dbo.Indexes (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    DatabaseId VARCHAR(36) NOT NULL CONSTRAINT FK_Indexes_Databases FOREIGN KEY REFERENCES dbo.Databases(Id),
                    TableId VARCHAR(36) NOT NULL CONSTRAINT FK_Indexes_Tables FOREIGN KEY REFERENCES dbo.Tables(Id),
                    Name NVARCHAR(255) NOT NULL,
                    IsUnique BIT NOT NULL DEFAULT 0,
                    ColumnNames NVARCHAR(MAX) NOT NULL,
                    Description NVARCHAR(MAX) NULL,
                    MetaInfo NVARCHAR(MAX) NULL)
                """,
                Index("IX_Indexes_TableId", "Indexes", "TableId"),
                Index("IX_Indexes_DatabaseId", "Indexes", "DatabaseId")
            ]),

        // Pas de clé étrangère vers l'entité : l'historique survit à sa suppression.
        new(7, "Historique des modifications",
            Sqlite:
            [
                """
                CREATE TABLE IF NOT EXISTS "History" (
                    "Id" TEXT PRIMARY KEY,
                    "EntityType" TEXT NOT NULL,
                    "EntityId" TEXT NOT NULL,
                    "Field" TEXT NOT NULL,
                    "OldValue" TEXT,
                    "NewValue" TEXT,
                    "ChangedAt" TEXT NOT NULL,
                    "ChangedBy" TEXT
                )
                """,
                """CREATE INDEX IF NOT EXISTS "IX_History_EntityId" ON "History"("EntityId")"""
            ],
            SqlServer:
            [
                """
                IF OBJECT_ID(N'dbo.History', N'U') IS NULL
                CREATE TABLE dbo.History (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    EntityType VARCHAR(20) NOT NULL,
                    EntityId VARCHAR(36) NOT NULL,
                    Field VARCHAR(50) NOT NULL,
                    OldValue NVARCHAR(MAX) NULL,
                    NewValue NVARCHAR(MAX) NULL,
                    ChangedAt DATETIME2 NOT NULL,
                    ChangedBy NVARCHAR(255) NULL)
                """,
                Index("IX_History_EntityId", "History", "EntityId")
            ]),

        // Jalon 5 : colonnes sensibles, domaines (regroupement de tables, avec la place de chaque table), étiquettes.
        // Les tables de liaison ont leur propre Id (comme toute entité du catalogue) et une contrainte d'unicité.
        new(8, "Gouvernance : colonnes sensibles, domaines, étiquettes",
            Sqlite:
            [
                """ALTER TABLE "Columns" ADD COLUMN "IsSensitive" INTEGER NOT NULL DEFAULT 0""",
                """
                CREATE TABLE IF NOT EXISTS "Domains" (
                    "Id" TEXT PRIMARY KEY,
                    "DatabaseId" TEXT NOT NULL REFERENCES "Databases"("Id"),
                    "Name" TEXT NOT NULL COLLATE NOCASE,
                    "Color" TEXT,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Domains_Name" ON "Domains"("DatabaseId", "Name")""",
                """
                CREATE TABLE IF NOT EXISTS "DomainTables" (
                    "Id" TEXT PRIMARY KEY,
                    "DomainId" TEXT NOT NULL REFERENCES "Domains"("Id"),
                    "TableId" TEXT NOT NULL REFERENCES "Tables"("Id"),
                    "X" REAL,
                    "Y" REAL,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """CREATE UNIQUE INDEX IF NOT EXISTS "UX_DomainTables" ON "DomainTables"("DomainId", "TableId")""",
                """CREATE INDEX IF NOT EXISTS "IX_DomainTables_TableId" ON "DomainTables"("TableId")""",
                """
                CREATE TABLE IF NOT EXISTS "Tags" (
                    "Id" TEXT PRIMARY KEY,
                    "Name" TEXT NOT NULL COLLATE NOCASE,
                    "Color" TEXT,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Tags_Name" ON "Tags"("Name")""",
                """
                CREATE TABLE IF NOT EXISTS "TagLinks" (
                    "Id" TEXT PRIMARY KEY,
                    "TagId" TEXT NOT NULL REFERENCES "Tags"("Id"),
                    "EntityType" TEXT NOT NULL,
                    "EntityId" TEXT NOT NULL,
                    "Description" TEXT,
                    "MetaInfo" TEXT
                )
                """,
                """CREATE UNIQUE INDEX IF NOT EXISTS "UX_TagLinks" ON "TagLinks"("TagId", "EntityType", "EntityId")""",
                """CREATE INDEX IF NOT EXISTS "IX_TagLinks_EntityId" ON "TagLinks"("EntityId")"""
            ],
            SqlServer:
            [
                "IF COL_LENGTH('dbo.Columns', 'IsSensitive') IS NULL ALTER TABLE dbo.Columns ADD IsSensitive BIT NOT NULL CONSTRAINT DF_Columns_IsSensitive DEFAULT 0",
                """
                IF OBJECT_ID(N'dbo.Domains', N'U') IS NULL
                CREATE TABLE dbo.Domains (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    DatabaseId VARCHAR(36) NOT NULL CONSTRAINT FK_Domains_Databases FOREIGN KEY REFERENCES dbo.Databases(Id),
                    Name NVARCHAR(255) NOT NULL,
                    Color VARCHAR(20) NULL,
                    Description NVARCHAR(MAX) NULL,
                    MetaInfo NVARCHAR(MAX) NULL)
                """,
                UniqueIndex("UX_Domains_Name", "Domains", "DatabaseId, Name"),
                """
                IF OBJECT_ID(N'dbo.DomainTables', N'U') IS NULL
                CREATE TABLE dbo.DomainTables (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    DomainId VARCHAR(36) NOT NULL CONSTRAINT FK_DomainTables_Domains FOREIGN KEY REFERENCES dbo.Domains(Id),
                    TableId VARCHAR(36) NOT NULL CONSTRAINT FK_DomainTables_Tables FOREIGN KEY REFERENCES dbo.Tables(Id),
                    X FLOAT NULL,
                    Y FLOAT NULL,
                    Description NVARCHAR(MAX) NULL,
                    MetaInfo NVARCHAR(MAX) NULL)
                """,
                UniqueIndex("UX_DomainTables", "DomainTables", "DomainId, TableId"),
                Index("IX_DomainTables_TableId", "DomainTables", "TableId"),
                """
                IF OBJECT_ID(N'dbo.Tags', N'U') IS NULL
                CREATE TABLE dbo.Tags (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    Name NVARCHAR(255) NOT NULL,
                    Color VARCHAR(20) NULL,
                    Description NVARCHAR(MAX) NULL,
                    MetaInfo NVARCHAR(MAX) NULL)
                """,
                UniqueIndex("UX_Tags_Name", "Tags", "Name"),
                """
                IF OBJECT_ID(N'dbo.TagLinks', N'U') IS NULL
                CREATE TABLE dbo.TagLinks (
                    Id VARCHAR(36) NOT NULL PRIMARY KEY,
                    TagId VARCHAR(36) NOT NULL CONSTRAINT FK_TagLinks_Tags FOREIGN KEY REFERENCES dbo.Tags(Id),
                    EntityType VARCHAR(20) NOT NULL,
                    EntityId VARCHAR(36) NOT NULL,
                    Description NVARCHAR(MAX) NULL,
                    MetaInfo NVARCHAR(MAX) NULL)
                """,
                UniqueIndex("UX_TagLinks", "TagLinks", "TagId, EntityType, EntityId"),
                Index("IX_TagLinks_EntityId", "TagLinks", "EntityId")
            ]),

        // Sources SQL Server enregistrées dans le catalogue : la connexion est chiffrée, son libellé ne l'est pas.
        new(9, "Connexion des sources SQL Server",
            Sqlite:
            [
                """ALTER TABLE "Databases" ADD COLUMN "SourceSecret" TEXT""",
                """ALTER TABLE "Databases" ADD COLUMN "SourceLabel" TEXT"""
            ],
            SqlServer:
            [
                "IF COL_LENGTH('dbo.Databases', 'SourceSecret') IS NULL ALTER TABLE dbo.Databases ADD SourceSecret NVARCHAR(MAX) NULL",
                "IF COL_LENGTH('dbo.Databases', 'SourceLabel') IS NULL ALTER TABLE dbo.Databases ADD SourceLabel NVARCHAR(500) NULL"
            ])
    ];

    private static string UniqueIndex(string name, string table, string columns)
    {
        return $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}' AND object_id = OBJECT_ID(N'dbo.{table}')) " +
               $"CREATE UNIQUE INDEX {name} ON dbo.{table}({columns})";
    }

    private static string Index(string name, string table, string column)
    {
        return $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}' AND object_id = OBJECT_ID(N'dbo.{table}')) " +
               $"CREATE INDEX {name} ON dbo.{table}({column})";
    }

    private static string ForeignKey(string name, string table, string column, string refTable)
    {
        return $"IF OBJECT_ID(N'dbo.{name}', N'F') IS NULL " +
               $"ALTER TABLE dbo.{table} ADD CONSTRAINT {name} FOREIGN KEY ({column}) REFERENCES dbo.{refTable}(Id)";
    }
}
