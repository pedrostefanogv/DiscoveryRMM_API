using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AutoTicketRules;

/// <summary>
/// Semeia um conjunto CONSERVADOR de regras padrão para um cliente.
///
/// Idempotente: a chave de idempotência é o nome da regra dentro do escopo do
/// cliente — rodar duas vezes atualiza em vez de duplicar.
///
/// O conjunto é deliberadamente pequeno e alinhado a boas práticas: só sensores
/// com falha inequívoca e acionável abrem chamado; sensores ruidosos ficam como
/// AlertOnly (visíveis, mas sem chamado).
/// </summary>
public sealed class SeedDefaultAutoTicketRulesCommandHandler(
    IAutoTicketRuleRepository ruleRepository,
    IAutoTicketRuleEngineService ruleEngine) : IRequestHandler<SeedDefaultAutoTicketRulesCommand, Result<SeedDefaultAutoTicketRulesResponse>>
{
    /// <summary>
    /// (Nometemplate, AlertCode, Ação, Prioridade). Nomes são a chave de idempotência.
    /// </summary>
    private static readonly (string Name, string AlertCode, AutoTicketRuleAction Action, TicketPriority Priority)[] Defaults =
    [
        // Falha inequívoca e acionável -> abre chamado.
        ("AutoTicket: Disco cheio", "sensor:disk_space", AutoTicketRuleAction.CreateTicket, TicketPriority.High),
        ("AutoTicket: Serviço parado", "sensor:service", AutoTicketRuleAction.CreateTicket, TicketPriority.High),

        // Sinais preditivos: o ITIL define que um evento que POSSA degradar o
        // serviço já é incidente (ex.: falha de um disco espelhado).
        ("AutoTicket: Saúde de disco (SMART)", "sensor:smart_status", AutoTicketRuleAction.CreateTicket, TicketPriority.Medium),
        ("AutoTicket: Certificado expirando", "sensor:certificate_expiry", AutoTicketRuleAction.CreateTicket, TicketPriority.Medium),

        // Ruidosos por natureza: visíveis, mas SEM abrir chamado.
        ("AutoTicket: CPU (somente alerta)", "sensor:cpu", AutoTicketRuleAction.AlertOnly, TicketPriority.Low),
        ("AutoTicket: Memória (somente alerta)", "sensor:memory", AutoTicketRuleAction.AlertOnly, TicketPriority.Low),
        ("AutoTicket: Latência DNS (somente alerta)", "sensor:dns_latency", AutoTicketRuleAction.AlertOnly, TicketPriority.Low),

        // Antivírus desatualizado: ruído conhecido em muitas estações.
        ("AutoTicket: Defender (somente alerta)", "sensor:defender_status", AutoTicketRuleAction.AlertOnly, TicketPriority.Low),
    ];

    public async Task<Result<SeedDefaultAutoTicketRulesResponse>> Handle(
        SeedDefaultAutoTicketRulesCommand cmd, CancellationToken ct)
    {
        var request = cmd.Request;
        if (request.ClientId == Guid.Empty)
            return Result<SeedDefaultAutoTicketRulesResponse>.Failure(Error.Validation("clientId", "clientId is required."));

        // Regras existentes do cliente — base da idempotência por nome.
        var existing = await ruleRepository.GetAllAsync(AutoTicketScopeLevel.Client, request.ClientId, null);
        var byName = existing
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var results = new List<SeededAutoTicketRuleResult>();
        var created = 0;
        var updated = 0;

        foreach (var (name, alertCode, action, priority) in Defaults)
        {
            if (byName.TryGetValue(name, out var found))
            {
                found.AlertCodeFilter = alertCode;
                found.Action = action;
                found.TargetPriority = priority;
                found.IsEnabled = request.IsEnabled;
                found.DedupWindowMinutes = request.DedupWindowMinutes;
                found.UpdatedAt = DateTime.UtcNow;
                await ruleRepository.UpdateAsync(found);
                updated++;

                results.Add(new SeededAutoTicketRuleResult
                {
                    RuleId = found.Id,
                    Name = found.Name,
                    Label = alertCode,
                    Status = "updated"
                });
            }
            else
            {
                var rule = new AutoTicketRule
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    IsEnabled = request.IsEnabled,
                    ScopeLevel = AutoTicketScopeLevel.Client,
                    ScopeId = request.ClientId,
                    AlertCodeFilter = alertCode,
                    Action = action,
                    TargetPriority = priority,
                    DedupWindowMinutes = request.DedupWindowMinutes,
                    CooldownMinutes = 0,
                    PriorityOrder = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var createdRule = await ruleRepository.CreateAsync(rule);
                created++;

                results.Add(new SeededAutoTicketRuleResult
                {
                    RuleId = createdRule.Id,
                    Name = createdRule.Name,
                    Label = alertCode,
                    Status = "created"
                });
            }
        }

        return Result<SeedDefaultAutoTicketRulesResponse>.Success(new SeedDefaultAutoTicketRulesResponse
        {
            CreatedCount = created,
            UpdatedCount = updated,
            Rules = results
        });
    }
}
