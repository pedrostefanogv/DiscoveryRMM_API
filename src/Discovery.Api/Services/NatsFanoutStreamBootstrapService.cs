using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Discovery.Api.Services;

/// <summary>
/// Garante no startup a migração de streams legados e a criação/atualização
/// idempotente dos streams JetStream de fan-out isolados por tenant e global.
/// </summary>
public class NatsFanoutStreamBootstrapService : BackgroundService
{
    private readonly INatsTenantStreamProvisioner _provisioner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<NatsFanoutStreamOptions> _optionsMonitor;
    private readonly ILogger<NatsFanoutStreamBootstrapService> _logger;

    public NatsFanoutStreamBootstrapService(
        INatsTenantStreamProvisioner provisioner,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<NatsFanoutStreamOptions> optionsMonitor,
        ILogger<NatsFanoutStreamBootstrapService> logger)
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
            _logger.LogInformation("NATS fan-out stream bootstrap disabled (Nats:FanoutStream:Enabled=false).");
            return;
        }

        var attempts = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempts++;

            try
            {
                await _provisioner.MigrateLegacyWildcardStreamsAsync(stoppingToken);
                await _provisioner.EnsureGlobalFanoutStreamAsync(stoppingToken);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var clientRepo = scope.ServiceProvider.GetRequiredService<IClientRepository>();
                var clients = await clientRepo.GetAllAsync(includeInactive: true);

                var count = 0;
                foreach (var client in clients)
                {
                    await _provisioner.EnsureTenantFanoutStreamAsync(client.Id, stoppingToken);
                    count++;
                }

                _logger.LogInformation(
                    "JetStream per-tenant fan-out streams ensured on startup (global + {TenantCount} tenant stream(s)).",
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
                        "Unable to ensure JetStream per-tenant fan-out streams after {Attempts} attempts. Continuing to retry in the background.",
                        attempts);
                }
                else
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to ensure JetStream per-tenant fan-out streams (attempt {Attempt}). Retrying...",
                        attempts);
                }

                var baseDelay = Math.Max(1, options.RetryDelaySeconds);
                var multiplier = options.MaxRetryAttempts > 0 && attempts >= options.MaxRetryAttempts
                    ? Math.Min(5, 1 + ((attempts - options.MaxRetryAttempts) / 10))
                    : 1;
                var retryDelaySeconds = baseDelay * multiplier;

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stoppingToken);
            }
        }
    }
}
