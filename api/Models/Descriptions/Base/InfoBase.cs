namespace GranitDB.API.Models.Descriptions.Base;

public class InfoBase
{
    public string Id { get; set; } = "";

    public string? Description { get; set; }
    public string? MetaInfo { get; set; }

    public string GetId()
    {
        return Id;
    }
}
