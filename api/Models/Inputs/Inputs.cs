using System.ComponentModel.DataAnnotations;
using GranitDB.API.Models.Descriptions;
using GranitDB.API.Models.Descriptions.Base;

namespace GranitDB.API.Models.Inputs;

/// <summary>
///     Corps d'une création (POST) ou d'une modification (PUT) : distinct de l'entité du catalogue, il ne porte que
///     les champs modifiables par un appelant et les valide. Les champs gérés par le serveur (ex : le fichier source
///     d'une base) n'y figurent pas et ne sont jamais écrasés par une modification.
/// </summary>
public abstract class InfoInput<T> where T : InfoBase, new()
{
    /// <summary>Facultatif à la création (généré), s'il est fourni à la modification il doit être celui de l'URL.</summary>
    [StringLength(36)] public string? Id { get; set; }

    [StringLength(4000)] public string? Description { get; set; }

    [StringLength(4000)] public string? MetaInfo { get; set; }

    /// <summary>Nouvelle entité (création).</summary>
    public T ToEntity()
    {
        var entity = new T { Id = Id ?? "" };
        Apply(entity);
        return entity;
    }

    /// <summary>Recopie les champs modifiables sur l'entité existante (modification).</summary>
    public void Apply(T entity)
    {
        entity.Description = Description;
        entity.MetaInfo = ResolveMetaInfo(entity.MetaInfo);
        ApplySpecific(entity);
    }

    /// <summary>MetaInfo à enregistrer, sachant la valeur actuelle (certaines valeurs sont réservées au serveur).</summary>
    protected virtual string? ResolveMetaInfo(string? current)
    {
        return MetaInfo;
    }

    protected abstract void ApplySpecific(T entity);
}

/// <summary>Corps de la modification de la documentation d'un élément : sa description et ses métadonnées.</summary>
public class DocumentationInput
{
    [StringLength(4000)] public string? Description { get; set; }

    [StringLength(4000)] public string? MetaInfo { get; set; }
}

public class DatabaseInput : InfoInput<DatabaseInfo>
{
    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>Ex : Sqlite, SqlServer.</summary>
    [Required, StringLength(50, MinimumLength = 1)]
    public string Type { get; set; } = "";

    public bool IsPublic { get; set; }

    [StringLength(2000), Url] public string? ProjectUrl { get; set; }

    [StringLength(2000), Url] public string? DatabaseUrl { get; set; }

    protected override void ApplySpecific(DatabaseInfo entity)
    {
        entity.Name = Name;
        entity.Type = Type;
        entity.IsPublic = IsPublic;
        entity.ProjectUrl = ProjectUrl;
        entity.DatabaseUrl = DatabaseUrl;
    }
}

public class TableInput : InfoInput<TableInfo>
{
    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required, StringLength(36, MinimumLength = 1)]
    public string DatabaseId { get; set; } = "";

    /// <summary>Table, View ou Virtual.</summary>
    [RegularExpression("^(Table|View|Virtual)$", ErrorMessage = "TableType doit valoir Table, View ou Virtual.")]
    public string TableType { get; set; } = "Table";

    protected override void ApplySpecific(TableInfo entity)
    {
        entity.Name = Name;
        entity.DatabaseId = DatabaseId;
        entity.TableType = TableType;
    }
}

public class ColumnInput : InfoInput<ColumnInfo>
{
    [Required, StringLength(36, MinimumLength = 1)]
    public string DatabaseId { get; set; } = "";

    [Required, StringLength(36, MinimumLength = 1)]
    public string TableId { get; set; } = "";

    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 1)]
    public string DataType { get; set; } = "";

    public bool IsNullable { get; set; }

    public bool IsPrimaryKey { get; set; }

    [StringLength(4000)] public string? DefaultValue { get; set; }

    [Range(0, 100000)] public int Position { get; set; }

    protected override void ApplySpecific(ColumnInfo entity)
    {
        entity.DatabaseId = DatabaseId;
        entity.TableId = TableId;
        entity.Name = Name;
        entity.DataType = DataType;
        entity.IsNullable = IsNullable;
        entity.IsPrimaryKey = IsPrimaryKey;
        entity.DefaultValue = DefaultValue;
        entity.Position = Position;
    }
}

public class RelationInput : InfoInput<RelationInfo>, IValidatableObject
{
    /// <summary>Colonne référencée.</summary>
    [Required, StringLength(36, MinimumLength = 1)]
    public string FirstId { get; set; } = "";

    /// <summary>Colonne qui référence.</summary>
    [Required, StringLength(36, MinimumLength = 1)]
    public string SecondId { get; set; } = "";

    [RegularExpression("^(One-to-One|One-to-Many|Many-to-Many)$",
        ErrorMessage = "RelationType doit valoir One-to-One, One-to-Many ou Many-to-Many.")]
    public string RelationType { get; set; } = "";

    /// <summary>
    ///     Paires suivantes d'une relation multi-colonnes (la première est <c>FirstId</c>/<c>SecondId</c>), dans l'ordre.
    ///     Absent à la modification : inchangé ; liste vide : la relation redevient simple.
    /// </summary>
    public List<RelationColumnPair>? ExtraColumns { get; set; }

    /// <summary>Le marqueur des relations détectées appartient à la synchronisation : on ne le pose ni ne l'enlève.</summary>
    protected override string? ResolveMetaInfo(string? current)
    {
        return current == RelationInfo.AutoMarker ? current : MetaInfo;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (MetaInfo == RelationInfo.AutoMarker)
            yield return new ValidationResult($"MetaInfo ne peut pas valoir {RelationInfo.AutoMarker} (réservé à la synchronisation).",
                [nameof(MetaInfo)]);
        if (FirstId.Length > 0 && FirstId == SecondId)
            yield return new ValidationResult("Une relation relie deux colonnes différentes.", [nameof(SecondId)]);
    }

    protected override void ApplySpecific(RelationInfo entity)
    {
        entity.FirstId = FirstId;
        entity.SecondId = SecondId;
        entity.RelationType = RelationType;
    }
}

public class DomainInput : InfoInput<DomainInfo>
{
    [Required, StringLength(36, MinimumLength = 1)]
    public string DatabaseId { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color doit être de la forme #rrggbb.")]
    public string? Color { get; set; }

    /// <summary>Absent : le réglage actuel du domaine est conservé (un renommage n'efface pas le choix des voisines).</summary>
    public bool? ShowNeighbors { get; set; }

    protected override void ApplySpecific(DomainInfo entity)
    {
        entity.DatabaseId = DatabaseId;
        entity.Name = Name.Trim();
        entity.Color = Color;
        if (ShowNeighbors != null) entity.ShowNeighbors = ShowNeighbors.Value;
    }
}

public class TagInput : InfoInput<TagInfo>
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Color doit être de la forme #rrggbb.")]
    public string? Color { get; set; }

    protected override void ApplySpecific(TagInfo entity)
    {
        entity.Name = Name.Trim();
        entity.Color = Color;
    }
}
