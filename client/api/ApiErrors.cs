namespace GranitDb.Client.api;

/// <summary>
///     Ce que l'écran dit quand un appel d'API échoue : le message du serveur tel quel, ou « API injoignable ». Un seul endroit,
///     pour que le texte ne diverge pas d'un écran à l'autre.
/// </summary>
public static class ApiErrors
{
    public const string Unreachable = "L'API est injoignable.";

    /// <summary>Le message à montrer pour <see cref="ApiException" /> et <see cref="HttpRequestException" /> ; <c>null</c> pour toute autre exception (elle doit remonter).</summary>
    public static string? Describe(Exception e) => e switch
    {
        ApiException api => api.Message,
        HttpRequestException => Unreachable,
        _ => null
    };

    /// <summary>
    ///     Exécute l'appel ; si le serveur le refuse ou ne répond pas, <paramref name="onError" /> reçoit le message à montrer et le
    ///     résultat est <c>false</c>.
    /// </summary>
    public static async Task<bool> TryAsync(Func<Task> action, Action<string> onError)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception e) when (Describe(e) is not null)
        {
            onError(Describe(e)!);
            return false;
        }
    }
}
