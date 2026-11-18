using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.E2E.Tests.Harness;

namespace Discovery.E2E.Tests.Tier2;

[TestFixture]
public class F08_ValidatorAndCiEdgeTests
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
    public async Task T2_F08_01_SendCommand_Update_Rejects_Legacy_ForceUpdate_And_Http_Url()
    {
        using var legacyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Update",
                payload = "{\"action\":\"force-update\"}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);
        using var legacyResp = await _host.HttpClient.SendAsync(legacyReq);
        Assert.That(legacyResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Update",
                payload = "{\"action\":\"install\",\"version\":\"1.2.3\",\"url\":\"http://insecure.local/agent.exe\"}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);
        using var httpResp = await _host.HttpClient.SendAsync(httpReq);
        Assert.That(httpResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task T2_F08_02_SendCommand_Notification_Missing_Required_Fields_Returns_400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "Notification",
                payload = "{\"message\":\"No title or keys\"}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task T2_F08_03_SendCommand_RemoteDebug_Rejects_NonTenant_Subject_And_Missing_ExpiresAtUtc()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "RemoteDebug",
                payload = JsonSerializer.Serialize(new
                {
                    action = "start",
                    sessionId = Guid.NewGuid().ToString(),
                    stream = new { natsSubject = "invalid.subject.log" }
                })
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task T2_F08_04_SendCommand_SoftwareInstall_Rejects_Integer_InstallationType_And_Missing_PackageId()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "SoftwareInstall",
                payload = "{\"installationType\":3}"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task T2_F08_05_TicketChatInvite_NonGuid_And_ReportsPreview_UnknownDataset_Return_400()
    {
        using var inviteReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/agents/{_world.AgentA1.Id}/commands")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                commandType = "TicketChatInvite",
                payload = JsonSerializer.Serialize(new { sessionId = Guid.NewGuid().ToString(), ticketId = "not-a-guid" })
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);
        using var inviteResp = await _host.HttpClient.SendAsync(inviteReq);
        Assert.That(inviteResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var reportReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reports/preview")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                template = new { name = "Bad Dataset", datasetType = "NonExistentDataset999" },
                format = "Csv",
                previewMode = "data"
            }), Encoding.UTF8, "application/json")
        }.WithJwt(_world.GlobalAdmin.Jwt);
        using var reportResp = await _host.HttpClient.SendAsync(reportReq);
        Assert.That(reportResp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
