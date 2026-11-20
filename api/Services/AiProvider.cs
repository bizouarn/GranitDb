using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GranitDB.API.Exceptions;
using GranitDB.API.Options;
using Microsoft.Extensions.Options;

namespace GranitDB.API.Services;

/// <summary>
///     Une demande au modèle. Avec <paramref name="Schema" /> (un schéma JSON), la réponse est contrainte puis validée : c'est ce
///     qu'il faut avec un petit modèle, qui dérive vite hors format. <paramref name="Validate" /> rend un message si le contenu
///     JSON ne convient pas (champ manquant…), ce qui déclenche l'unique réessai.
/// </summary>
public record AiRequest(string System, string User, string? Schema = null, Func<JsonElement, string?>? Validate = null, int? MaxTokens = null);

/// <summary>La réponse : le texte, le JSON analysé si un schéma était demandé, et le coût de l'appel (jamais le contenu dans les journaux).</summary>
public record AiResponse(string Content, JsonElement? Json, string Model, int PromptTokens, int CompletionTokens, long ElapsedMs);

/// <summary>État du modèle tel que l'API le montre : pas de clé, pas de chemin, seulement l'origine du serveur.</summary>
public record AiStatusView(bool Enabled, string Model, string Endpoint, bool Reachable, bool ModelAvailable, string? Message);

/// <summary>Résultat de « Tester le modèle ».</summary>
public record AiTestView(string Model, string Reply, bool ValidJson, long ElapsedMs, int PromptTokens, int CompletionTokens);

/// <summary>Génération de texte structurée par un modèle d'IA, derrière un seul protocole (compatible OpenAI).</summary>
public interface IAiProvider
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default);

    Task<AiStatusView> StatusAsync(CancellationToken ct = default);
}

/// <summary>
///     Fournisseur pour tout serveur parlant l'API <c>/chat/completions</c> d'OpenAI (Ollama, llama-server, LM Studio, vLLM…).
///     Garde-fous : désactivé par défaut (<c>Catalog:AllowAi</c>), point d'accès distant refusé s'il n'est pas en https (sauf
///     <c>Ai:AllowRemote</c>), taille de requête et appels simultanés bornés, annulation, un seul réessai sur un JSON invalide,
///     et dans les journaux seulement le modèle, la durée et le nombre de jetons.
/// </summary>
public partial class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    private readonly IOptions<CatalogOptions> _catalog;
    private readonly IOptions<AiOptions> _ai;
    private readonly ILogger<OpenAiCompatibleProvider> _log;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate;

    public OpenAiCompatibleProvider(IOptions<CatalogOptions> catalog, IOptions<AiOptions> ai, ILogger<OpenAiCompatibleProvider> log,
        HttpMessageHandler? handler = null)
    {
        _catalog = catalog;
        _ai = ai;
        _log = log;
        _http = handler == null ? new HttpClient() : new HttpClient(handler, false);
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // bornée par appel, avec l'annulation de l'appelant
        _gate = new SemaphoreSlim(Math.Max(1, ai.Value.MaxConcurrent));
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private AiOptions Options => _ai.Value;

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex Thinking();

    private void EnsureAllowed()
    {
        if (!_catalog.Value.AllowAi)
            throw new GranitExceptions("L'assistant d'IA est désactivé (Catalog:AllowAi = false) : il envoie des noms et descriptions du catalogue à un modèle.");
        Endpoint();
    }

    /// <summary>L'adresse de base validée : http(s) uniquement, et en clair seulement vers cette machine (ou avec <c>Ai:AllowRemote</c>).</summary>
    public Uri Endpoint()
    {
        if (!Uri.TryCreate(Options.Endpoint?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new GranitExceptions("Ai:Endpoint doit être une adresse http(s) absolue (celle d'un serveur compatible OpenAI, avec son /v1).");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
            throw new GranitExceptions("Ai:Endpoint ne doit contenir ni identifiants ni paramètres : la clé va dans Ai:ApiKey.");
        var local = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!local && uri.Scheme != "https" && !Options.AllowRemote)
            throw new GranitExceptions($"Ai:Endpoint ({uri.Host}) n'est ni cette machine ni en https : les noms du catalogue partiraient en clair. Utilisez https ou Ai:AllowRemote = true pour un réseau de confiance.");
        return uri;
    }

    private HttpRequestMessage Build(HttpMethod method, string path, string? json)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint(), path));
        if (!string.IsNullOrWhiteSpace(Options.ApiKey))
        {
            string key;
            try
            {
                key = SecretReference.Resolve(Options.ApiKey);
            }
            catch (InvalidOperationException e)
            {
                throw new GranitExceptions($"Ai:ApiKey : {e.Message}");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Options.TimeoutSeconds)));
        return source;
    }

    public async Task<AiStatusView> StatusAsync(CancellationToken ct = default)
    {
        var enabled = _catalog.Value.AllowAi;
        var origin = Uri.TryCreate(Options.Endpoint, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "";
        if (!enabled) return new AiStatusView(false, Options.Model, origin, false, false, "Désactivé (Catalog:AllowAi = false).");
        try
        {
            Endpoint();
            using var timeout = Timeout(ct);
            using var response = await _http.SendAsync(Build(HttpMethod.Get, "models", null), timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new AiStatusView(true, Options.Model, origin, false, false, $"Le serveur répond {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var ids = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList()
                : [];
            // Ollama nomme « qwen3:4b-instruct » ce que l'on peut écrire « qwen3 » : on accepte l'étiquette « :latest » implicite.
            var available = ids.Any(id => id.Equals(Options.Model, StringComparison.OrdinalIgnoreCase) ||
                                          id.Equals(Options.Model + ":latest", StringComparison.OrdinalIgnoreCase));
            return new AiStatusView(true, Options.Model, origin, true, available,
                available ? null : $"Le modèle {Options.Model} n'est pas (encore) sur le serveur : téléchargement en cours, ou à installer (ollama pull {Options.Model}).");
        }
        catch (GranitExceptions e)
        {
            return new AiStatusView(true, Options.Model, origin, false, false, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return new AiStatusView(true, Options.Model, origin, false, false, "Le serveur d'IA ne répond pas.");
        }
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        EnsureAllowed();
        var size = request.System.Length + request.User.Length + (request.Schema?.Length ?? 0);
        if (size > Options.MaxRequestChars)
            throw new GranitExceptions($"La requête à l'IA est trop longue ({size:N0} caractères, au plus {Options.MaxRequestChars:N0} : Ai:MaxRequestChars). Un petit modèle demande un contexte court.");

        // Un petit serveur ne tient pas la charge : les appels font la queue, dans la limite de leur propre délai.
        using var wait = Timeout(ct);
        try
        {
            await _gate.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GranitExceptions("Le modèle est occupé : réessayez dans un instant.");
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var (content, prompt, completion) = await SendAsync(request, null, ct);
            if (request.Schema == null)
                return Done(new AiResponse(content, null, Options.Model, prompt, completion, stopwatch.ElapsedMilliseconds));

            var error = Check(content, request, out var json);
            if (error != null)
            {
                // Un seul réessai, en disant au modèle ce qui n'allait pas.
                var (again, p2, c2) = await SendAsync(request, (content, error), ct);
                prompt += p2;
                completion += c2;
                content = again;
                error = Check(content, request, out json);
                if (error != null)
                    throw new GranitExceptions($"Le modèle {Options.Model} n'a pas répondu dans le format attendu ({error}). Un modèle plus gros ou une requête plus courte aide.");
            }

            return Done(new AiResponse(content, json, Options.Model, prompt, completion, stopwatch.ElapsedMilliseconds));
        }
        finally
        {
            _gate.Release();
        }
    }

    private AiResponse Done(AiResponse response)
    {
        // Ni les instructions ni la réponse : seulement de quoi surveiller le coût.
        _log.LogInformation("Appel IA : modèle {Model}, {Prompt} + {Completion} jetons, {Elapsed} ms", response.Model,
            response.PromptTokens, response.CompletionTokens, response.ElapsedMs);
        return response;
    }

    /// <summary>Vérifie que le contenu est du JSON (objet) et que l'appelant l'accepte ; rend le motif du refus sinon.</summary>
    private static string? Check(string content, AiRequest request, out JsonElement? json)
    {
        json = null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "ce n'est pas un objet JSON";
            var element = doc.RootElement.Clone();
            var invalid = request.Validate?.Invoke(element);
            if (invalid != null) return invalid;
            json = element;
            return null;
        }
        catch (JsonException)
        {
            return "ce n'est pas du JSON valide";
        }
    }

    private async Task<(string Content, int Prompt, int Completion)> SendAsync(AiRequest request, (string Bad, string Error)? retry,
        CancellationToken ct)
    {
        var messages = new List<object>
        {
            new { role = "system", content = $"{request.System}\nRéponds en {Options.Language}." + (request.Schema != null ? "\nRéponds uniquement par un objet JSON conforme au schéma." : "") },
            new { role = "user", content = request.User }
        };
        if (retry is { } r)
        {
            messages.Add(new { role = "assistant", content = r.Bad });
            messages.Add(new { role = "user", content = $"Ta réponse n'est pas acceptable : {r.Error}. Recommence en répondant uniquement par l'objet JSON demandé." });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = Options.Model,
            ["messages"] = messages,
            ["temperature"] = 0.2,
            ["max_tokens"] = Math.Min(request.MaxTokens ?? Options.MaxOutputTokens, Options.MaxOutputTokens),
            ["stream"] = false
        };
        if (Options.DisableThinking) body["reasoning_effort"] = "none";
        if (request.Schema != null)
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "reponse", strict = true, schema = JsonDocument.Parse(request.Schema).RootElement }
            };

        using var timeout = Timeout(ct);
        try
        {
            using var response = await _http.SendAsync(Build(HttpMethod.Post, "chat/completions", JsonSerializer.Serialize(body)), timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new GranitExceptions($"Le modèle {Options.Model} n'est pas disponible sur le serveur d'IA (téléchargement en cours, ou à installer).");
            if (!response.IsSuccessStatusCode)
                throw new GranitExceptions($"Le serveur d'IA répond {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(text);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            content = Thinking().Replace(content, "").Trim();
            int Usage(string name) => doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var v) ? v : 0;
            return (content, Usage("prompt_tokens"), Usage("completion_tokens"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GranitExceptions($"Le modèle n'a pas répondu en {Options.TimeoutSeconds} s (Ai:TimeoutSeconds).");
        }
        catch (HttpRequestException)
        {
            throw new GranitExceptions("Le serveur d'IA est injoignable : vérifiez Ai:Endpoint et que le serveur tourne.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new GranitExceptions("Le serveur d'IA a répondu dans un format inattendu (est-il compatible OpenAI ?).");
        }
    }
}
