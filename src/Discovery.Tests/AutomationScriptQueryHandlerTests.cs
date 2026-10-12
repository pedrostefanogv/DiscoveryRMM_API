using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AutomationScripts;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressao do console /automation/scripts. O TypeError
/// "can't access property 'join', e.triggerModes is undefined" acontecia porque os
/// handlers de listagem/detalhe projetavam o DTO legado AutomationScriptDto
/// (Id/Name/Description/IsActive/datas) e descartavam triggerModes, summary,
/// scriptType, version, executionFrequency e content.
/// </summary>
public class AutomationScriptQueryHandlerTests
{
    private static readonly Guid ScriptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Test]
    public async Task List_DeveExporTriggerModes_E_CamposDoResumo()
    {
        var handler = new ListAutomationScriptsQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new ListAutomationScriptsQuery(null, null, 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var item = result.Value!.Items.Single();
        Assert.That(item.TriggerModes, Is.EqualTo(new[] { "manual" }));
        Assert.That(item.Summary, Is.EqualTo("Coleta o ID do AnyDesk"));
        Assert.That(item.Version, Is.EqualTo("1.0.0"));
        Assert.That(item.ExecutionFrequency, Is.EqualTo("manual"));
        Assert.That(item.ScriptType, Is.EqualTo(AutomationScriptType.PowerShell));

        // Contrato de fio consumido pelo console (camelCase): as propriedades precisam existir.
        var json = JsonSerializer.Serialize(
            result.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.That(json, Does.Contain("\"triggerModes\":[\"manual\"]"));
        Assert.That(json, Does.Contain("\"scriptType\":0"));
        Assert.That(json, Does.Contain("\"summary\":\"Coleta o ID do AnyDesk\""));
    }

    [Test]
    public async Task GetById_DeveExporConteudo_E_TriggerModes()
    {
        var handler = new GetAutomationScriptByIdQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptByIdQuery(ScriptId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Content, Does.Contain("AnyDesk"));
        Assert.That(result.Value.TriggerModes, Is.EqualTo(new[] { "manual" }));
    }

    [Test]
    public async Task GetById_Desconhecido_DeveRetornarNotFound()
    {
        var handler = new GetAutomationScriptByIdQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    private sealed class FakeAutomationScriptService : IAutomationScriptService
    {
        private static AutomationScriptDetailDto Detail() => new()
        {
            Id = ScriptId,
            Name = "Coletar ID do AnyDesk",
            Summary = "Coleta o ID do AnyDesk",
            ScriptType = AutomationScriptType.PowerShell,
            Version = "1.0.0",
            ExecutionFrequency = "manual",
            TriggerModes = ["manual"],
            IsActive = true,
            LastUpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            Content = "Write-Output 'AnyDesk ID'",
            ContentHashSha256 = "hash",
            UpdatedAt = DateTime.UtcNow
        };

        public Task<CursorPageDto<AutomationScriptSummaryDto>> GetListPageAsync(
            Guid? clientId, bool activeOnly, string? cursor, int limit, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AutomationScriptSummaryDto> items = [Detail()];
            return Task.FromResult(new CursorPageDto<AutomationScriptSummaryDto>(items, items.Count, null, null, false, limit));
        }

        public Task<AutomationScriptDetailDto?> GetByIdAsync(
            Guid id, bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult(id == ScriptId ? Detail() : null);

        public Task<AutomationScriptDetailDto> CreateAsync(
            CreateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationScriptDetailDto?> UpdateAsync(
            Guid id, UpdateAutomationScriptRequest request, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid id, string? changedBy, string? ipAddress, string correlationId, string? reason, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AutomationScriptAuditDto>> GetAuditAsync(
            Guid id, int limit = 100, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(
            Guid id, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
