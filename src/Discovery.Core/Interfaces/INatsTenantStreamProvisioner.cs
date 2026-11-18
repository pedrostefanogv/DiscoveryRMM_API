namespace Discovery.Core.Interfaces;

/// <summary>
/// Provisiona e migra streams JetStream isolados por tenant para fan-out de comandos
/// e chat de suporte, eliminando ACLs wildcard globais entre tenants.
/// </summary>
public interface INatsTenantStreamProvisioner
{
    Task EnsureGlobalFanoutStreamAsync(CancellationToken ct = default);
    Task EnsureTenantFanoutStreamAsync(Guid clientId, CancellationToken ct = default);
    Task EnsureTenantSupportChatStreamAsync(Guid clientId, CancellationToken ct = default);
    Task MigrateLegacyWildcardStreamsAsync(CancellationToken ct = default);
}
