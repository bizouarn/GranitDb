using System.ComponentModel.DataAnnotations.Schema;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Descriptions;

[Table("Relations")]
public class RelationInfo : InfoBase
{
    /// <summary>Marque les relations créées automatiquement par la synchronisation (clés étrangères).</summary>
    public const string AutoMarker = "auto:fk";

    public string FirstId { get; set; } = "";

    public string SecondId { get; set; } = "";

    public string RelationType { get; set; } = ""; // Ex: One-to-One, One-to-Many, Many-to-Many

    /// <summary>
    ///     Paires de colonnes suivantes d'une relation multi-colonnes (clé étrangère composite), dans l'ordre ;
    ///     la première paire est <see cref="FirstId" /> / <see cref="SecondId" />. Vide pour une relation simple.
    ///     Remplie par <c>GetRelationsOfDatabaseAsync</c>, jamais enregistrée avec la relation.
    /// </summary>
    [NotMapped]
    public List<RelationColumnPair> ExtraColumns { get; set; } = [];

    /// <summary>Toutes les paires (référencée, qui référence), la première d'abord.</summary>
    public IEnumerable<(string First, string Second)> Pairs() =>
        new[] { (FirstId, SecondId) }.Concat(ExtraColumns.Select(p => (p.FirstId, p.SecondId)));
}

/// <summary>Une paire de colonnes d'une relation : la colonne référencée et celle qui référence.</summary>
public record RelationColumnPair(string FirstId, string SecondId);

/// <summary>Paire de colonnes supplémentaire (rang 1, 2…) d'une relation multi-colonnes.</summary>
[Table("RelationColumns")]
public class RelationColumnInfo : InfoBase
{
    public string RelationId { get; set; } = "";

    public string FirstId { get; set; } = "";

    public string SecondId { get; set; } = "";

    public int Position { get; set; }
}
