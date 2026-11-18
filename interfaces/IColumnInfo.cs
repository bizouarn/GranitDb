namespace GranitDb.Interfaces;

public interface IColumnInfo : IInfoBase
{
    public string DatabaseId { get; set; }

    public string TableId { get; set; }

    public string Name { get; set; }

    public string DataType { get; set; }

    public bool IsNullable { get; set; }

    public bool IsPrimaryKey { get; set; }

    public string? DefaultValue { get; set; }

    /// <summary>Colonne marquée comme contenant des données sensibles (ex : personnelles).</summary>
    public bool IsSensitive { get; set; }

    /// <summary>Position de la colonne dans la table source (0 pour la première).</summary>
    public int Position { get; set; }
}
