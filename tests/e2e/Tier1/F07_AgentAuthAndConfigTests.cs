using System.Net;
using System.Text;
using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.E2E.Tests.Harness;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;

namespace Discovery.E2E.Tests.Tier1;

[TestFixture]
public class F07_AgentAuthAndConfigTests
{
    private ApiE2ETestHost _host = null!;
    private E2ETenantWorld _world = null!;

    [SetUp]
    public async Task SetUp()
    {
        _host = await ApiE2ETestHost.StartAsync(new Dictionary<string, string?>
        {
            ["Security:RateLimiting:Agent:PermitLimit"] = "3",
            ["Security:RateLimiting:Agent:WindowSeconds"] = "60",
            ["Security:RateLimiting:Agent:QueueLimit"] = "0",
            ["AutomaticLogging:Enabled"] = "true"
        });
        _world = await E2ESeedHelper.SeedMultiTenantWorldAsync(_host);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Test]
    public async Task T1_F07_01_AgentAuth_Me_Succeeds_With_Valid_MdzToken_And_Matching_XAgentId()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenA1, _world.AgentA1.Id);
        using var resp = await _host.HttpClient.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain(_world.AgentA1.Id.ToString()));
    }

    [Test]
    public async Task T1_F07_02_AgentAuth_RateLimit_Is_Partitioned_By_XAgentId_Not_Shared_By_Ip()
    {
        // Exhaust PermitLimit=3 for AgentA1 from 127.0.0.1
        for (var i = 0; i < 3; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
                .WithAgentAuth(_world.AgentTokenA1, _world.AgentA1.Id);
            using var resp = await _host.HttpClient.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var throttledReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenA1, _world.AgentA1.Id);
        using var throttledResp = await _host.HttpClient.SendAsync(throttledReq);
        Assert.That(throttledResp.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

        // AgentB1 from the SAME loopback IP must NOT be throttled by AgentA1's partition!
        using var agentBReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent-auth/me")
            .WithAgentAuth(_world.AgentTokenB1, _world.AgentB1.Id);
        using var agentBResp = await _host.HttpClient.SendAsync(agentBReq);
        Assert.That(agentBResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T1_F07_03_AgentAuth_Post_Does_Not_Write_Verbose_LoggingActionFilter_Db_Rows()
    {
        var beforeLogs = await _host.WithDbAsync(async db => await db.Logs.CountAsync());

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent-auth/me/pending-reboot")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                pendingReboot = true,
                reasons = new[] { "windows_update" }
            }), Encoding.UTF8, "application/json")
        }.WithAgentAuth(_world.AgentTokenA1, _world.AgentA1.Id);

        using var resp = await _host.HttpClient.SendAsync(req);
        Assert.That((int)resp.StatusCode, Is.InRange(200, 204));

        var afterLogs = await _host.WithDbAsync(async db => await db.Logs.CountAsync());
        Assert.That(afterLogs, Is.EqualTo(beforeLogs),
            "/api/v1/agent-auth must be excluded from verbose LoggingActionFilter DB persistence.");
    }

    [Test]
    public void T1_F07_04_NatsConnection_Configures_Unlimited_MaxReconnectRetry_MinusOne()
    {
        var conn = _host.Services.GetRequiredService<NatsConnection>();
        Assert.That(conn.Opts.MaxReconnectRetry, Is.EqualTo(-1));
    }

    [Test]
    public async Task T1_F07_05_McpToolExecutor_Enforces_RateLimit_Across_Separate_DI_Scopes()
    {
        await _host.WithDbAsync(async db =>
        {
            db.McpToolPolicies.Add(new McpToolPolicy
            {
                Id = Guid.NewGuid(),
                ToolName = "time.current",
                IsEnabled = true,
                MaxCallsPerMinute = 2,
                TimeoutSeconds = 10,
                Source = McpToolSources.Server
            });
            await db.SaveChangesAsync();
        });

        var results = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await _host.WithScopeAsync(async sp =>
            {
                var executor = sp.GetRequiredService<IMcpToolExecutor>();
                var output = await executor.ExecuteAsync(
                    "time.current",
                    "{}",
                    _world.ClientA.Id,
                    _world.SiteA1.Id,
                    _world.AgentA1.Id,
                    new AIIntegrationSettings());
                results.Add(output);
            });
        }

        Assert.That(results[0], Does.Not.Contain("Rate limit"));
        Assert.That(results[1], Does.Not.Contain("Rate limit"));
        Assert.That(results[2], Does.Contain("Rate limit"));
    }

    [Test]
    public void T1_F07_06_AppSettings_Preserves_Both_Tickets_DepartmentAuth_And_CategoryOptions()
    {
        var config = _host.Services.GetRequiredService<IConfiguration>();
        var shadowMode = config.GetValue<bool?>("Tickets:DepartmentAuth:ShadowMode");
        var categories = config.GetSection("Tickets:CategoryOptions").GetChildren().ToArray();

        Assert.That(shadowMode, Is.Not.Null, "Tickets:DepartmentAuth:ShadowMode must not be overwritten by duplicate Tickets root key.");
        Assert.That(categories, Is.Not.Empty, "Tickets:CategoryOptions must be preserved.");
    }
}
