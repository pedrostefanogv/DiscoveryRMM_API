using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F08_ControllerValidatorAndCiTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await ApiE2ETestHost.StartAsync();
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Test]
    public async Task T1_F08_01_SendCommand_Update_Install_Valid_Normalizes_And_Returns_201()
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            action = "install",
            version = "1.2.3",
            url = "https://updates.discovery.local/agent-1.2.3.exe"
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Update",
                payload = payloadJson
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var cmdId = doc.RootElement.GetProperty("id").GetGuid();

        var persisted = await _host.WithDbAsync(async db =>
            await db.AgentCommands.AsNoTracking().FirstAsync(c => c.Id == cmdId));
        Assert.That(persisted.Payload, Does.Contain("1.2.3"));
    }

    [Test]
    public async Task T1_F08_02_SendCommand_Notification_Applies_Defaults_And_Returns_201()
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            notificationId = Guid.NewGuid().ToString(),
            idempotencyKey = Guid.NewGuid().ToString("N"),
            title = "Maintenance Window",
            message = "Reboot scheduled in 10 minutes",
            eventType = "maintenance"
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Notification",
                payload = payloadJson
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var cmdId = doc.RootElement.GetProperty("id").GetGuid();

        var persisted = await _host.WithDbAsync(async db =>
            await db.AgentCommands.AsNoTracking().FirstAsync(c => c.Id == cmdId));
        Assert.That(persisted.Payload, Does.Contain("notify_only"));
        Assert.That(persisted.Payload, Does.Contain("toast"));
    }

    [Test]
    public async Task T1_F08_03_SendCommand_RunSensors_And_DecommissionAgent_Return_201()
    {
        using var sensorsReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "RunSensors",
                payload = "{}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var sensorsResp = await _host.HttpClient.SendAsync(sensorsReq);
        Assert.That(sensorsResp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var decomReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "DecommissionAgent",
                payload = "{ \"requestedBy\": \" admin \", \"reason\": \" retired \" }"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var decomResp = await _host.HttpClient.SendAsync(decomReq);
        Assert.That(decomResp.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [Test]
    public async Task T1_F08_04_Reports_Preview_Executes_Valid_Dataset_Query()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/preview")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                template = new
                {
                    name = "E2E Agents Preview",
                    datasetType = "Agents"
                },
                format = "Csv",
                previewMode = "data"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(resp.Headers.TryGetValues("X-Report-RowCount", out var rowCountVals), Is.True);
        Assert.That(int.Parse(rowCountVals!.First()), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void T1_F08_05_Ci_Workflow_Exists_And_SecurityHotfix_Has_Closed_Trigger()
    {
        var apiRoot = FindApiRepoRoot();
        var ciPath = Path.Combine(apiRoot, ".github", "workflows", "ci.yml");
        Assert.That(File.Exists(ciPath), Is.True, "F08 requires .github/workflows/ci.yml in DiscoveryRMM_API.");

        var ciYaml = File.ReadAllText(ciPath);
        Assert.That(ciYaml, Does.Contain("dotnet test"));

        var hotfixPath = Path.Combine(apiRoot, ".github", "workflows", "security-hotfix.yml");
        Assert.That(File.Exists(hotfixPath), Is.True);
        var hotfixYaml = File.ReadAllText(hotfixPath);
        Assert.That(hotfixYaml, Does.Contain("closed"),
            "security-hotfix.yml pull_request.types must include 'closed' for auto-sync-to-branches to trigger.");
    }

    private static string FindApiRepoRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Discovery.slnx")))
                return dir.FullName;
            var sub = Path.Combine(dir.FullName, "DiscoveryRMM_API");
            if (File.Exists(Path.Combine(sub, "Discovery.slnx")))
                return sub;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate DiscoveryRMM_API root.");
    }
}
