namespace GranitDb.Interfaces;

public interface ITableInfo
{
    public string Name { get; set; }

    public string DatabaseId { get; set; }

    public string TableType { get; set; } // Table, View, Virtual

    public IReadOnlyList<IRelationInfo> Relations { get; }

    public IReadOnlyList<IColumnInfo> Columns { get; }

    public IReadOnlyList<IIndexInfo> Indexes { get; }
}
