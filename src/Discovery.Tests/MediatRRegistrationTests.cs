using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Notifications.Commands;
using Discovery.Core.Cqrs.Agents.StartupTasks.Commands;
using Discovery.Api.Cqrs.DependencyInjection;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Regressão: o AddDiscoveryCqrs só escaneava Discovery.Infrastructure e
/// Discovery.Api. Handlers vivendo em Discovery.Core nunca eram registrados,
/// então mediator.Send lançava InvalidOperationException ("Handler not
/// found"), mapeada pelo ExceptionHandlingMiddleware para 400 "Requisição
/// inválida" — silencioso e difícil de rastrear.
///
/// O teste verifica a *registracão* (ServiceCollection), não a construção, para
/// não depender das dependências do handler (repositórios, dispatcher).
/// </summary>
[TestFixture]
public class MediatRHandlerRegistrationTests
{
    private static ServiceCollection BuildCqrsServices()
    {
        var services = new ServiceCollection();
        services.AddDiscoveryCqrs();
        return services;
    }

    [Test]
    public void SendAgentNotificationCommand_Handler_ShouldBeRegistered()
    {
        var registrations = BuildCqrsServices()
            .Where(sd => sd.ServiceType == typeof(IRequestHandler<SendAgentNotificationCommand, Result<Guid>>))
            .ToList();

        Assert.That(
            registrations,
            Has.Count.EqualTo(1),
            "O handler em Discovery.Core precisa ser registrado: adicione o assembly Discovery.Core ao AddDiscoveryCqrs.");
    }

    [Test]
    public void StartupItemActionCommand_Handler_ShouldBeRegistered()
    {
        var registrations = BuildCqrsServices()
            .Where(sd => sd.ServiceType == typeof(IRequestHandler<StartupItemActionCommand, Result<VoidResult>>))
            .ToList();

        Assert.That(registrations, Has.Count.EqualTo(1));
    }

    [Test]
    public void ScheduledTaskActionCommand_Handler_ShouldBeRegistered()
    {
        var registrations = BuildCqrsServices()
            .Where(sd => sd.ServiceType == typeof(IRequestHandler<ScheduledTaskActionCommand, Result<VoidResult>>))
            .ToList();

        Assert.That(registrations, Has.Count.EqualTo(1));
    }
}
