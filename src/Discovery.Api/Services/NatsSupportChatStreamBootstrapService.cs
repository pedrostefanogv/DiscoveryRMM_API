using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

/// <summary>
/// Garante no startup a migração de streams legados e a criação/atualização
/// idempotente dos streams JetStream do chat de suporte isolados por tenant.
/// </summary>
public class NatsSupportChatStreamBootstrapService : BackgroundService
{
    private readonly INatsTenantStreamProvisioner _provisioner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<NatsSupportChatStreamOptions> _optionsMonitor;
    private readonly ILogger<NatsSupportChatStreamBootstrapService> _logger;

    public NatsSupportChatStreamBootstrapService(
        INatsTenantStreamProvisioner provisioner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<NatsSupportChatStreamOptions> optionsMonitor,
        ILogger<NatsSupportChatStreamBootstrapService> logger)
    {
        _provisioner = provisioner;
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("NATS support chat stream bootstrap disabled (Nats:SupportChatStream:Enabled=false).");
            return;
        }

        var attempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempts++;

            try
            {
                await _provisioner.MigrateLegacyWildcardStreamsAsync(stoppingToken);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var clientRepo = scope.ServiceProvider.GetRequiredService<IClientRepository>();
                var clients = await clientRepo.GetAllAsync(includeInactive: true);

                var count = 0;
                foreach (var client in clients)
                {
                    await _provisioner.EnsureTenantSupportChatStreamAsync(client.Id, stoppingToken);
                    count++;
                }

                _logger.LogInformation(
                    "JetStream per-tenant support chat streams ensured on startup ({TenantCount} tenant stream(s)).",
                    count);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (options.MaxRetryAttempts > 0 && attempts >= options.MaxRetryAttempts)
                {
                    _logger.LogError(
                        ex,
                        "Unable to ensure JetStream per-tenant support chat streams after {Attempts} attempts. Service will continue without stopping API startup.",
                        attempts);
                    return;
                }

                var retryDelaySeconds = Math.Max(1, options.RetryDelaySeconds);
                _logger.LogWarning(
                    ex,
                    "Failed to ensure JetStream per-tenant support chat streams (attempt {Attempt}). Retrying in {DelaySeconds}s...",
                    attempts,
                    retryDelaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stoppingToken);
            }
        }
    }
}
