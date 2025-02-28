namespace GranitDb.Interfaces;

public interface IInfoBase
{
    public string Id { get; set; }

    public string Description { get; set; }
    public string MetaInfo { get; set; }
}