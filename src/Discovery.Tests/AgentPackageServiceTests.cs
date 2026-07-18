using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Tests for AgentPackageService.
/// Note: Full build tests (BuildInstallerAsync, BuildBootstrapInstallerAsync, etc.)
/// require a real agent source repo with Go/Wails/NSIS toolchain and are exercised
/// via integration/end-to-end tests on the build server.
/// </summary>
public class AgentPackageServiceTests
{
    private sealed class StubConfigurationService : IConfigurationService
    {
        public Task<ServerConfiguration> GetServerConfigAsync() =>
            Task.FromResult(new ServerConfiguration());

        public Task<ServerConfiguration> UpdateServerAsync(ServerConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ServerConfiguration> ResetServerAsync(string? resetBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId) => throw new NotImplementedException();
        public Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null) => throw new NotImplementedException();
        public Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId) => throw new NotImplementedException();
        public Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null) => throw new NotImplementedException();
        public Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null) => throw new NotImplementedException();
        public Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null) => throw new NotImplementedException();
        public Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null) => throw new NotImplementedException();
        public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config) => throw new NotImplementedException();
        public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json) => throw new NotImplementedException();
    }
}