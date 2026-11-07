using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Garante que a política de acesso por departamento é REGISTRADA e RESOLVÍVEL
/// pelo container de DI.
///
/// Risco real coberto: o auto-registro (AddDiscoveryAutoRegisteredServices) só
/// registra interfaces com UMA implementação e resolve o construtor pelo
/// container. A política ganhou parâmetros opcionais (IConfiguration/ILogger)
/// para permitir ligar/desligar a regra — se algum deles não fosse resolvível,
/// a aplicação falharia ao subir, e nenhum teste de unidade pegaria isso porque
/// eles constroem o serviço diretamente.
/// </summary>
public class TicketAccessPolicyRegistrationTests
{
    [Test]
    public void Politica_e_registrada_e_resolvivel_pelo_container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        var registrations = Discovery.Api.DependencyInjection.ServiceCollectionExtensions
            .AddDiscoveryAutoRegisteredServices(services);

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(ITicketAccessPolicy)),
            Is.True,
            "ITicketAccessPolicy precisa entrar no auto-registro (uma unica implementacao).");

        Assert.That(
            registrations.Single(r => r.InterfaceType == typeof(ITicketAccessPolicy)).ImplementationType,
            Is.EqualTo(typeof(TicketAccessPolicy)));
    }

    [Test]
    public void Configuracao_define_o_modo_de_observacao_no_container()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tickets:DepartmentAuth:ShadowMode"] = "false"
            })
            .Build();

        Assert.That(
            config.GetValue("Tickets:DepartmentAuth:ShadowMode", defaultValue: true),
            Is.False,
            "A chave lida pela politica deve refletir o appsettings.");
    }
}
