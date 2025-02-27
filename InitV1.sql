CREATE TABLE Databases (
    Id VARCHAR(36) PRIMARY KEY,
    Name VARCHAR(60),
    Type VARCHAR(50),
    IsPublic bit,
    Description VARCHAR(MAX),
    ProjectUrl VARCHAR(MAX),
    DatabaseUrl VARCHAR(MAX),
    MetaInfo VARCHAR(MAX)
);
CREATE TABLE Columns (
    Id VARCHAR(36) PRIMARY KEY,
    DatabaseId VARCHAR(36),
	TableId VARCHAR(36),
    Name VARCHAR(60),
    DataType VARCHAR(50),
    IsNullable Bit,
    MetaInfo VARCHAR(MAX)
);
CREATE TABLE Relations (
    Id VARCHAR(36) PRIMARY KEY,
    FirstId VARCHAR(36),
    SecondId VARCHAR(36),
    RelationType VARCHAR(50),
    MetaInfo VARCHAR(MAX)
);
CREATE TABLE Tables (
    Id VARCHAR(36) PRIMARY KEY,
    Name VARCHAR(255) NOT NULL,
    DatabaseId VARCHAR(36) NOT NULL,  -- R�f�rence vers la base de donn�es
    ColumnCount INT NOT NULL,
    MetaInfo VARCHAR(MAX),
    FOREIGN KEY (DatabaseId) REFERENCES Databases(Id),  -- Assurez-vous que la table Databases existe
);