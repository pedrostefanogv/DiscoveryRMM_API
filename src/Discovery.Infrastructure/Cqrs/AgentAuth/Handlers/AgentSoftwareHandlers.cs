using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Software;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentSoftwareHandler(
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<GetAgentSoftwareQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentSoftwareQuery q, CancellationToken ct)
    {
        var items = await softwareRepo.GetCurrentByAgentIdAsync(q.AgentId);
        return Result<object>.Success(items);
    }
}

public sealed class ReportAgentSoftwareHandler(
    IAgentSoftwareRepository softwareRepo
) : IRequestHandler<ReportAgentSoftwareCommand, Result<VoidResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<Result<VoidResult>> Handle(ReportAgentSoftwareCommand cmd, CancellationToken ct)
    {
        var collectedAt = cmd.CollectedAt ?? DateTime.UtcNow;

        var entries = ParseSoftwareEntries(cmd.Software);
        await softwareRepo.ReplaceInventoryAsync(cmd.AgentId, collectedAt, entries);

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static List<SoftwareInventoryEntry> ParseSoftwareEntries(object? software)
    {
        if (software is null)
            return [];

        try
        {
            if (software is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<SoftwareInventoryEntry>>(elem.GetRawText(), JsonOptions) ?? [];
            }

            var json = JsonSerializer.Serialize(software, JsonOptions);
            if (json.StartsWith("["))
            {
                return JsonSerializer.Deserialize<List<SoftwareInventoryEntry>>(json, JsonOptions) ?? [];
            }
        }
        catch { /* invalid payload, return empty */ }

        return [];
    }
}