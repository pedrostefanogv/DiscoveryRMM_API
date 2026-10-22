using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AiCost;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Estimador de custo de IA em USD. O schema de chat persiste apenas
/// tokens_used (total da resposta); preços de referência gpt-4o-mini:
/// input $0.15/1M, output $0.60/1M. Enquanto não houver persistência do
/// split prompt/completion, todo o uso é atribuído a outputTokens.
/// </summary>
public static class AiCostEstimator
{
    public const decimal InputPricePerMillion = 0.15m;
    public const decimal OutputPricePerMillion = 0.60m;

    public static double EstimateUsd(long inputTokens, long outputTokens)
        => (double)Math.Round(
            inputTokens * InputPricePerMillion / 1_000_000m
            + outputTokens * OutputPricePerMillion / 1_000_000m,
            6, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Agregação do painel de custo de IA (cockpit) + leitor de custo mensal
/// usado pelo enforcement de orçamento do AiCostControlService.
/// </summary>
public class AiCostCockpitQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<GetAiCostCockpitQuery, Result<AiCostCockpitDto>>, IAiMonthlyCostReader
{
    public async Task<Result<AiCostCockpitDto>> Handle(GetAiCostCockpitQuery q, CancellationToken ct)
    {
        var from = q.FromUtc ?? DateTime.UtcNow.AddDays(-30);
        var to = q.ToUtc ?? DateTime.UtcNow;

        var rows = await db.AiChatMessages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.CreatedAt <= to && m.Session != null)
            .Join(db.AiChatSessions.AsNoTracking(),
                m => m.SessionId, s => s.Id,
                (m, s) => new { m.Role, m.TokensUsed, m.ToolName, s.ClientId })
            .Where(r => q.ClientId == null || r.ClientId == q.ClientId)
            .ToListAsync(ct);

        long inputTokens = rows.Where(r => r.Role == "user").Sum(r => (long?)r.TokensUsed ?? 0);
        long outputTokens = rows.Where(r => r.Role == "assistant").Sum(r => (long?)r.TokensUsed ?? 0);
        var requests = rows.Count(r => r.Role == "assistant" && r.TokensUsed is not null);
        var costUsd = AiCostEstimator.EstimateUsd(inputTokens, outputTokens);

        var clientIds = rows.Select(r => r.ClientId).Distinct().ToList();
        var clientNames = await db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var budgets = await db.ClientConfigurations.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId))
            .Select(c => new { c.ClientId, c.AIIntegrationSettingsJson })
            .ToDictionaryAsync(c => c.ClientId, c => c.AIIntegrationSettingsJson, ct);

        var byClient = clientIds.Select(clientId =>
        {
            var own = rows.Where(r => r.ClientId == clientId);
            var inTok = own.Where(r => r.Role == "user").Sum(r => (long?)r.TokensUsed ?? 0);
            var outTok = own.Where(r => r.Role == "assistant").Sum(r => (long?)r.TokensUsed ?? 0);
            return new AiCostClientDto(
                clientId,
                clientNames.TryGetValue(clientId, out var name) ? name : clientId.ToString(),
                own.Count(r => r.Role == "assistant" && r.TokensUsed is not null),
                inTok,
                outTok,
                AiCostEstimator.EstimateUsd(inTok, outTok),
                ReadBudget(budgets.GetValueOrDefault(clientId)));
        }).OrderByDescending(c => c.CostUsd).ToList();

        var topTools = rows
            .Where(r => r.Role == "tool" && !string.IsNullOrEmpty(r.ToolName))
            .GroupBy(r => r.ToolName!)
            .Select(g => new AiCostToolDto(
                g.Key,
                g.Count(),
                // A execução da tool é local (sem tokens próprios); o custo do
                // LLM permanece nas mensagens assistant que geraram as tool calls.
                0d))
            .OrderByDescending(t => t.Calls)
            .ToList();

        return Result<AiCostCockpitDto>.Success(new AiCostCockpitDto(
            from, to,
            new AiCostCockpitTotalsDto(requests, inputTokens, outputTokens, costUsd),
            byClient, topTools));
    }

    public async Task<double?> GetMonthToDateCostUsdAsync(Guid clientId, CancellationToken ct = default)
    {
        try
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var tokens = await db.AiChatMessages.AsNoTracking()
                .Where(m => m.CreatedAt >= monthStart && m.Role == "assistant" && m.TokensUsed != null)
                .Join(db.AiChatSessions.AsNoTracking(),
                    m => m.SessionId, s => s.Id,
                    (m, s) => new { m.TokensUsed, s.ClientId })
                .Where(r => r.ClientId == clientId)
                .SumAsync(r => (long?)r.TokensUsed ?? 0, ct);
            return AiCostEstimator.EstimateUsd(0, tokens);
        }
        catch
        {
            // Fail-open: sem leitura de custo, o enforcement de orçamento não bloqueia.
            return null;
        }
    }

    /// <summary>Lê budgetUsdMonthly do override JSON do cliente (fail-open: null em qualquer falha).</summary>
    internal static double? ReadBudget(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return null;

        try
        {
            // Case-insensitive: o console grava o JSON em camelCase.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var overrideSettings = JsonSerializer.Deserialize<AIIntegrationSettingsOverride>(settingsJson, options);
            return overrideSettings?.BudgetUsdMonthly;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}