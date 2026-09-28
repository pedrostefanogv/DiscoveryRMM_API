using Discovery.Api.DependencyInjection;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Garante que a injeção de IP2pService (agora usada também pelo
/// AgentAuthController para o rate-limit de telemetria) está coberta pelo
/// auto-registro de serviços — sem isso o host falharia ao construir o controller.
/// </summary>
public class P2pServiceRegistrationTests
{
    [Test]
    public void P2pService_is_auto_registered_for_IP2pService()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IP2pService)
                                   && r.ImplementationType == typeof(P2pService)),
            Is.True);
    }
}
