using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Roles.Commands;
using Discovery.Core.Cqrs.UserGroups.Commands;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Behaviors;

/// <summary>
/// Registra auditoria de MUTAÇÕES DE IDENTIDADE de forma centralizada.
///
/// Por que um behavior (e não chamadas em cada handler): a auditoria de 2026-11-07
/// mostrou que a cobertura era ZERO — e o motivo é justamente ter que lembrar de
/// auditar em cada handler. Centralizar garante que um handler novo de identidade
/// entre auditado por construção, ou falhe um teste de contrato.
///
/// <b>Posição no pipeline:</b> registrado ANTES de <c>TransactionBehavior</c>
/// (que é o mais interno). Assim este behavior executa <c>next()</c> — que inclui
/// o commit da transação — e só então grava. Se a mutação reverter, nada é
/// auditado (correto: não aconteceu); se persistir, o rastro existe.
///
/// Eventos de FALHA também são registrados: uma tentativa recusada de conceder
/// acesso fora do escopo é exatamente o sinal que interessa em segurança.
/// </summary>
public sealed class IdentityAuditBehavior<TRequest, TResponse>(
    IIdentityAuditService audit,
    ICurrentActorAccessor currentActor
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
    where TResponse : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var descriptor = IdentityAuditRegistry.Describe(request);
        if (descriptor is null)
            return await next();

        TResponse response;
        try
        {
            response = await next();
        }
        catch (Exception ex)
        {
            // Exceção não tratada: a transação revertida significa que NADA foi
            // persistido, então não há mutação para auditar. Registramos a tentativa
            // falha (valor de segurança) e repropagamos — não engolimos o erro.
            await SafeRecordAsync(descriptor, request, success: false, failureReason: ex.GetType().Name);
            throw;
        }

        // Result<T> traz IsSuccess/Errors; comandos que não sejam Result<T> assumem sucesso.
        var (success, failureReason) = IdentityAuditRegistry.ReadOutcome(response);
        await SafeRecordAsync(descriptor, request, success, failureReason);

        return response;
    }

    private async Task SafeRecordAsync(
        IdentityAuditDescriptor descriptor,
        TRequest request,
        bool success,
        string? failureReason)
    {
        var actorId = currentActor.UserId;
        var actorLogin = currentActor.Login;

        // O ator também pode vir do próprio comando (RequestedByUserId), para o caso
        // de execução fora de request HTTP (jobs) — a sessão tem precedência.
        if (actorId == Guid.Empty)
            actorId = IdentityAuditRegistry.ReadActorFromCommand(request);

        // O serviço já garante que auditoria nunca derruba a operação; o try aqui
        // cobre apenas uma falha improvável de resolução do próprio descritor.
        await audit.RecordAsync(
            descriptor.EventType,
            actorId,
            actorLogin,
            descriptor.TargetType,
            descriptor.ResolveTargetId(request),
            descriptor.ResolveTargetUserId(request),
            before: null,
            after: descriptor.DescribePayload(request),
            success: success,
            failureReason: failureReason);
    }
}
