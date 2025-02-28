namespace GranitDb.Interfaces;

public interface ITableInfo
{
    public string Name { get; set; }

    public string DatabaseId { get; set; }

    public List<IRelationInfo> Relations { get; }

    public List<IColumnInfo> Columns { get; }
}