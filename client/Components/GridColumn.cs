namespace GranitDb.Client.Components;

public enum GridKind
{
    Text,
    Check,
    Choice
}

/// <summary>Une colonne de <see cref="EditableGrid{TRow}" /> : comment lire et écrire la cellule d'une ligne, et la valider.</summary>
public class GridColumn<TRow> where TRow : class
{
    public GridColumn(string header, Func<TRow, string?> get, Action<TRow, string?> set, GridKind kind = GridKind.Text)
    {
        Header = header;
        Get = get;
        Set = set;
        Kind = kind;
    }

    public string Header { get; }

    public GridKind Kind { get; }

    /// <summary>Valeur de la cellule ; pour une case à cocher, « true » ou « false ».</summary>
    public Func<TRow, string?> Get { get; }

    public Action<TRow, string?> Set { get; }

    /// <summary>Valeurs proposées (colonne de type liste).</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Message d'erreur de la cellule, ou null ; reçoit la ligne et toutes les lignes (doublons…). Appelé pour les lignes non vides.</summary>
    public Func<TRow, IReadOnlyList<TRow>, string?>? Validate { get; init; }

    /// <summary>La cellule n'est pas modifiable pour cette ligne (ex : colonne venue de la source).</summary>
    public Func<TRow, bool>? ReadOnly { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>Largeur de la colonne (CSS, ex : « 12rem »).</summary>
    public string? Width { get; init; }
}

/// <summary>Un problème de saisie : la ligne (à partir de 1), la colonne et ce qui ne va pas.</summary>
public record GridProblem(int Row, string Column, string Message);

public static class GridValidation
{
    /// <summary>Les problèmes de toutes les lignes non vides, dans l'ordre de lecture.</summary>
    public static List<GridProblem> Problems<TRow>(IReadOnlyList<GridColumn<TRow>> columns, IReadOnlyList<TRow> rows,
        Func<TRow, bool> isBlank) where TRow : class
    {
        var result = new List<GridProblem>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (isBlank(rows[i])) continue;
            foreach (var column in columns)
                if (column.Validate?.Invoke(rows[i], rows) is { } message)
                    result.Add(new GridProblem(i + 1, column.Header, message));
        }

        return result;
    }

    /// <summary>Lit un texte collé depuis un tableur : une ligne par ligne, cellules séparées par une tabulation (ou « ; »).</summary>
    public static List<string[]> ParsePaste(string? text)
    {
        var lines = new List<string[]>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var cells = raw.Contains('\t') ? raw.Split('\t') : raw.Split(';');
            lines.Add(cells.Select(c => c.Trim()).ToArray());
        }

        return lines;
    }

    /// <summary>Interprète une cellule collée comme un oui / non.</summary>
    public static bool IsYes(string? value) =>
        value?.Trim().ToLowerInvariant() is "x" or "1" or "oui" or "true" or "vrai" or "yes" or "o";
}
