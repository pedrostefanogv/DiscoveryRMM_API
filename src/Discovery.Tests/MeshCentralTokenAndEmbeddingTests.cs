using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class MeshCentralTokenAndEmbeddingTests
{
    private static readonly string ValidLoginKeyHex = new('a', 64);

    [Test]
    public void GenerateLoginToken_ShouldReturnOpaqueToken()
    {
        var service = new MeshCentralTokenService(Options.Create(new MeshCentralOptions
        {
            LoginKeyHex = ValidLoginKeyHex,
            DomainId = string.Empty
        }));

        var token = service.GenerateLoginToken("mesh-user");

        Assert.That(token, Is.Not.Null.And.Not.Empty);
        Assert.That(token, Does.Not.Contain("mesh-user"));
    }

    [Test]
    public async Task GenerateUserEmbedUrlAsync_ShouldUseLoginQueryParameter()
    {
        var options = Options.Create(new MeshCentralOptions
        {
            Enabled = true,
            BaseUrl = "https://mesh.public.example/",
            LoginKeyHex = ValidLoginKeyHex,
            TechnicalUsername = "svc-discovery"
        });
        var configService = new MeshCentralConfigService(options);
        var tokenService = new MeshCentralTokenService(options);
        var embeddingService = new MeshCentralEmbeddingService(options, configService, tokenService);

        var result = await embeddingService.GenerateUserEmbedUrlAsync(
            "mesh-user",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            11,
            null,
            "node//abc123",
            null,
            CancellationToken.None);

        Assert.That(result.Url, Does.Contain("?login="));
        Assert.That(result.Url, Does.Not.Contain("auth="));
        Assert.That(result.Url, Does.Contain("gotonode=node%2F%2Fabc123"));
    }

    [Test]
    public async Task GenerateAgentEmbedUrlAsync_ShouldPreferPersistedMeshNodeId()
    {
        var options = Options.Create(new MeshCentralOptions
        {
            Enabled = true,
            BaseUrl = "https://mesh.public.example/",
            LoginKeyHex = ValidLoginKeyHex,
            TechnicalUsername = "svc-discovery"
        });
        var configService = new MeshCentralConfigService(options);
        var tokenService = new MeshCentralTokenService(options);
        var embeddingService = new MeshCentralEmbeddingService(options, configService, tokenService);

        var agent = new Agent
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SiteId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Hostname = "agent-01",
            MeshCentralNodeId = "node//persisted"
        };

        var result = await embeddingService.GenerateAgentEmbedUrlAsync(
            agent,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            11,
            null,
            "node//request",
            null,
            CancellationToken.None);

        Assert.That(result.Url, Does.Contain("gotonode=node%2F%2Fpersisted"));
        Assert.That(result.Url, Does.Not.Contain("gotonode=node%2F%2Frequest"));
    }
}