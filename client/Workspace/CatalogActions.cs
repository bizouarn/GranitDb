using GranitDb.Client.api;
using GranitDb.Client.Models;
using MudBlazor;

namespace GranitDb.Client.Workspace;

/// <summary>
///     Les actions du catalogue que le panneau de détail et la page d'une base offrent à l'identique (sensibilité d'une colonne,
///     étiquettes). Chaque écran dit seulement comment se mettre à jour : <c>refresh</c> / <c>apply</c>.
/// </summary>
public static class CatalogActions
{
    /// <summary>
    ///     Marque la colonne sensible ou non, avec un message et une annulation ; l'erreur du serveur est une alerte, sauf si
    ///     <paramref name="onError" /> la montre autrement.
    /// </summary>
    public static async Task ToggleSensitiveAsync(GranitDb.Client.api.Client api, Notifier notifier, ColumnInfo column, Func<Task> refresh,
        Action<string>? onError = null)
    {
        var now = !column.IsSensitive;
        try
        {
            await api.SetSensitiveAsync(column.Id, now);
            column.IsSensitive = now;
            await refresh();
            notifier.Show(now ? $"{column.Name} marquée comme sensible." : $"{column.Name} n'est plus marquée comme sensible.",
                Severity.Success, async () =>
                {
                    await api.SetSensitiveAsync(column.Id, !now);
                    column.IsSensitive = !now;
                    await refresh();
                });
        }
        catch (ApiException ex)
        {
            (onError ?? (message => notifier.Error(message)))(ex.Message);
        }
    }

    /// <summary>Pose l'étiquette puis rend les étiquettes de la base à <paramref name="apply" />.</summary>
    public static async Task AddTagAsync(GranitDb.Client.api.Client api, string databaseId, string name, string type, string entityId,
        Func<TagLinkView[], Task> apply)
    {
        await api.AttachTagAsync(name, type, entityId);
        await apply(await api.GetTagLinksAsync(databaseId));
    }

    /// <summary>Retire l'étiquette (avec annulation) puis rend les étiquettes de la base à <paramref name="apply" />.</summary>
    public static async Task RemoveTagAsync(GranitDb.Client.api.Client api, Notifier notifier, string databaseId, TagLinkView tag,
        Func<TagLinkView[], Task> apply)
    {
        await api.DetachTagAsync(tag.TagId, tag.EntityType, tag.EntityId);
        await apply(await api.GetTagLinksAsync(databaseId));
        notifier.Show($"Étiquette {tag.TagName} retirée.", Severity.Success, async () =>
        {
            await api.AttachTagAsync(tag.TagName, tag.EntityType, tag.EntityId);
            await apply(await api.GetTagLinksAsync(databaseId));
        });
    }
}
