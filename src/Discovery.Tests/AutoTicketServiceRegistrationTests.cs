using Discovery.Api.DependencyInjection;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Garante que os serviços novos do auto-ticket entram pelo AUTO-REGISTRO de
/// serviços (scan de Discovery.Infrastructure), sem exigir linha manual em
/// Program.cs.
///
/// O scan só registra interfaces que tenham EXATAMENTE UMA implementação
/// pública. Se alguém adicionar uma segunda implementação, estes testes falham
/// — que é exatamente o alerta desejado, porque o host deixaria de resolver as
/// dependências do orquestrador.
/// </summary>
[TestFixture]
public class AutoTicketServiceRegistrationTests
{
    [Test]
    public void PolicyResolver_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IAutoTicketPolicyResolver)
                                   && r.ImplementationType == typeof(AutoTicketPolicyResolver)),
            Is.True,
            "IAutoTicketPolicyResolver precisa ser resolvido pelo host.");
    }

    [Test]
    public void SignalEvaluator_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IAutoTicketSignalEvaluator)
                                   && r.ImplementationType == typeof(AutoTicketSignalEvaluator)),
            Is.True,
            "IAutoTicketSignalEvaluator precisa ser resolvido pelo host.");
    }

    [Test]
    public void EnrichmentService_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IAutoTicketEnrichmentService)
                                   && r.ImplementationType == typeof(AutoTicketEnrichmentService)),
            Is.True);
    }

    [Test]
    public void SuggestionApplier_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IAutoTicketSuggestionApplier)
                                   && r.ImplementationType == typeof(AutoTicketSuggestionApplier)),
            Is.True);
    }

    [Test]
    public void ResolutionService_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IAutoTicketResolutionService)
                                   && r.ImplementationType == typeof(AutoTicketResolutionService)),
            Is.True);
    }

    [Test]
    public void Orchestrator_dependencies_are_all_resolvable_from_the_scan()
    {
        var services = new ServiceCollection();
        var registrations = services.AddDiscoveryAutoRegisteredServices();

        // As duas dependências novas do orquestrador não podem ter caído no
        // caminho "mais de uma implementação" (que as deixaria sem registro).
        var registered = registrations.Select(r => r.InterfaceType).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(registered, Does.Contain(typeof(IAutoTicketPolicyResolver)));
            Assert.That(registered, Does.Contain(typeof(IAutoTicketSignalEvaluator)));
        });
    }
}
