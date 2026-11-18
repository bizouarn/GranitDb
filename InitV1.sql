-- Référence : schéma du catalogue SQL Server après toutes les migrations (version 6).
-- Ne pas exécuter à la main : l'API crée la base si besoin et applique les migrations au démarrage
-- (api/Data/CatalogMigrations.cs, version enregistrée dans dbo.SchemaVersion).
-- En SQLite (par défaut), le catalogue est créé automatiquement.
CREATE TABLE dbo.Databases (
    Id VARCHAR(36) NOT NULL PRIMARY KEY,
    Name NVARCHAR(255) NOT NULL,
    Type NVARCHAR(50) NOT NULL,
    IsPublic BIT NOT NULL,
    Description NVARCHAR(MAX) NULL,
    ProjectUrl NVARCHAR(MAX) NULL,
    DatabaseUrl NVARCHAR(MAX) NULL,
    SourcePath NVARCHAR(MAX) NULL,
    MetaInfo NVARCHAR(MAX) NULL
);
CREATE TABLE dbo.Tables (
    Id VARCHAR(36) NOT NULL PRIMARY KEY,
    Name NVARCHAR(255) NOT NULL,
    DatabaseId VARCHAR(36) NOT NULL CONSTRAINT FK_Tables_Databases REFERENCES dbo.Databases(Id),
    TableType VARCHAR(20) NOT NULL DEFAULT 'Table',
    Description NVARCHAR(MAX) NULL,
    MetaInfo NVARCHAR(MAX) NULL
);
CREATE TABLE dbo.Columns (
    Id VARCHAR(36) NOT NULL PRIMARY KEY,
    DatabaseId VARCHAR(36) CONSTRAINT FK_Columns_Databases REFERENCES dbo.Databases(Id),
    TableId VARCHAR(36) CONSTRAINT FK_Columns_Tables REFERENCES dbo.Tables(Id),
    Name NVARCHAR(255) NOT NULL,
    DataType NVARCHAR(100) NULL,
    IsNullable BIT NOT NULL,
    IsPrimaryKey BIT NOT NULL DEFAULT 0,
    Position INT NOT NULL DEFAULT 0,
    DefaultValue NVARCHAR(MAX) NULL,
    Description NVARCHAR(MAX) NULL,
    MetaInfo NVARCHAR(MAX) NULL
);
CREATE TABLE dbo.Relations (
    Id VARCHAR(36) NOT NULL PRIMARY KEY,
    FirstId VARCHAR(36) CONSTRAINT FK_Relations_First REFERENCES dbo.Columns(Id),
    SecondId VARCHAR(36) CONSTRAINT FK_Relations_Second REFERENCES dbo.Columns(Id),
    RelationType NVARCHAR(50) NULL,
    Description NVARCHAR(MAX) NULL,
    MetaInfo NVARCHAR(MAX) NULL
);
CREATE INDEX IX_Tables_DatabaseId ON dbo.Tables(DatabaseId);
CREATE INDEX IX_Columns_TableId ON dbo.Columns(TableId);
CREATE INDEX IX_Columns_DatabaseId ON dbo.Columns(DatabaseId);
CREATE INDEX IX_Relations_FirstId ON dbo.Relations(FirstId);
CREATE INDEX IX_Relations_SecondId ON dbo.Relations(SecondId);
CREATE TABLE dbo.Indexes (
    Id VARCHAR(36) NOT NULL PRIMARY KEY,
    DatabaseId VARCHAR(36) NOT NULL CONSTRAINT FK_Indexes_Databases FOREIGN KEY REFERENCES dbo.Databases(Id),
    TableId VARCHAR(36) NOT NULL CONSTRAINT FK_Indexes_Tables FOREIGN KEY REFERENCES dbo.Tables(Id),
    Name NVARCHAR(255) NOT NULL,
    IsUnique BIT NOT NULL DEFAULT 0,
    ColumnNames NVARCHAR(MAX) NOT NULL,
    Description NVARCHAR(MAX) NULL,
    MetaInfo NVARCHAR(MAX) NULL
);
CREATE INDEX IX_Indexes_TableId ON dbo.Indexes(TableId);
CREATE INDEX IX_Indexes_DatabaseId ON dbo.Indexes(DatabaseId);
