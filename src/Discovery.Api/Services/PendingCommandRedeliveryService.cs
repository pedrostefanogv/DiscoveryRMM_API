using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

/// <summary>
/// Reentrega comandos que o agente ainda não confirmou.
///
/// Motivo: o dispatch por agente publica em NATS core (fire-and-forget). Um
/// agente offline não recebe a mensagem e ela é perdida — o comando ficava
/// "Pending/Sent" eternamente e nada o reexecutava. Aqui os comandos não
/// confirmados (Pending/Sent/Running) são reenviados enquanto o agente estiver
/// online, respeitando uma janela de retenção.
///
/// Segurança contra reexecução: o agente deduplica por CommandId (TTL de 24h),
/// então reenviar não executa o comando duas vezes — apenas garante a entrega.
/// </summary>
public class PendingCommandRedeliveryService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NatsCommandRedeliveryOptions> optionsMonitor,
    ILogger<PendingCommandRedeliveryService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PendingCommandRedeliveryService iniciado.");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            var window = options.ResolveWindow(DateTime.UtcNow);

            try
            {
                if (options.Enabled)
                    await ProcessAsync(window, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na reentrega de comandos pendentes.");
            }

            try
            {
                await Task.Delay(window.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("PendingCommandRedeliveryService encerrado.");
    }

    /// <summary>
    /// Agentes elegíveis a receber reentrega: online e fora da lixeira.
    /// `GetOnlineAsync` não filtra soft-deleted, e um agente na lixeira não deve
    /// voltar a executar comandos.
    /// </summary>
    internal static Guid[] SelectRedeliveryAgentIds(IReadOnlyList<Discovery.Core.Entities.Agent> onlineAgents)
        => onlineAgents
            .Where(agent => agent.DeletedAt is null)
            .Select(agent => agent.Id)
            .ToArray();

    private async Task ProcessAsync(NatsCommandRedeliveryWindow window, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var commandRepo = scope.ServiceProvider.GetRequiredService<ICommandRepository>();

        // Independe do transporte: comando preso além da janela nunca vai chegar.
        await ExpireStaleAsync(scope.ServiceProvider, commandRepo, window, ct);

        // Sem transporte não há o que reenviar: os demais continuam pendentes
        // e entram na próxima varredura.
        if (!messaging.IsConnected)
            return;

        var onlineAgents = await agentRepo.GetOnlineAsync(ct);
        var agentIds = SelectRedeliveryAgentIds(onlineAgents);
        if (agentIds.Length == 0)
            return;
        var candidates = await commandRepo.GetRedeliveryCandidatesAsync(
            agentIds, window.CreatedAfterUtc, window.StaleBeforeUtc, window.BatchSize, ct);

        if (candidates.Count == 0)
            return;

        var sent = 0;
        foreach (var command in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var wireType = CommandTypeWireMapper.ToWireValue(command.CommandType);
                await messaging.SendCommandAsync(command.AgentId, command.Id, wireType, command.Payload);

                // Atualiza SentAt: além de refletir a tentativa, é o que espaça as
                // reentregas (RetryGraceSeconds). Result/ExitCode/ErrorMessage são
                // repassados para não apagar um resultado parcial já gravado.
                await commandRepo.UpdateStatusAsync(
                    command.Id, CommandStatus.Sent, command.Result, command.ExitCode, command.ErrorMessage);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Falha ao reentregar comando {CommandId} para o agente {AgentId}.",
                    command.Id, command.AgentId);
            }
        }

        logger.LogInformation(
            "Reentrega de comandos: {Sent}/{Total} reenviado(s) para {Agents} agente(s) online.",
            sent, candidates.Count, agentIds.Length);
    }

    /// <summary>
    /// Encerra comandos que passaram da janela de retenção sem confirmação: sem
    /// isso a linha (e a execução no histórico) fica "pendente" para sempre, sem
    /// que ninguém saiba que nunca será entregue.
    ///
    /// O status do comando pode ser atualizado depois por um resultado tardio,
    /// mas o report de automação tem guarda de estado terminal — nesse caso ele
    /// permanece marcado como não entregue (mensagem explícita).
    /// </summary>
    private async Task ExpireStaleAsync(
        IServiceProvider serviceProvider,
        ICommandRepository commandRepo,
        NatsCommandRedeliveryWindow window,
        CancellationToken ct)
    {
        var expired = await commandRepo.GetExpiredUnconfirmedAsync(window.CreatedAfterUtc, window.BatchSize, ct);
        if (expired.Count == 0)
            return;

        var reportRepo = serviceProvider.GetRequiredService<IAutomationExecutionReportRepository>();
        const string reason = "Não entregue: agente offline além da janela de reentrega.";

        foreach (var command in expired)
        {
            ct.ThrowIfCancellationRequested();

            await commandRepo.UpdateStatusAsync(command.Id, CommandStatus.Timeout, null, null, reason);

            // Atualiza a execução correspondente (quando existir) para o histórico
            // não exibir "em andamento" eterno. Idempotente por natureza.
            await reportRepo.UpdateResultFromCommandAsync(
                command.Id, success: false, exitCode: null, errorMessage: reason,
                resultMetadataJson: null, DateTime.UtcNow);
        }

        logger.LogInformation("Comandos expirados (sem confirmação): {Count}.", expired.Count);
    }
}
