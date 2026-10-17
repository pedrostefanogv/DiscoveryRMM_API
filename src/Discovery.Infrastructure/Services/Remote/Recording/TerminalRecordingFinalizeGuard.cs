namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Guarda idempotente de finalização. Só a primeira chamada vence; as demais
/// (exit duplicado, StopRecording concorrente, shutdown) viram no-op.
/// </summary>
public sealed class TerminalRecordingFinalizeGuard
{
    private int _finalized;

    public bool IsFinalized => Volatile.Read(ref _finalized) == 1;

    public bool TryBeginFinalize() => Interlocked.CompareExchange(ref _finalized, 1, 0) == 0;
}
