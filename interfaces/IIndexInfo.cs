namespace GranitDb.Interfaces;

public interface IIndexInfo : IInfoBase
{
    public string DatabaseId { get; set; }

    public string TableId { get; set; }

    public string Name { get; set; }

    public bool IsUnique { get; set; }

    /// <summary>Colonnes de l'index, dans l'ordre, séparées par des virgules.</summary>
    public string ColumnNames { get; set; }
}
