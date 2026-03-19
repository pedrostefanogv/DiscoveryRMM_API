using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meduza.Api.DependencyInjection;

internal sealed record AutoServiceRegistration(Type InterfaceType, Type ImplementationType);

internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all services from Meduza.Infrastructure that implement a single interface from Meduza.Core.Interfaces.
    /// This reduces the need to manually register every service/repository one-by-one.
    /// </summary>
    public static IReadOnlyList<AutoServiceRegistration> AddMeduzaAutoRegisteredServices(this IServiceCollection services)
    {
        // Scan the infrastructure assembly for concrete implementations
        var infrastructureAssembly = typeof(Meduza.Infrastructure.Repositories.ClientRepository).Assembly;
        var coreInterfaceAssembly = typeof(IClientRepository).Assembly;

        // Collect all core interfaces we care about
        var coreInterfaces = coreInterfaceAssembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t.Namespace != null && t.Namespace.StartsWith("Meduza.Core.Interfaces", StringComparison.Ordinal))
            .ToArray();

        // Map each interface type to its concrete implementations
        var interfaceToImpls = new Dictionary<Type, List<Type>>();

        foreach (var implType in infrastructureAssembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsPublic))
        {
            foreach (var iface in implType.GetInterfaces())
            {
                if (!coreInterfaces.Contains(iface))
                    continue;

                if (!interfaceToImpls.TryGetValue(iface, out var list))
                {
                    list = new List<Type>();
                    interfaceToImpls[iface] = list;
                }

                list.Add(implType);
            }
        }

        var registeredServices = new List<AutoServiceRegistration>();

        // Interfaces we explicitly want to register manually (special lifetimes, multiple implementations, factory-based, etc.)
        var excludedInterfaces = new[]
        {
            typeof(IReportRenderer),
            typeof(ISyncPingDispatchQueue),
            typeof(IObjectStorageService),
            typeof(ILlmProvider),
            typeof(IJwtService),
            typeof(ISecretProtector),
            typeof(IRedisService),
        };

        // Register only interfaces with a single implementation (avoids ambiguity and multi-implementation patterns)
        foreach (var (iface, impls) in interfaceToImpls)
        {
            if (excludedInterfaces.Contains(iface))
                continue;

            if (impls.Count != 1)
                continue; // leave multi-implementation services to explicit registration

            services.TryAddScoped(iface, impls[0]);
            registeredServices.Add(new AutoServiceRegistration(iface, impls[0]));
        }

        return registeredServices;
    }
}
