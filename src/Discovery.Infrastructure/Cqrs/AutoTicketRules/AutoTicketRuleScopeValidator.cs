using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Infrastructure.Cqrs.AutoTicketRules;

/// <summary>
/// Validação do PAR (nível de escopo + id de escopo) das regras de auto-ticket.
///
/// Existe porque create e update repetiam a mesma validação escrita à mão — e as
/// duas cópias divergiram, produzindo dois bugs opostos:
///
///  - CREATE tratava nível inválido como "Global": uma regra de UM cliente virava
///    global e passava a abrir chamado para todos;
///  - UPDATE aceitava Client sem id: a regra ficava INERTE (MatchesScope exige
///    ScopeId.HasValue), ativa na tela e sem nunca disparar.
///
/// Centralizar aqui elimina a possibilidade de as cópias voltarem a divergir.
/// A regra é uma só: <b>nível e id são um par indivisível</b>.
/// </summary>
internal static class AutoTicketRuleScopeValidator
{
    /// <summary>
    /// Resolve e valida o par de escopo.
    ///
    /// <paramref name="requestedLevel"/> nulo mantém <paramref name="currentLevel"/>
    /// (caso do update parcial). <paramref name="requestedScopeId"/> nulo mantém
    /// <paramref name="currentScopeId"/>, para que atualizar só o nome não exija
    /// reenviar o escopo.
    /// </summary>
    internal static Result<(AutoTicketScopeLevel Level, Guid? ScopeId)> Resolve(
        string? requestedLevel,
        Guid? requestedScopeId,
        AutoTicketScopeLevel? currentLevel = null,
        Guid? currentScopeId = null)
    {
        var level = currentLevel ?? AutoTicketScopeLevel.Global;

        if (requestedLevel is not null)
        {
            if (!Enum.TryParse<AutoTicketScopeLevel>(requestedLevel, true, out var parsed))
                return Result<(AutoTicketScopeLevel, Guid?)>.Failure(
                    Error.Validation("scopeLevel", $"Invalid scope level '{requestedLevel}'."));

            level = parsed;
        }

        var scopeId = requestedScopeId ?? currentScopeId;

        // Client/Site SEM id nunca casa em MatchesScope — a regra existiria sem
        // efeito. Falhar é melhor que gravar algo silenciosamente inerte.
        if (level != AutoTicketScopeLevel.Global && (scopeId is null || scopeId == Guid.Empty))
            return Result<(AutoTicketScopeLevel, Guid?)>.Failure(
                Error.Validation("scopeId", $"scopeId is required for scope level '{level}'."));

        return Result<(AutoTicketScopeLevel, Guid?)>.Success((level, scopeId));
    }
}
