namespace Discovery.Api.Services;

public sealed class RealtimeContractOptions
{
    public const string SectionName = "RealtimeContract";

    /// <summary>
    /// Supported values: auto, transition, strict.
    /// auto: uses HardeningDateUtc cutoff.
    /// </summary>
    public string Mode { get; set; } = "auto";

    public DateTime HardeningDateUtc { get; set; } = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
}