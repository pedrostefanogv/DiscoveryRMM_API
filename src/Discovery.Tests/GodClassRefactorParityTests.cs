using System.Reflection;
using Discovery.Api.Controllers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

[TestFixture]
public class GodClassRefactorParityTests
{
    [Test]
    public void AgentAuth_SplitControllers_PreserveUniqueRoutes_AndNarrowDependencies()
    {
        var agentAuthControllers = typeof(AgentAuthController).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                        && typeof(AgentAuthControllerBase).IsAssignableFrom(t))
            .ToList();

        Assert.That(agentAuthControllers, Has.Count.EqualTo(7),
            "Expected 7 concrete AgentAuth feature controllers after God class split.");

        var routePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var controller in agentAuthControllers)
        {
            var routeAttr = controller.GetCustomAttribute<RouteAttribute>(inherit: true);
            Assert.That(routeAttr?.Template, Is.EqualTo("api/v{version:apiVersion}/agent-auth"),
                $"{controller.Name} must share the exact agent-auth route prefix.");

            var publicCtors = controller.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(publicCtors, Has.Length.EqualTo(1),
                $"{controller.Name} must expose a single unambiguous DI constructor.");
            Assert.That(publicCtors[0].GetParameters().Length, Is.LessThanOrEqualTo(8),
                $"{controller.Name} constructor should remain narrow after decomposition.");

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var httpAttrs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).ToList();
                foreach (var attr in httpAttrs)
                {
                    foreach (var httpMethod in attr.HttpMethods)
                    {
                        var key = $"{httpMethod} {attr.Template}";
                        Assert.That(routePairs.Add(key), Is.True,
                            $"Duplicate agent-auth route detected after split: {key} on {controller.Name}.{method.Name}");
                    }
                }
            }
        }

        // All 68 original [Http*] actions on AgentAuthController preserved without duplicates.
        Assert.That(routePairs.Count, Is.EqualTo(68));
    }

    [Test]
    public void AiChatStreamingOrchestrator_HasSinglePublicDiConstructor_AndResolvesFromDi()
    {
        var publicCtors = typeof(AiChatStreamingOrchestrator)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.That(publicCtors, Has.Length.EqualTo(1),
            "AiChatStreamingOrchestrator must expose exactly 1 public constructor for DI.");
        Assert.That(publicCtors[0].GetParameters().Length, Is.EqualTo(3));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<ILogger<AiChatService>>(NullLogger<AiChatService>.Instance);
        services.AddScoped(_ => CreateNullProxy<IAiChatSessionRepository>());
        services.AddScoped(_ => CreateNullProxy<IAiChatMessageRepository>());
        services.AddScoped(_ => CreateNullProxy<IAgentRepository>());
        services.AddScoped(_ => CreateNullProxy<ISiteRepository>());
        services.AddScoped(_ => CreateNullProxy<ILlmProvider>());
        services.AddScoped(_ => CreateNullProxy<IMcpToolExecutor>());
        services.AddScoped(_ => CreateNullProxy<IMcpToolGovernance>());
        services.AddScoped(_ => CreateNullProxy<IAiTokenBudgetResolver>());
        services.AddScoped(_ => CreateNullProxy<IAiModelCatalogService>());
        services.AddScoped(_ => CreateNullProxy<IMcpToolPolicyRepository>());
        services.AddScoped(_ => CreateNullProxy<IKnowledgeChunkRepository>());
        services.AddScoped(_ => CreateNullProxy<IEmbeddingProvider>());
        services.AddScoped<AiChatSystemPromptBuilder>();
        services.AddScoped<AiChatToolOrchestrator>();
        services.AddScoped<AiChatQuickReply>();
        services.AddScoped<AiChatCapabilityResolver>();
        services.AddScoped<AiChatToolRateLimiter>();
        services.AddScoped<AiChatSingleRoundStreamer>();
        services.AddScoped<AiChatMultiRoundStreamer>();
        services.AddScoped<AiChatStreamingOrchestrator>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        var orchestrator = scope.ServiceProvider.GetRequiredService<AiChatStreamingOrchestrator>();
        Assert.That(orchestrator, Is.Not.Null);
    }

    private static T CreateNullProxy<T>() where T : class
        => DispatchProxy.Create<T, NoopProxy>()!;

    private class NoopProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
