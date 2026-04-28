using System.IO.Compression;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class AgentPackageServiceTests
{
    [Test]
    public async Task BuildPortablePackageAsync_ShouldUsePublicApiOverride_WhenProvided()
    {
        using var fixture = new AgentPackageFixture();
        var service = fixture.CreateService();

        var package = await service.BuildPortablePackageAsync("deploy-token", "https://192-168-1-131.nip.io/api/");
        var config = fixture.ReadDebugConfig(package);

        Assert.Multiple(() =>
        {
            Assert.That(config.GetProperty("apiScheme").GetString(), Is.EqualTo("https"));
            Assert.That(config.GetProperty("apiServer").GetString(), Is.EqualTo("192-168-1-131.nip.io"));
            Assert.That(config.GetProperty("deployToken").GetString(), Is.EqualTo("deploy-token"));
        });
    }

    [Test]
    public async Task BuildPortablePackageAsync_ShouldUseConfiguredPublicApiEndpoint_WhenOverrideIsMissing()
    {
        using var fixture = new AgentPackageFixture();
        var service = fixture.CreateService();

        var package = await service.BuildPortablePackageAsync("deploy-token");
        var config = fixture.ReadDebugConfig(package);

        Assert.Multiple(() =>
        {
            Assert.That(config.GetProperty("apiScheme").GetString(), Is.EqualTo("https"));
            Assert.That(config.GetProperty("apiServer").GetString(), Is.EqualTo("api.example.com"));
        });
    }

    private sealed class AgentPackageFixture : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _binaryPath;

        public AgentPackageFixture()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "Discovery.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);

            _binaryPath = Path.Combine(_tempRoot, "discovery-agent.exe");
            File.WriteAllBytes(_binaryPath, [0x44, 0x49, 0x53, 0x43]);
        }

        public AgentPackageService CreateService()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentPackage:ActiveProfile"] = "windows",
                    ["AgentPackage:Profiles:windows:BinaryPath"] = _binaryPath,
                    ["AgentPackage:PublicApiScheme"] = "https",
                    ["AgentPackage:PublicApiServer"] = "api.example.com"
                })
                .Build();

            return new AgentPackageService(
                config,
                new StubConfigurationService(),
                NullLogger<AgentPackageService>.Instance);
        }

        public JsonElement ReadDebugConfig(byte[] package)
        {
            using var ms = new MemoryStream(package);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = archive.GetEntry("debug_config.json");
            Assert.That(entry, Is.Not.Null);

            using var stream = entry!.Open();
            using var reader = new StreamReader(stream);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            return document.RootElement.Clone();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
    }

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