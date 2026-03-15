using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IMeshCentralEmbeddingService
{
    Task<MeshCentralEmbedUrlResult> GenerateAgentEmbedUrlAsync(
        Agent agent,
        Guid clientId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default);

    Task<MeshCentralEmbedUrlResult> GenerateUserEmbedUrlAsync(
        string meshUsername,
        Guid clientId,
        Guid siteId,
        Guid? agentId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default);
}

public sealed class MeshCentralEmbedUrlResult
{
    public required string Url { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public required int ViewMode { get; init; }
    public required int HideMask { get; init; }
}
