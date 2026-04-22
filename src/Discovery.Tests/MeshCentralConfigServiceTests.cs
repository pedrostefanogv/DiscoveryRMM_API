using Discovery.Core.Configuration;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class MeshCentralConfigServiceTests
{
    [Test]
    public void BuildControlWebSocketUri_ShouldUseInternalBaseUrl_AndAuthOnlyByDefault()
    {
        var options = Options.Create(new MeshCentralOptions
        {
            BaseUrl = "https://mesh.public.example/mesh/",
            InternalBaseUrl = "https://mesh.internal.local/mesh/",
            TechnicalUsername = "Discovery-Admin"
        });

        var service = new MeshCentralConfigService(options);

        var uri = service.BuildControlWebSocketUri("token-123");

        Assert.That(uri.ToString(), Does.StartWith("wss://mesh.internal.local/mesh/control.ashx?"));
        Assert.That(uri.Query, Does.Contain("auth=token-123"));
        Assert.That(uri.Query, Does.Not.Contain("key="));
        Assert.That(service.GetTechnicalUsername(), Is.EqualTo("discovery-admin"));
    }

    [Test]
    public void BuildControlWebSocketUri_ShouldIncludeKey_WhenCompatibilityModeEnabled()
    {
        var options = Options.Create(new MeshCentralOptions
        {
            BaseUrl = "https://mesh.example.com/",
            LoginKeyHex = new string('a', 64),
            AdministrativeIncludeKeyInQuery = true
        });

        var service = new MeshCentralConfigService(options);

        var uri = service.BuildControlWebSocketUri("token-456");

        Assert.That(uri.Query, Does.Contain("auth=token-456"));
        Assert.That(uri.Query, Does.Contain("key="));
    }
}