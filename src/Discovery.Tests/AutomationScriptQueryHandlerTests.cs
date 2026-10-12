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
/// Regressao do console /automation/scripts. Os handlers de listagem/detalhe
/// precisam projetar o DTO completo (Summary/ScriptType/Version e, no detalhe,
/// Content) — a projeção legada descartava esses campos e quebrava a tela.
///
/// Os metadados declarativos (executionFrequency/triggerModes) e os JSONs de
/// contrato (parametersSchemaJson/metadataJson) foram removidos do contrato: o
/// resumo não pode voltar a expô-los.
/// </summary>
public class AutomationScriptQueryHandlerTests
{
    private static readonly Guid ScriptId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Test]
    public async Task List_DeveExporCamposDoResumo_SemMetadadosRemovidos()
    {
        var handler = new ListAutomationScriptsQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new ListAutomationScriptsQuery(null, null, 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var item = result.Value!.Items.Single();
        Assert.That(item.Summary, Is.EqualTo("Coleta o ID do AnyDesk"));
        Assert.That(item.Version, Is.EqualTo("1.0.0"));
        Assert.That(item.ScriptType, Is.EqualTo(AutomationScriptType.PowerShell));

        // Contrato de fio consumido pelo console (camelCase): as propriedades precisam existir
        // e as removidas não podem reaparecer.
        var json = JsonSerializer.Serialize(
            result.Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.That(json, Does.Contain("\"scriptType\":0"));
        Assert.That(json, Does.Contain("\"summary\":\"Coleta o ID do AnyDesk\""));
        Assert.That(json, Does.Not.Contain("triggerModes"));
        Assert.That(json, Does.Not.Contain("executionFrequency"));
        Assert.That(json, Does.Not.Contain("parametersSchemaJson"));
        Assert.That(json, Does.Not.Contain("metadataJson"));
    }

    [Test]
    public async Task GetById_DeveExporConteudo()
    {
        var service = new FakeAutomationScriptService();
        var handler = new GetAutomationScriptByIdQueryHandler(service);

        var result = await handler.Handle(new GetAutomationScriptByIdQuery(ScriptId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Content, Does.Contain("AnyDesk"));
    }

    [Test]
    public async Task GetById_DeveAceitarScriptInativo()
    {
        var service = new FakeAutomationScriptService();
        var handler = new GetAutomationScriptByIdQueryHandler(service);

        await handler.Handle(new GetAutomationScriptByIdQuery(ScriptId), CancellationToken.None);

        Assert.That(service.LastIncludeInactive, Is.True,
            "o detalhe precisa incluir inativos: o catálogo lista inativos e a edição precisa abri-los");
    }

    [Test]
    public async Task GetById_Desconhecido_DeveRetornarNotFound()
    {
        var handler = new GetAutomationScriptByIdQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    [Test]
    public async Task GetAudit_DeveRetornarEventos()
    {
        var handler = new GetAutomationScriptAuditQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptAuditQuery(ScriptId, 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Has.Count.EqualTo(1));
        Assert.That(result.Value![0].ChangeType, Is.EqualTo("Created"));
        Assert.That(result.Value[0].ScriptId, Is.EqualTo(ScriptId));
    }

    [Test]
    public async Task Consume_DeveRetornarPayloadComHash()
    {
        var handler = new GetAutomationScriptConsumeQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptConsumeQuery(ScriptId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.ScriptId, Is.EqualTo(ScriptId));
        Assert.That(result.Value.Content, Does.Contain("AnyDesk"));
        Assert.That(result.Value.ContentHashSha256, Is.EqualTo("hash"));
    }

    [Test]
    public async Task Consume_Desconhecido_DeveRetornarNotFound()
    {
        var handler = new GetAutomationScriptConsumeQueryHandler(new FakeAutomationScriptService());

        var result = await handler.Handle(new GetAutomationScriptConsumeQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
    }

    private sealed class FakeAutomationScriptService : IAutomationScriptService
    {
        public bool LastIncludeInactive { get; private set; }

        private static AutomationScriptDetailDto Detail() => new()
        {
            Id = ScriptId,
            Name = "Coletar ID do AnyDesk",
            Summary = "Coleta o ID do AnyDesk",
            ScriptType = AutomationScriptType.PowerShell,
            Version = "1.0.0",
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
        {
            LastIncludeInactive = includeInactive;
            return Task.FromResult(id == ScriptId ? Detail() : null);
        }

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
        {
            if (id != ScriptId)
                return Task.FromResult<IReadOnlyList<AutomationScriptAuditDto>>([]);

            IReadOnlyList<AutomationScriptAuditDto> entries =
            [
                new AutomationScriptAuditDto
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    ScriptId = ScriptId,
                    ChangeType = "Created",
                    Reason = "created",
                    NewValueJson = "{}",
                    ChangedAt = DateTime.UtcNow
                }
            ];
            return Task.FromResult(entries);
        }

        public Task<AutomationScriptConsumeDto?> GetConsumePayloadAsync(
            Guid id, string? changedBy, string? ipAddress, string correlationId, CancellationToken cancellationToken = default)
        {
            if (id != ScriptId)
                return Task.FromResult<AutomationScriptConsumeDto?>(null);

            var detail = Detail();
            return Task.FromResult<AutomationScriptConsumeDto?>(new AutomationScriptConsumeDto
            {
                ScriptId = detail.Id,
                Name = detail.Name,
                Version = detail.Version,
                ScriptType = detail.ScriptType,
                Summary = detail.Summary,
                LastUpdatedAt = detail.LastUpdatedAt,
                Content = detail.Content,
                ContentHashSha256 = detail.ContentHashSha256
            });
        }
    }
}
