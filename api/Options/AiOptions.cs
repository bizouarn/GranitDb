namespace GranitDB.API.Options;

/// <summary>
///     Réglages du modèle d'IA (section <c>Ai</c>), utilisés seulement quand <c>Catalog:AllowAi</c> est vrai. Un seul protocole :
///     l'API compatible OpenAI (<c>/chat/completions</c>), que parlent Ollama, llama-server (llama.cpp), LM Studio, vLLM et la plupart
///     des services hébergés. Le modèle par défaut est léger, pour tourner sur la machine ou le serveur de l'application.
/// </summary>
public class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Modèle conseillé par défaut : un « instruct » de 4 milliards de paramètres, quantifié (≈ 2,5 Go, tourne sur CPU).</summary>
    public const string DefaultModel = "qwen3:4b-instruct";

    /// <summary>Adresse de base de l'API compatible OpenAI (celle d'Ollama par défaut, avec son <c>/v1</c>).</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";

    public string Model { get; set; } = DefaultModel;

    /// <summary>Clé d'API, facultative pour un serveur local ; <c>env:NOM</c> ou <c>file:/chemin</c> évitent de l'écrire dans la configuration.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Durée maximale d'un appel, en secondes (un petit modèle sur CPU met de quelques secondes à plusieurs dizaines).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Appels simultanés au plus : 1 par défaut, un petit serveur ne tient pas la charge. Les autres attendent leur tour.</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>
    ///     Accepte un serveur qui n'est ni cette machine ni en https (par exemple le service <c>ollama</c> d'un réseau Docker). Sans
    ///     cela, un point d'accès distant en clair est refusé : les noms des tables partiraient sans protection.
    /// </summary>
    public bool AllowRemote { get; set; }

    /// <summary>Taille maximale d'une requête (instructions et contexte), en caractères : un prompt court convient à un petit modèle.</summary>
    public int MaxRequestChars { get; set; } = 12000;

    /// <summary>Jetons de réponse au plus.</summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>
    ///     Demande aux modèles « qui réfléchissent » (Qwen3…) de répondre directement (<c>reasoning_effort: none</c>) : leur bloc de réflexion
    ///     multiplie le temps de réponse par plusieurs fois sur CPU pour un gain nul sur une description d'une phrase.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    /// <summary>Langue des réponses.</summary>
    public string Language { get; set; } = "français";
}
