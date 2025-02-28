using GranitDb.Interfaces;

namespace GranitDb.Client.Models;

public class DatabaseInfo : IDatabaseInfo
{
    public string Id { get; set; }
    public string Description { get; set; }
    public string MetaInfo { get; set; }
    public string Name { get; set; }
    public string Type { get; set; }
    public bool IsPublic { get; set; }
    public string ProjectUrl { get; set; }
    public string DatabaseUrl { get; set; }
}