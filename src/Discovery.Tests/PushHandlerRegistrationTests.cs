using Discovery.Api.Cqrs.DependencyInjection;
using Discovery.Api.DependencyInjection;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Push;
using Discovery.Core.Cqrs.Push.Commands;
using Discovery.Core.Cqrs.Push.Queries;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Regressao: handlers de Web Push vivem em Discovery.Infrastructure e precisam
/// ser descobertos pelo AddDiscoveryCqrs. Sem isso, o controller responde 400
/// ("Handler not found") de forma silenciosa.
/// </summary>
[TestFixture]
public class PushHandlerRegistrationTests
{
    private static IReadOnlyList<ServiceDescriptor> Registrations()
    {
        var services = new ServiceCollection();
        services.AddDiscoveryCqrs();
        return services.ToList();
    }

    [Test]
    public void RegisterPushSubscriptionCommand_Handler_ShouldBeRegistered()
    {
        Assert.That(
            Registrations().Count(sd => sd.ServiceType == typeof(IRequestHandler<RegisterPushSubscriptionCommand, Result<PushSubscriptionDto>>)),
            Is.EqualTo(1));
    }

    [Test]
    public void DeletePushSubscriptionCommand_Handler_ShouldBeRegistered()
    {
        Assert.That(
            Registrations().Count(sd => sd.ServiceType == typeof(IRequestHandler<DeletePushSubscriptionCommand, Result<VoidResult>>)),
            Is.EqualTo(1));
    }

    /// <summary>
    /// O controller depende de IWebPushSender e NotificationService do repositorio.
    /// Ambos vem do auto-scan de Discovery.Infrastructure: se o auto-registro deixar
    /// de enxerga-los, o erro so aparece em runtime (requisicao).
    /// </summary>
    [Test]
    public void AutoRegistration_ShouldRegisterPushServices()
    {
        var services = new ServiceCollection();
        services.AddDiscoveryAutoRegisteredServices();

        var sender = services.SingleOrDefault(sd => sd.ServiceType == typeof(IWebPushSender));
        var repository = services.SingleOrDefault(sd => sd.ServiceType == typeof(IPushSubscriptionRepository));

        Assert.That(sender, Is.Not.Null, "IWebPushSender nao foi auto-registrado.");
        Assert.That(sender!.ImplementationType, Is.EqualTo(typeof(WebPushSender)));
        Assert.That(repository, Is.Not.Null, "IPushSubscriptionRepository nao foi auto-registrado.");
        Assert.That(repository!.ImplementationType, Is.EqualTo(typeof(PushSubscriptionRepository)));
    }

    [Test]
    public void GetPushStatusQuery_Handler_ShouldBeRegistered()
    {
        Assert.That(
            Registrations().Count(sd => sd.ServiceType == typeof(IRequestHandler<GetPushStatusQuery, Result<PushConfigDto>>)),
            Is.EqualTo(1));
    }
}
