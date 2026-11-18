using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

/// <summary>Domaine métier : regroupe des tables d'une base pour en faire des diagrammes lisibles.</summary>
[Table("Domains")]
public class DomainInfo : InfoBase
{
    public string DatabaseId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Couleur d'affichage (#rrggbb), facultative.</summary>
    public string? Color { get; set; }

    /// <summary>Le diagramme montre aussi, en pointillés, les tables d'autres domaines reliées à celles du domaine.</summary>
    public bool ShowNeighbors { get; set; }
}

/// <summary>Relation que le diagramme d'un domaine ne dessine pas (le choix est propre au domaine).</summary>
[Table("DomainHiddenRelations")]
public class DomainHiddenRelationInfo : InfoBase
{
    public string DomainId { get; set; } = "";

    public string RelationId { get; set; } = "";
}

/// <summary>Appartenance d'une table à un domaine, avec sa place mémorisée dans le diagramme du domaine.</summary>
[Table("DomainTables")]
public class DomainTableInfo : InfoBase
{
    public string DomainId { get; set; } = "";

    public string TableId { get; set; } = "";

    public double? X { get; set; }

    public double? Y { get; set; }
}

/// <summary>Étiquette libre (ex : « RGPD », « legacy »), commune à toutes les bases.</summary>
[Table("Tags")]
public class TagInfo : InfoBase
{
    public string Name { get; set; } = "";

    public string? Color { get; set; }
}

/// <summary>Pose d'une étiquette sur une base, une table ou une colonne.</summary>
[Table("TagLinks")]
public class TagLinkInfo : InfoBase
{
    public const string Database = "database";
    public const string Table = "table";
    public const string Column = "column";

    public string TagId { get; set; } = "";

    /// <summary>database, table ou column.</summary>
    public string EntityType { get; set; } = "";

    public string EntityId { get; set; } = "";
}
