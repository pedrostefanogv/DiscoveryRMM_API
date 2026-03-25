namespace Meduza.Api.Services;

/// <summary>
/// Sinal de reload do serviço de auth callout NATS.
/// Quando acionado, o NatsAuthCalloutBackgroundService reinicia a assinatura do subject
/// sem necessidade de reiniciar a API.
/// </summary>
public interface INatsAuthCalloutReloadSignal
{
    /// <summary>Aciona o reload do serviço de auth callout.</summary>
    void Signal();

    /// <summary>Token cancelado quando um reload é acionado.</summary>
    CancellationToken Token { get; }
}

public sealed class NatsAuthCalloutReloadSignal : INatsAuthCalloutReloadSignal
{
    private CancellationTokenSource _cts = new();
    private readonly object _lock = new();

    public CancellationToken Token
    {
        get { lock (_lock) { return _cts.Token; } }
    }

    public void Signal()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }
}
