using Meduza.Core.Interfaces;

namespace Meduza.Api.Services;

public class MeshCentralIdentitySyncTriggerService
{
    private readonly IMeshCentralIdentitySyncService _syncService;
    private readonly ILogger<MeshCentralIdentitySyncTriggerService> _logger;

    public MeshCentralIdentitySyncTriggerService(
        IMeshCentralIdentitySyncService syncService,
        ILogger<MeshCentralIdentitySyncTriggerService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public async Task<MeshCentralIdentitySyncResult> OnUserCreatedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _syncService.SyncUserOnCreateAsync(userId, cancellationToken);
            LogResult("create", userId, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MeshCentral trigger create failed for user {UserId}", userId);
            return BuildFailureResult(userId, ex.Message);
        }
    }

    public async Task OnUserUpdatedBestEffortAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await ExecuteBestEffortAsync(
            operationName: "update",
            userId,
            ct => _syncService.SyncUserOnUpdatedAsync(userId, ct),
            cancellationToken);
    }

    public async Task OnUserDeprovisionBestEffortAsync(Guid userId, bool deleteRemoteUser = false, CancellationToken cancellationToken = default)
    {
        await ExecuteBestEffortAsync(
            operationName: "deprovision",
            userId,
            ct => _syncService.DeprovisionUserAsync(userId, deleteRemoteUser, ct),
            cancellationToken);
    }

    public async Task OnUserScopesChangedBestEffortAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await ExecuteBestEffortAsync(
            operationName: "scope-change",
            userId,
            ct => _syncService.SyncUserScopesAsync(userId, ct),
            cancellationToken);
    }

    public async Task OnUsersScopesChangedBestEffortAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        foreach (var userId in userIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await OnUserScopesChangedBestEffortAsync(userId, cancellationToken);
        }
    }

    private async Task ExecuteBestEffortAsync(
        string operationName,
        Guid userId,
        Func<CancellationToken, Task<MeshCentralIdentitySyncResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action(cancellationToken);
            LogResult(operationName, userId, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MeshCentral trigger {OperationName} failed for user {UserId}", operationName, userId);
        }
    }

    private void LogResult(string operationName, Guid userId, MeshCentralIdentitySyncResult result)
    {
        if (result.Synced)
        {
            _logger.LogInformation(
                "MeshCentral trigger {OperationName} succeeded for user {UserId}. MeshUserId={MeshUserId}, SiteBindingsApplied={SiteBindingsApplied}",
                operationName,
                userId,
                result.MeshUserId,
                result.SiteBindingsApplied);
            return;
        }

        _logger.LogWarning(
            "MeshCentral trigger {OperationName} completed with failure for user {UserId}. Error={Error}",
            operationName,
            userId,
            result.Error);
    }

    private static MeshCentralIdentitySyncResult BuildFailureResult(Guid userId, string error)
    {
        return new MeshCentralIdentitySyncResult
        {
            UserId = userId,
            LocalLogin = string.Empty,
            Synced = false,
            MeshUsername = string.Empty,
            SiteBindingsApplied = 0,
            Error = error
        };
    }
}
