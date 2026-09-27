using System.Text.Json;
using System.Text.Json.Serialization;
using Discovery.Core.DTOs;

namespace Discovery.Tests;

/// <summary>
/// Contrato de serialização dos DTOs da triagem por IA: campos anuláveis saem como
/// null explícito, mesmo com o JsonIgnoreCondition.WhenWritingNull global. O front
/// declara esses campos como `| null` e testar `=== null` quebrava quando a API
/// omitia a propriedade (bug do csatAverage.toFixed).
/// </summary>
public class AiAssignmentDtosSerializationTests
{
    // Mesma política global da API (Program.cs) + camelCase do front.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Test]
    public void TechnicianMetrics_nullableFields_serializeExplicitNull()
    {
        var dto = new TechnicianMetricsDto(
            Guid.NewGuid(), 90, 0, 0, 0, null, null, null, 0, 0, null, 0, null, [], [], null);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.That(json, Does.Contain("\"csatAverage\":null"));
        Assert.That(json, Does.Contain("\"avgFirstResponseMinutes\":null"));
        Assert.That(json, Does.Contain("\"avgResolutionMinutes\":null"));
        Assert.That(json, Does.Contain("\"p90ResolutionMinutes\":null"));
        Assert.That(json, Does.Contain("\"difficultyAverage\":null"));
        Assert.That(json, Does.Contain("\"computedAt\":null"));
    }

    [Test]
    public void DepartmentMemberProfile_nullableFields_serializeExplicitNull()
    {
        var dto = new DepartmentMemberProfileDto(
            Guid.NewGuid(), Guid.NewGuid(), null, false, DateTime.UtcNow, [], 1, null, 1m, false, null);

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.That(json, Does.Contain("\"userName\":null"));
        Assert.That(json, Does.Contain("\"maxOpenTickets\":null"));
        Assert.That(json, Does.Contain("\"metrics\":null"));
    }
}
