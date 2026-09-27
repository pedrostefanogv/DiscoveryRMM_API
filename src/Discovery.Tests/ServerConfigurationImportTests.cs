using Discovery.Core.Cqrs.Configurations.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.Configurations;

namespace Discovery.Tests;

/// <summary>
/// Export/import da configuração do servidor: chaves desconhecidas precisam
/// bloquear a importação (e aparecer no dry-run) e segredos nunca são importados.
/// </summary>
public class ServerConfigurationImportTests
{
    [Test]
    public async Task DryRun_ReportsFieldsWithoutApplying()
    {
        var service = new FakeConfigurationService();
        var handler = new ImportServerConfigurationCommandHandler(service);

        var result = await handler.Handle(
            new ImportServerConfigurationCommand(
                new Dictionary<string, object>
                {
                    ["discoveryEnabled"] = true,
                    ["meshCentralGroupPolicyProfile"] = "viewer"
                },
                DryRun: true,
                ChangedBy: "tester"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.DryRun, Is.True);
        Assert.That(result.Value!.AppliedFields, Does.Contain("discoveryEnabled"));
        Assert.That(result.Value!.UnknownFields, Does.Contain("meshCentralGroupPolicyProfile"));
        Assert.That(service.PatchCalls, Is.Empty);
    }

    [Test]
    public async Task Import_RejectsUnknownFieldsWithoutApplying()
    {
        var service = new FakeConfigurationService();
        var handler = new ImportServerConfigurationCommandHandler(service);

        var result = await handler.Handle(
            new ImportServerConfigurationCommand(
                new Dictionary<string, object> { ["meshCentralGroupPolicyProfile"] = "viewer" },
                DryRun: false,
                ChangedBy: "tester"),
            CancellationToken.None);

        Assert.That(result.IsFailure, Is.True);
        Assert.That(service.PatchCalls, Is.Empty);
    }

    [Test]
    public async Task Import_AppliesKnownFieldsAndNeverSecrets()
    {
        var service = new FakeConfigurationService();
        var handler = new ImportServerConfigurationCommandHandler(service);

        var result = await handler.Handle(
            new ImportServerConfigurationCommand(
                new Dictionary<string, object>
                {
                    ["discoveryEnabled"] = true,
                    ["objectStorageSecretKey"] = "plaintext-secret"
                },
                DryRun: false,
                ChangedBy: "tester"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.PatchCalls, Has.Count.EqualTo(1));
        Assert.That(service.PatchCalls[0].ContainsKey("discoveryEnabled"), Is.True);
        Assert.That(service.PatchCalls[0].ContainsKey("objectStorageSecretKey"), Is.False);
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        public List<Dictionary<string, object>> PatchCalls { get; } = [];

        public Task<ServerConfiguration> GetServerConfigAsync()
            => Task.FromResult(new ServerConfiguration { Id = Guid.NewGuid(), Version = 7 });

        public Task<ServerConfiguration> PatchServerAsync(Dictionary<string, object> updates, string? updatedBy = null)
        {
            PatchCalls.Add(updates);
            return Task.FromResult(new ServerConfiguration { Id = Guid.NewGuid(), Version = 8 });
        }

        public Task<ServerConfiguration> ResetServerAsync(string? resetBy = null)
            => throw new NotSupportedException();

        public Task<ClientConfiguration?> GetClientConfigAsync(Guid clientId)
            => throw new NotSupportedException();

        public Task<ClientConfiguration> CreateClientConfigAsync(Guid clientId, ClientConfiguration config, string? createdBy = null)
            => throw new NotSupportedException();

        public Task<ClientConfiguration> UpdateClientAsync(Guid clientId, ClientConfiguration config, string? updatedBy = null)
            => throw new NotSupportedException();

        public Task<ClientConfiguration> PatchClientAsync(Guid clientId, Dictionary<string, object> updates, string? updatedBy = null)
            => throw new NotSupportedException();

        public Task DeleteClientConfigAsync(Guid clientId, string? deletedBy = null)
            => throw new NotSupportedException();

        public Task ResetClientPropertyAsync(Guid clientId, string propertyName, string? resetBy = null)
            => throw new NotSupportedException();

        public Task<SiteConfiguration?> GetSiteConfigAsync(Guid siteId)
            => throw new NotSupportedException();

        public Task<SiteConfiguration> CreateSiteConfigAsync(Guid siteId, SiteConfiguration config, string? createdBy = null)
            => throw new NotSupportedException();

        public Task<SiteConfiguration> UpdateSiteAsync(Guid siteId, SiteConfiguration config, string? updatedBy = null)
            => throw new NotSupportedException();

        public Task<SiteConfiguration> PatchSiteAsync(Guid siteId, Dictionary<string, object> updates, string? updatedBy = null)
            => throw new NotSupportedException();

        public Task DeleteSiteConfigAsync(Guid siteId, string? deletedBy = null)
            => throw new NotSupportedException();

        public Task ResetSitePropertyAsync(Guid siteId, string propertyName, string? resetBy = null)
            => throw new NotSupportedException();

        public Task<(bool IsValid, string[] Errors)> ValidateAsync(object config)
            => Task.FromResult((true, Array.Empty<string>()));

        public Task<(bool IsValid, string[] Errors)> ValidateJsonAsync(string objectType, string json)
            => Task.FromResult((true, Array.Empty<string>()));
    }
}
