using System.Reflection;
using Discovery.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Tests;

/// <summary>
/// BUG 2026-11-21 (tupa.pstefano.com.br): o portal chama
/// POST /api/v1/nats-auth/user/credentials desde sempre
/// (DiscoveryRMM_Site/src/api/nats.ts) e a API respondia 404 — a rota nunca
/// existiu. Consequencia: o dashboard mostrava "Falha de rede NATS"/"Offline"
/// com o broker, o nginx e o tunnel saudaveis (o painel "Testar" respondia
/// "Conectado").
///
/// Este teste trava o CONTRATO da rota: se alguem renomear o metodo ou mudar o
/// verbo/path, o portal perde a conexao de novo — em silencio.
/// </summary>
public class NatsAuthControllerRouteTests
{
    private static (string? Template, string[] Verbs) RouteOf(string methodName)
    {
        var method = typeof(NatsAuthController).GetMethod(methodName);
        Assert.That(method, Is.Not.Null, $"metodo {methodName} nao existe no NatsAuthController");

        var http = method!.GetCustomAttribute<HttpPostAttribute>();
        var verbs = http is null ? Array.Empty<string>() : new[] { "POST" };
        return (http?.Template, verbs);
    }

    [Test]
    public void IssueUserCredentials_IsAPostAtUserCredentials()
    {
        var (template, verbs) = RouteOf(nameof(NatsAuthController.IssueUserCredentials));

        Assert.That(verbs, Does.Contain("POST"),
            "o portal faz POST em /api/v1/nats-auth/user/credentials");
        Assert.That(template, Is.EqualTo("user/credentials"),
            "o path relativo precisa continuar sendo user/credentials");
    }

    [Test]
    public void GetStatus_StillExists()
    {
        var method = typeof(NatsAuthController).GetMethod(nameof(NatsAuthController.GetStatus));
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.GetCustomAttribute<HttpGetAttribute>(), Is.Not.Null);
    }
}
