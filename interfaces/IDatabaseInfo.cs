namespace GranitDb.Interfaces;

public interface IDatabaseInfo : IInfoBase
{
    public string Name { get; set; }

    public string Type { get; set; } // Ex: SQL Server, PostgreSQL, MySQL

    public bool IsPublic { get; set; }

    public string ProjectUrl { get; set; }
    public string DatabaseUrl { get; set; }
}