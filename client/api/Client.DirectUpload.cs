using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GranitDb.Client.api;

/// <summary>
///     Envoi des gros fichiers par le navigateur lui-même (<c>granitdb.uploadFile</c>) : le client .NET en WebAssembly garde
///     toute une requête en mémoire, ce qui fait planter l'onglet pour quelques centaines de Mo. Le navigateur, lui, lit le
///     fichier choisi sur le disque au fil de l'envoi.
/// </summary>
public partial class Client
{
    /// <summary>Au-delà de cette taille, le fichier n'est pas chargé dans la mémoire de l'application.</summary>
    public const long DirectUploadThreshold = 50L * 1024 * 1024;

    private record UploadResponse(int Status, string? Body);

    /// <summary>
    ///     Envoie le fichier choisi dans <paramref name="input" /> à la route donnée ; mêmes en-têtes, session et messages
    ///     d'erreur que les autres appels.
    /// </summary>
    public async Task<T> UploadFromBrowserAsync<T>(IJSRuntime js, ElementReference input, HttpMethod method, string route,
        Dictionary<string, string>? fields = null)
    {
        var response = await js.InvokeAsync<UploadResponse>("granitdb.uploadFile", input, new Uri(http.BaseAddress!, route).AbsoluteUri,
            method.Method, fields ?? [], sessionHandler?.SendCredentials == true);
        if (response.Status == 0) throw new HttpRequestException(response.Body);
        if (response.Status == 401) Unauthorized?.Invoke();
        if (response.Status is < 200 or >= 300) throw new ApiException(MessageOf(response.Status, response.Body));
        return JsonSerializer.Deserialize<T>(response.Body ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new ApiException("Réponse vide de l'API.");
    }
}
