namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Agregador em memória dos metadados de uma gravação de terminal. É a fonte
/// dos contadores persistidos periodicamente (frames/bytes/dropped/max_fps).
/// </summary>
public sealed class TerminalRecordingMetrics
{
    private DateTime _windowStartUtc = DateTime.MinValue;
    private int _windowFrames;

    public long Frames { get; private set; }
    public long Bytes { get; private set; }
    public long DroppedFrames { get; private set; }
    public int MaxFps { get; private set; }
    public DateTime? LastFrameAtUtc { get; private set; }

    /// <summary>Retoma os contadores de uma gravação existente no banco.</summary>
    public void Restore(long frames, long bytes, long droppedFrames, int maxFps)
    {
        Frames = Math.Max(0, frames);
        Bytes = Math.Max(0, bytes);
        DroppedFrames = Math.Max(0, droppedFrames);
        MaxFps = Math.Max(0, maxFps);
    }

    public void RecordFrame(long bytes, DateTime utcNow)
    {
        if (bytes > 0)
            Bytes += bytes;

        Frames++;
        LastFrameAtUtc = utcNow;

        if (_windowStartUtc == DateTime.MinValue || (utcNow - _windowStartUtc) >= TimeSpan.FromSeconds(1))
        {
            _windowStartUtc = utcNow;
            _windowFrames = 0;
        }

        _windowFrames++;
        if (_windowFrames > MaxFps)
            MaxFps = _windowFrames;
    }

    public void RecordDroppedFrame() => RecordDroppedFrames(1);

    public void RecordDroppedFrames(long count)
    {
        if (count > 0)
            DroppedFrames += count;
    }
}
