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

        var baseQuery = db.AiChatMessages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.CreatedAt <= to && m.Session != null)
            .Join(db.AiChatSessions.AsNoTracking(),
                m => m.SessionId, s => s.Id,
                (m, s) => new { m.Role, m.TokensUsed, m.ToolName, s.ClientId })
            .Where(r => q.ClientId == null || r.ClientId == q.ClientId);

        var clientAggs = await baseQuery
            .GroupBy(r => r.ClientId)
            .Select(g => new
            {
                ClientId = g.Key,
                InputTokens = g.Sum(x => x.Role == "user" ? (long)(x.TokensUsed ?? 0) : 0L),
                OutputTokens = g.Sum(x => x.Role == "assistant" ? (long)(x.TokensUsed ?? 0) : 0L),
                Requests = g.Count(x => x.Role == "assistant" && x.TokensUsed != null)
            })
            .ToListAsync(ct);

        long inputTokens = clientAggs.Sum(c => c.InputTokens);
        long outputTokens = clientAggs.Sum(c => c.OutputTokens);
        var requests = clientAggs.Sum(c => c.Requests);
        var costUsd = AiCostEstimator.EstimateUsd(inputTokens, outputTokens);

        var clientIds = clientAggs.Select(c => c.ClientId).ToList();
        var clientNames = await db.Clients.AsNoTracking()
            .Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var budgets = await db.ClientConfigurations.AsNoTracking()
            .Where(c => clientIds.Contains(c.ClientId))
            .Select(c => new { c.ClientId, c.AIIntegrationSettingsJson })
            .ToDictionaryAsync(c => c.ClientId, c => c.AIIntegrationSettingsJson, ct);

        var byClient = clientAggs.Select(c => new AiCostClientDto(
                c.ClientId,
                clientNames.TryGetValue(c.ClientId, out var name) ? name : c.ClientId.ToString(),
                c.Requests,
                c.InputTokens,
                c.OutputTokens,
                AiCostEstimator.EstimateUsd(c.InputTokens, c.OutputTokens),
                ReadBudget(budgets.GetValueOrDefault(c.ClientId))))
            .OrderByDescending(c => c.CostUsd)
            .ToList();

        var topToolsRaw = await baseQuery
            .Where(r => r.Role == "tool" && r.ToolName != null && r.ToolName != "")
            .GroupBy(r => r.ToolName!)
            .Select(g => new { ToolName = g.Key, Calls = g.Count() })
            .OrderByDescending(x => x.Calls)
            .Take(50)
            .ToListAsync(ct);

        var topTools = topToolsRaw
            .Select(t => new AiCostToolDto(
                t.ToolName,
                t.Calls,
                // A execução da tool é local (sem tokens próprios); o custo do
                // LLM permanece nas mensagens assistant que geraram as tool calls.
                0d))
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