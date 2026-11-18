namespace GranitDB.API.Options;

/// <summary>Réglages des webhooks (section <c>Webhooks</c>).</summary>
public class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>
    ///     Autorise des URL qui visent une adresse locale ou privée (localhost, 10.x, 192.168.x, host.docker.internal…).
    ///     Refusé par défaut : sans authentification, n'importe quel appelant pourrait sinon faire joindre au serveur
    ///     son réseau interne. À ouvrir seulement sur un réseau de confiance.
    /// </summary>
    public bool AllowPrivateHosts { get; set; }

    /// <summary>Nombre d'essais par envoi (le premier compris).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Attente avant chaque reprise, en millisecondes (la dernière valeur sert pour les suivantes).</summary>
    public int[] RetryDelaysMs { get; set; } = [1000, 5000];

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Envois conservés par webhook (les plus récents).</summary>
    public int DeliveriesKept { get; set; } = 100;
}
