using GranitDb.Interfaces;

namespace GranitDB.API.Models.Descriptions.Base;

public class InfoBase : IInfoBase
{
    public string Id { get; set; } = "";

    public string? Description { get; set; }
    public string? MetaInfo { get; set; }

    public string GetId()
    {
        return Id;
    }
}
