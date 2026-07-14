using System.Globalization;
using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Software;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

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
    IAgentSoftwareRepository softwareRepo,
    ILogger<ReportAgentSoftwareHandler>? logger = null
) : IRequestHandler<ReportAgentSoftwareCommand, Result<VoidResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<Result<VoidResult>> Handle(ReportAgentSoftwareCommand cmd, CancellationToken ct)
    {
        try
        {
            var collectedAt = cmd.CollectedAt ?? DateTime.UtcNow;
            var entries = ParseSoftwareEntries(cmd.Software);
            await softwareRepo.ReplaceInventoryAsync(cmd.AgentId, collectedAt, entries);
            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao processar inventário de software para AgentId={AgentId}", cmd.AgentId);
            return Result<VoidResult>.Failure(Error.Internal($"Erro ao processar inventário de software: {ex.Message}"));
        }
    }

    private static List<SoftwareInventoryEntry> ParseSoftwareEntries(object? software)
    {
        if (software is null)
            return [];

        var rawItems = ExtractJsonArray(software);
        if (rawItems is null or { ValueKind: not JsonValueKind.Array })
            return [];

        return rawItems.Value.EnumerateArray()
            .Select(MapToEntry)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .ToList();
    }

    private static JsonElement? ExtractJsonArray(object? software)
    {
        if (software is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Array)
                return elem;
            return null;
        }

        var json = JsonSerializer.Serialize(software, JsonOptions);
        if (!json.StartsWith('['))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SoftwareInventoryEntry MapToEntry(JsonElement item)
    {
        return new SoftwareInventoryEntry
        {
            Name         = GetString(item, "name"),
            Version      = GetString(item, "version"),
            Publisher    = GetString(item, "publisher"),
            InstallId    = GetString(item, "installId"),
            Serial       = GetString(item, "serial"),
            Source       = GetString(item, "source"),
            InstallDate  = ParseInstallDate(GetString(item, "installDate")),
            InstallSource = GetString(item, "installSource")
        };
    }

    private static string GetString(JsonElement item, string propertyName)
    {
        if (item.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static DateTime? ParseInstallDate(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var value = rawValue.Trim();

        // Formato compacto do osquery: YYYYMMDD (ex.: "20240401")
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compactDate))
            return DateTime.SpecifyKind(compactDate.Date, DateTimeKind.Utc);

        // Formato ISO 8601 ou qualquer formato parseável
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);

        return null;
    }
}