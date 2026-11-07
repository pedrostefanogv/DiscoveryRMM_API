using Discovery.Api.DependencyInjection;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Tests;

/// <summary>
/// Garante que os serviços de identidade criados na revisão de 2026-11-07 são
/// resolvíveis pelo host.
///
/// Risco real coberto aqui: <c>AddDiscoveryAutoRegisteredServices</c> só registra
/// interfaces que tenham EXATAMENTE UMA implementação pública em
/// Discovery.Infrastructure (ServiceCollectionExtensions.cs:79). <c>UserRepository</c>
/// implementa DUAS interfaces de Core (IUserRepository e IUserScopedQueryRepository) —
/// o que funciona porque cada interface tem uma implementação, mas se alguém
/// adicionar outra implementação de IUserScopedQueryRepository o scan PULA a
/// interface e o host quebra na primeira requisição (erro de DI em runtime, não em
/// build). Estes testes transformam isso em falha de build.
/// </summary>
[TestFixture]
public class IdentityServiceRegistrationTests
{
    [Test]
    public void Scoped_user_query_repository_is_auto_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IUserScopedQueryRepository)
                                   && r.ImplementationType == typeof(UserRepository)),
            Is.True,
            "IUserScopedQueryRepository (listagem/checagem escopada de usuários) precisa ser resolvido pelo host.");
    }

    [Test]
    public void Core_user_repository_is_still_auto_registered()
    {
        // Regressão: adicionar a segunda interface não pode ter "roubado" o
        // registro original de IUserRepository.
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IUserRepository)
                                   && r.ImplementationType == typeof(UserRepository)),
            Is.True);
    }

    [Test]
    public void Scope_context_is_registered()
    {
        var services = new ServiceCollection();

        var registrations = services.AddDiscoveryAutoRegisteredServices();

        Assert.That(
            registrations.Any(r => r.InterfaceType == typeof(IScopeContext)),
            Is.True,
            "IScopeContext é a base de toda decisão de escopo (usuários, acesso remoto, tickets).");
    }

    [Test]
    public void Identity_audit_service_has_an_implementation()
    {
        // Não passa pelo auto-scan (registrado explicitamente em Program.cs), então a
        // guarda aqui é sobre a existência de uma implementação concreta única.
        var impls = typeof(IdentityAuditService).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IIdentityAuditService).IsAssignableFrom(t))
            .ToList();

        Assert.That(impls, Has.Count.EqualTo(1),
            "deve existir exatamente uma implementação de IIdentityAuditService");
        Assert.That(impls[0], Is.EqualTo(typeof(IdentityAuditService)));
    }
}
