using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

/// <summary>
///     Profil léger d'une colonne, calculé à la demande sur la source : des comptes (lignes, nuls, valeurs distinctes), jamais
///     de valeur. Une ligne par colonne profilée ; un nouveau profilage de la table remplace les précédents.
/// </summary>
[Table("ColumnProfiles")]
public class ColumnProfileInfo : InfoBase
{
    public string TableId { get; set; } = "";

    public string ColumnId { get; set; } = "";

    /// <summary>Lignes de la table au moment du profilage.</summary>
    public long RowCount { get; set; }

    public long NullCount { get; set; }

    /// <summary>Valeurs distinctes (hors nuls) ; absent si le type de la colonne ne permet pas de les compter.</summary>
    public long? DistinctCount { get; set; }

    public DateTime ProfiledAt { get; set; }
}
