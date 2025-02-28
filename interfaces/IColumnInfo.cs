namespace GranitDb.Interfaces;

public interface IColumnInfo : IInfoBase
{
    public string DatabaseId { get; set; }

    public string TableId { get; set; }

    public string Name { get; set; }

    public string DataType { get; set; }

    public bool IsNullable { get; set; }
}