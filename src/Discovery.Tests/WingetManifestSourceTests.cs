using System.Net;
using System.Text;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class WingetManifestSourceTests
{
    [TestCase("Google.Chrome", "156.0.8078.48",
        "manifests/g/Google/Chrome/156.0.8078.48/Google.Chrome.installer.yaml")]
    [TestCase("Microsoft.VisualStudio.2022.BuildTools", "17.9.1",
        "manifests/m/Microsoft/VisualStudio/2022/BuildTools/17.9.1/Microsoft.VisualStudio.2022.BuildTools.installer.yaml")]
    public void BuildManifestPathFollowsRepositoryLayout(string packageId, string version, string expected)
    {
        Assert.That(WingetManifestSource.BuildManifestPath(packageId, version), Is.EqualTo(expected));
    }

    [Test]
    public void SelectLatestVersionComparesNumerically()
    {
        var names = new[] { "156.0.9", "156.0.10", "156.0.2", "Beta", "Preview", "156.1.0" };
        Assert.That(WingetManifestSource.SelectLatestVersion(names), Is.EqualTo("156.1.0"));

        Assert.That(WingetManifestSource.SelectLatestVersion(new[] { "156.0.9", "156.0.10" }),
            Is.EqualTo("156.0.10"), "ordem textual colocaria 156.0.9 no topo");
        Assert.That(WingetManifestSource.SelectLatestVersion(new[] { "Beta" }), Is.Null);
        Assert.That(WingetManifestSource.SelectLatestVersion(null), Is.Null);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Test]
    public async Task GetLatestVersionParsesGitHubContentsListing()
    {
        var handler = new FakeHandler(_ => Json("""
        [
          {"name":"156.0.9","type":"dir"},
          {"name":"156.0.10","type":"dir"},
          {"name":"Beta","type":"dir"}
        ]
        """));
        var source = new WingetManifestSource(new HttpClient(handler));

        var version = await source.GetLatestVersionAsync("Google.Chrome");

        Assert.That(version, Is.EqualTo("156.0.10"));
    }

    [Test]
    public async Task GetInstallerManifestUsesRawContent()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("PackageIdentifier: Vendor.App", Encoding.UTF8)
            };
        });
        var source = new WingetManifestSource(new HttpClient(handler));

        var yaml = await source.GetInstallerManifestAsync("Vendor.App", "1.0.0");

        Assert.That(yaml, Does.Contain("PackageIdentifier"));
        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.RequestUri!.ToString(),
            Is.EqualTo("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/v/Vendor/App/1.0.0/Vendor.App.installer.yaml"));
        Assert.That(seen.Headers.UserAgent.ToString(), Does.Contain("DiscoveryRMM"));
    }

    [Test]
    public async Task MissingManifestReturnsNullInsteadOfThrowing()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = new WingetManifestSource(new HttpClient(handler));

        Assert.That(await source.GetLatestVersionAsync("Vendor.App"), Is.Null);
        Assert.That(await source.GetInstallerManifestAsync("Vendor.App", "1.0.0"), Is.Null);
    }
}
