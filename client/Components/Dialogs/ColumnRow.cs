namespace GranitDb.Client.Components.Dialogs;

/// <summary>Une ligne de la grille des colonnes (table saisie à la main, ou colonnes d'une table à compléter).</summary>
public class ColumnRow
{
    /// <summary>Identifiant de la colonne du catalogue ; null pour une nouvelle ligne.</summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "";

    public string DataType { get; set; } = "text";

    public bool IsPrimaryKey { get; set; }

    public bool IsNullable { get; set; } = true;

    public bool IsSensitive { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Métadonnées de la colonne du catalogue, renvoyées telles quelles à l'enregistrement de la description.</summary>
    public string? MetaInfo { get; set; }

    /// <summary>Description de départ (pour ne mettre à jour que ce qui a changé).</summary>
    public string OriginalDescription { get; set; } = "";

    /// <summary>La colonne vient de la source : son nom et son type ne se modifient pas ici.</summary>
    public bool FromSource { get; set; }

    public static bool IsBlank(ColumnRow row) =>
        row.Id == null && string.IsNullOrWhiteSpace(row.Name) && string.IsNullOrWhiteSpace(row.Description) && !row.IsPrimaryKey && !row.IsSensitive;

    /// <summary>Les colonnes de la grille ; nom, type et clés des colonnes venues de la source sont verrouillés.</summary>
    public static IReadOnlyList<GridColumn<ColumnRow>> Grid() =>
    [
        new GridColumn<ColumnRow>("Nom", r => r.Name, (r, v) => r.Name = v ?? "")
        {
            Placeholder = "nom_de_colonne", Width = "14rem", ReadOnly = r => r.FromSource,
            Validate = (r, rows) =>
            {
                if (string.IsNullOrWhiteSpace(r.Name)) return "Donnez un nom à la colonne.";
                return rows.Count(o => !IsBlank(o) && string.Equals(o.Name.Trim(), r.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1
                    ? "Ce nom est saisi deux fois."
                    : null;
            }
        },
        new GridColumn<ColumnRow>("Type", r => r.DataType, (r, v) => r.DataType = v ?? "")
        {
            Placeholder = "text, integer…", Width = "9rem", ReadOnly = r => r.FromSource,
            Validate = (r, _) => string.IsNullOrWhiteSpace(r.DataType) ? "Indiquez le type." : null
        },
        new GridColumn<ColumnRow>("Clé", r => r.IsPrimaryKey ? "true" : "false", (r, v) => r.IsPrimaryKey = v == "true", GridKind.Check)
            { Width = "3.5rem", ReadOnly = r => r.FromSource },
        new GridColumn<ColumnRow>("Nul permis", r => r.IsNullable ? "true" : "false", (r, v) => r.IsNullable = v == "true", GridKind.Check)
            { Width = "5rem", ReadOnly = r => r.FromSource },
        new GridColumn<ColumnRow>("Sensible", r => r.IsSensitive ? "true" : "false", (r, v) => r.IsSensitive = v == "true", GridKind.Check)
            { Width = "4.5rem", ReadOnly = r => r.FromSource },
        new GridColumn<ColumnRow>("Description", r => r.Description, (r, v) => r.Description = v ?? "") { Placeholder = "À quoi sert cette colonne ?" }
    ];
}
