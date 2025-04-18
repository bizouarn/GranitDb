﻿using Dapper.Contrib.Extensions;
using GranitDB.API.Models.Descriptions.Base;
using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions;

[Table("Databases")]
public class DatabaseInfo : InfoBase, IDatabaseInfo
{
    public string Name { get; set; }

    public string Type { get; set; } // Ex: SQL Server, PostgreSQL, MySQL

    public bool IsPublic { get; set; }

    public string ProjectUrl { get; set; }
    public string DatabaseUrl { get; set; }
}