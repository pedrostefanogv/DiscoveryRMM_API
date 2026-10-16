using Discovery.Api.Controllers;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Regressão (2026-10-15): quando o instalador do pacote custom não existe mais no
/// object storage, o endpoint de download devolvia <b>HTTP 406</b> sem corpo em vez
/// de 404. O <c>Content-Type</c>/<c>Content-Disposition</c> do instalador já tinham
/// sido aplicados à resposta antes do stream; ao voltar um <c>NotFound(new { ... })</c>
/// o MVC tentava serializar o JSON nesse mesmo media type, não encontrava output
/// formatter e trocava o status. O agent reportava apenas "HTTP 406 Not Acceptable"
/// e a causa real (arquivo ausente) ficava invisível.
/// </summary>
[TestFixture]
public class AgentCustomFileDownloadTests
{
    private static readonly Guid AgentId = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();
    private const string ObjectKey = "app-store/custom/installers/2c2dc39e/Setup.zip";
    private const string InstallerContentType = "application/x-zip-compressed";

    [Test]
    public async Task Falha_do_storage_devolve_404_e_nao_406()
    {
        await using var db = AppPackageTestDbContext.New();
        var appPackages = new AppPackageRepository(db);
        await appPackages.UpsertCustomAsync(new AppPackage
        {
            Id = Guid.NewGuid(),
            InstallationType = AppInstallationType.Custom,
            PackageId = "foxitedito",
            Name = "Foxit editor",
            InstallCommand = "\"{{installer}}\" /S",
            FileObjectKey = ObjectKey,
            FileName = "Setup.zip",
            FileContentType = InstallerContentType,
            FileSizeBytes = 2145443974,
        });

        var controller = NewController();
        var httpContext = controller.ControllerContext.HttpContext;

        var result = await controller.DownloadCustomAppFile(
            "foxitedito",
            appPackages,
            new MissingObjectAssets(),
            new ApprovedAppStoreService(),
            CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>(),
            "arquivo ausente no storage deve virar 404, não 406");

        // Sem limpar os headers do instalador o MVC responde 406 sem corpo.
        Assert.Multiple(() =>
        {
            Assert.That(httpContext.Response.ContentType, Is.Null.Or.Empty);
            Assert.That(httpContext.Response.Headers.ContainsKey("Content-Disposition"), Is.False);
        });

        var status = await ExecuteMvcResultAsync(httpContext, (ObjectResult)result);
        Assert.That(status, Is.EqualTo(StatusCodes.Status404NotFound),
            "o 406 mascarava a causa real da falha de instalação");
    }

    /// <summary>Executa o ObjectResult pelo mesmo executor do MVC (negociação de conteúdo real).</summary>
    private static async Task<int> ExecuteMvcResultAsync(HttpContext httpContext, ObjectResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        await using var provider = services.BuildServiceProvider();

        var executor = provider.GetRequiredService<IActionResultExecutor<ObjectResult>>();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        await executor.ExecuteAsync(actionContext, result);
        return httpContext.Response.StatusCode;
    }

    private static AgentAuthController NewController()
    {
        var controller = new AgentAuthController(
            mediator: null!,
            agentRepo: new FakeAgentRepository(new Agent { Id = AgentId, SiteId = SiteId, Hostname = "host" }),
            siteRepo: new FakeSiteRepository(new Site { Id = SiteId, ClientId = Guid.NewGuid(), Name = "site" }),
            workflowRepo: null!,
            aiChat: null!,
            departmentRepo: null!,
            workflowProfileRepo: null!,
            departmentCustomFieldService: null!,
            ticketFormSchemaService: null!,
            p2pService: null!,
            attachmentService: null!,
            attachmentRepository: null!,
            knowledgeArticleRepository: null!,
            notificationService: null!,
            logger: NullLogger<AgentAuthController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AgentId"] = AgentId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>Storage sem o objeto: replica o erro do provedor S3/RustFS no stream.</summary>
    private sealed class MissingObjectAssets : ICustomPackageAssetService
    {
        public Task<CustomPackageAssetDto> UploadAsync(
            string kind, string fileName, string contentType, Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomPackageAssetContent> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"Objeto {objectKey} nao existe no object storage.");

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ApprovedAppStoreService : IAppStoreService
    {
        public Task<bool> IsPackageApprovedAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType,
            string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<EffectiveApprovedAppDto>> GetEffectiveAppsAsync(Guid? clientId, Guid? siteId, Guid? agentId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogSearchResultDto> SearchCatalogAsync(AppInstallationType installationType, string? search, string? architecture, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto?> GetCatalogPackageByIdAsync(AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppCatalogPackageDto> UpsertCustomCatalogPackageAsync(UpsertCustomAppCatalogPackageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageFileAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteCustomPackageIconAsync(string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalRuleResolvedDto> UpsertRuleAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, AppApprovalActionType action, bool? autoUpdateEnabled, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AppApprovalRuleResolvedDto>> GetRulesByScopeAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteRuleAsync(Guid ruleId, string? reason, string? changedBy, string? ipAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalAuditPageDto> GetAuditHistoryAsync(AppInstallationType installationType, string? packageId, AppApprovalScopeType? scopeType, Guid? scopeId, string? changedBy, DateTime? changedFrom, DateTime? changedTo, AppApprovalAuditChangeType? changeType, int limit, Guid? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EffectiveApprovedAppPageDto> GetEffectiveAppsPageAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppApprovalPackageDiffDto> GetPackageDiffAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string packageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AppEffectivePackageDiffPageDto> GetEffectiveAppDiffsAsync(AppApprovalScopeType scopeType, Guid? scopeId, AppInstallationType installationType, string? search, int limit, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult<Agent?>(agent.Id == id ? agent : null);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSiteRepository(Site site) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(site.Id == id ? site : null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }
}
