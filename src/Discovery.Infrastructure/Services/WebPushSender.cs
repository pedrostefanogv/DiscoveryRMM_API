using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebPush;
using WebPushSubscription = WebPush.PushSubscription;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Envio de Web Push (RFC 8030 + RFC 8291) para todas as inscricoes de um usuario.
///
/// Best-effort: nenhuma falha de provedor interrompe o fluxo que publicou a
/// notificacao. Inscricoes que o provedor reporta como inexistentes (404) ou
/// expiradas (410) sao removidas da base.
///
/// O envio NAO roda no caminho critico de publicacao: ele e consumido por
/// <c>IWebPushDispatchQueue</c>. Aqui os cuidados sao:
///  - HttpClient com timeout curto (o default de 100s bloquearia a fila);
///  - envios em paralelo (N inscricoes custam 1 timeout, nao N);
///  - payload truncado (o provedor recusa acima de ~4096 bytes);
///  - par VAPID validado na inicializacao (chave invalida = servico inativo,
///    em vez de "ativo" que nunca entrega).
///
/// As chaves vem de <see cref="PushOptions"/>. Em Development, quando ausentes,
/// um par transitorio e gerado para permitir o fluxo ponta a ponta — nesse caso
/// as inscricoes caem a cada restart.
/// </summary>
public sealed class WebPushSender : IWebPushSender
{
    /// <summary>Limite do provedor e ~4096 bytes; ficamos com folga para o envelope JSON/cripto.</summary>
    private const int MaxTitleChars = 120;
    private const int MaxTitleBytes = 360;
    private const int MaxBodyChars = 500;
    private const int MaxBodyBytes = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static int _keyWarningLogged;

    private readonly IPushSubscriptionRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PushOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<WebPushSender> _logger;
    private readonly Lazy<VapidKeyPair?> _keys;
    private readonly Lazy<VapidDetails?> _vapidDetails;

    public WebPushSender(
        IPushSubscriptionRepository repository,
        IHttpClientFactory httpClientFactory,
        IOptions<PushOptions> options,
        IHostEnvironment environment,
        ILogger<WebPushSender> logger)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _keys = new Lazy<VapidKeyPair?>(ResolveKeys, LazyThreadSafetyMode.ExecutionAndPublication);
        _vapidDetails = new Lazy<VapidDetails?>(ResolveVapidDetails, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsConfigured => _options.Enabled && _vapidDetails.Value is not null;

    public string PublicKey => _keys.Value?.PublicKey ?? string.Empty;

    public async Task<WebPushDispatchResult> SendToUserAsync(
        Guid userId,
        WebPushMessage message,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty || !_options.Enabled)
            return WebPushDispatchResult.Empty;

        var vapidDetails = _vapidDetails.Value;
        if (vapidDetails is null)
            return WebPushDispatchResult.Empty;

        IReadOnlyList<Discovery.Core.Entities.PushSubscription> subscriptions;
        try
        {
            subscriptions = await _repository.GetByUserIdAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao carregar inscricoes de push do usuario {UserId}", userId);
            return WebPushDispatchResult.Empty;
        }

        if (subscriptions.Count == 0)
            return WebPushDispatchResult.Empty;

        var payload = BuildPayload(message);

        // O timeout e por lote, nao por inscricao: com envio paralelo, N
        // dispositivos custam no maximo BatchTimeoutSeconds.
        using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        batchCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.BatchTimeoutSeconds, 2, 120)));

        var client = new WebPushClient(_httpClientFactory.CreateClient(WebPushHttpClientName));
        var outcomes = await Task.WhenAll(
            subscriptions.Select(subscription => SendOneAsync(client, subscription, payload, vapidDetails, batchCts.Token)));

        var delivered = 0;
        var failed = 0;
        var expired = new List<Guid>();

        for (var index = 0; index < outcomes.Length; index++)
        {
            if (outcomes[index] == PushOutcome.Delivered)
            {
                delivered++;
                continue;
            }

            failed++;
            if (outcomes[index] == PushOutcome.Expired)
                expired.Add(subscriptions[index].Id);
        }

        var removed = 0;
        if (expired.Count > 0)
        {
            try
            {
                removed = await _repository.DeleteByIdsAsync(expired, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao remover inscricoes de push expiradas");
            }
        }

        return new WebPushDispatchResult(subscriptions.Count, delivered, failed, removed);
    }

    /// <summary>Nome do HttpClient configurado em Program.cs com timeout curto.</summary>
    public const string WebPushHttpClientName = "WebPush";

    private async Task<PushOutcome> SendOneAsync(
        WebPushClient client,
        Discovery.Core.Entities.PushSubscription subscription,
        string payload,
        VapidDetails vapidDetails,
        CancellationToken ct)
    {
        try
        {
            await client.SendNotificationAsync(
                new WebPushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth),
                payload,
                vapidDetails,
                ct);
            return PushOutcome.Delivered;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Inscricao morta no provedor: remove para nao tentar de novo.
            _logger.LogInformation(
                "Inscricao de push {SubscriptionId} expirada (HTTP {Status}); removendo.",
                subscription.Id, (int?)ex.StatusCode);
            return PushOutcome.Expired;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("Timeout ao enviar push para a inscricao {SubscriptionId}", subscription.Id);
            return PushOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar push para a inscricao {SubscriptionId}", subscription.Id);
            return PushOutcome.Failed;
        }
    }

    internal static string BuildPayload(WebPushMessage message)
    {
        // O provedor recusa payloads acima do limite (~4096 bytes) e o
        // AppNotification.Message aceita ate 2000 chars — sem truncar, mensagens
        // longas falhariam silenciosamente (HTTP 413).
        return JsonSerializer.Serialize(new
        {
            title = Truncate(message.Title, MaxTitleChars, MaxTitleBytes),
            body = Truncate(message.Body, MaxBodyChars, MaxBodyBytes),
            severity = message.Severity,
            topic = message.Topic,
            eventType = message.EventType,
            notificationId = message.NotificationId,
            url = message.Url
        }, JsonOptions);
    }

    internal static string Truncate(string? value, int maxChars, int maxBytes)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return string.Empty;

        if (text.Length > maxChars)
            text = text[..SafeCut(text, maxChars)].TrimEnd() + "...";

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        // Corta por bytes sem partir uma sequencia UTF-8 ao meio e sem separar
        // pares substitutos (emoji virava caractere invalido).
        while (text.Length > 0 && Encoding.UTF8.GetByteCount(text) > maxBytes - 3)
            text = text[..SafeCut(text, text.Length - 1)];

        return text.TrimEnd() + "...";
    }

    /// <summary>
    /// Indice de corte que nunca separa um par substituto: se o ultimo caractere
    /// mantido for um high surrogate, recua 1 para remover o par inteiro.
    /// </summary>
    private static int SafeCut(string text, int index)
    {
        if (index <= 0)
            return 0;

        if (index >= text.Length)
            return text.Length;

        return char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;
    }

    private VapidKeyPair? ResolveKeys()
    {
        if (!_options.Enabled)
            return null;

        if (!string.IsNullOrWhiteSpace(_options.VapidPublicKey) &&
            !string.IsNullOrWhiteSpace(_options.VapidPrivateKey))
        {
            return new VapidKeyPair(_options.VapidPublicKey.Trim(), _options.VapidPrivateKey.Trim());
        }

        if (_environment.IsDevelopment() && _options.AllowDevelopmentKeyGeneration)
        {
            WarnOnce(
                "Push: chaves VAPID nao configuradas. Gerando um par transitorio de DESENVOLVIMENTO " +
                "(inscricoes serao invalidadas a cada restart). Configure Push:VapidPublicKey/Push:VapidPrivateKey.");
            return VapidKeyGenerator.Generate();
        }

        WarnOnce("Push habilitado, mas sem par VAPID configurado. Web Push permanecera inativo.");
        return null;
    }

    /// <summary>
    /// Valida o par VAPID de uma vez. Chave malformada vira servico INATIVO:
    /// melhor a UI dizer "indisponivel" do que "ativo" sem nunca entregar.
    /// </summary>
    private VapidDetails? ResolveVapidDetails()
    {
        var keys = _keys.Value;
        if (keys is null)
            return null;

        try
        {
            return new VapidDetails(_options.VapidSubject, keys.PublicKey, keys.PrivateKey);
        }
        catch (Exception ex)
        {
            WarnOnce("Push: par VAPID invalido; Web Push permanecera inativo.");
            _logger.LogWarning(ex, "Falha ao validar o par VAPID configurado.");
            return null;
        }
    }

    private void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _keyWarningLogged, 1) != 0)
            return;

        _logger.LogWarning("{Message}", message);
    }

    private enum PushOutcome
    {
        Delivered,
        Expired,
        Failed
    }
}
