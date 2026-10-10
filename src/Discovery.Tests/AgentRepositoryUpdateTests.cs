using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug "busca global não acha o agent pelo usuário logado":
/// o handler de hardware/inventário calculava o usuário (campo explícito do
/// envelope ou inventoryRaw.loggedInUsers), atribuía em <c>agent.LoggedUser</c>
/// e chamava UpdateAsync — mas a whitelist de UpdateAsync recopia campo a campo
/// uma entidade DESTACADA e LoggedUser tinha ficado fora dela. Resultado:
/// agents.logged_user permanecia vazio para TODA a frota, o card exibia o
/// usuário ao vivo (heartbeat/Redis) e a busca — que casa justamente por essa
/// coluna — devolvia zero agentes.
/// </summary>
[TestFixture]
public class AgentRepositoryUpdateTests
{
    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"agent-update-{Guid.NewGuid():N}")
            .Options;
        return new AgentUpdateTestDbContext(options);
    }

    /// <summary>Contexto restrito a Agent (o modelo completo não sobe no provider InMemory).</summary>
    private sealed class AgentUpdateTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(Agent)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Agent>(entity => entity.HasKey(a => a.Id));
        }
    }

    private static Agent NewAgent() => new()
    {
        SiteId = Guid.NewGuid(),
        Hostname = "AORUSAXV2",
        DisplayName = "AORUSAXV2",
        Status = AgentStatus.Online,
        OperatingSystem = "Windows 11 Pro",
        OsVersion = "10.0 (25H2)",
        AgentVersion = "1.2.1",
        LastIpAddress = "192.168.10.77",
        MacAddress = "10:FF:E0:2A:93:23",
        LoggedUser = null,
        ZeroTouchPending = false,
    };

    [Test]
    public async Task UpdateAsync_PersistsLoggedUser()
    {
        await using var db = NewDb();
        var repo = new AgentRepository(db);
        var created = await repo.CreateAsync(NewAgent());

        // O handler busca com AsNoTracking e devolve uma entidade DESTACADA.
        var detached = await repo.GetByIdAsync(created.Id);
        Assert.That(detached, Is.Not.Null, "agent deveria existir");

        detached!.LoggedUser = "pedro";

        await repo.UpdateAsync(detached);

        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.That(reloaded, Is.Not.Null);
        Assert.That(reloaded!.LoggedUser, Is.EqualTo("pedro"),
            "o usuário logado reportado pelo agent precisa sobreviver ao UpdateAsync — " +
            "é a coluna que a busca global usa para achar o agent pelo usuário");
    }

    [Test]
    public async Task UpdateAsync_DoesNotClearLoggedUserWhenReportIsEmpty()
    {
        await using var db = NewDb();
        var repo = new AgentRepository(db);
        var agent = NewAgent();
        agent.LoggedUser = "pedro";
        var created = await repo.CreateAsync(agent);

        // Sync parcial (sem sessão) traz null: a API não deve apagar o último
        // usuário conhecido — o mesmo contrato garantido no handler.
        var detached = await repo.GetByIdAsync(created.Id);
        Assert.That(detached!.LoggedUser, Is.EqualTo("pedro"));

        await repo.UpdateAsync(detached);

        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.That(reloaded!.LoggedUser, Is.EqualTo("pedro"));
    }

    /// <summary>
    /// Rede de proteção para a CLASSE do bug acima: perturba TODAS as
    /// propriedades persistidas da entidade, chama UpdateAsync e exige que todas
    /// sobrevivam. Se alguém adicionar um campo a Agent e esquecer de copiá-lo na
    /// whitelist do repositório, este teste falha — foi exatamente assim que
    /// LoggedUser passou despercebido.
    /// </summary>
    [Test]
    public async Task UpdateAsync_PersistsEveryPersistedProperty()
    {
        await using var db = NewDb();
        var repo = new AgentRepository(db);
        var created = await repo.CreateAsync(NewAgent());

        var detached = await repo.GetByIdAsync(created.Id);
        Assert.That(detached, Is.Not.Null);

        // Campos gerenciados pelo próprio repositório (não vêm do payload).
        var managed = new HashSet<string> { "Id", "CreatedAt", "UpdatedAt" };
        var expected = new Dictionary<string, object?>();
        foreach (var prop in typeof(Agent).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite || managed.Contains(prop.Name))
                continue;

            var perturbed = Perturb(prop.PropertyType, prop.GetValue(detached));
            prop.SetValue(detached, perturbed);
            expected[prop.Name] = perturbed;
        }

        Assert.That(expected, Is.Not.Empty, "a entidade deveria ter campos persistidos");

        await repo.UpdateAsync(detached);

        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.That(reloaded, Is.Not.Null);
        foreach (var (name, value) in expected)
        {
            var actual = typeof(Agent).GetProperty(name)!.GetValue(reloaded);
            Assert.That(actual, Is.EqualTo(value),
                $"campo '{name}' não sobreviveu ao UpdateAsync — provavelmente falta na whitelist do repositório");
        }
    }

    private static object? Perturb(Type type, object? current)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        var effective = underlying ?? type;

        if (effective == typeof(bool))
            return !(bool)(current ?? false);
        if (effective == typeof(int))
            return (int)(current ?? 0) + 7;
        if (effective == typeof(string))
            return (current as string ?? "valor") + "-perturbado";
        if (effective == typeof(Guid))
            return Guid.NewGuid();
        if (effective == typeof(DateTime))
            return DateTime.UtcNow.AddMinutes(-11);
        if (effective.IsEnum)
        {
            var values = Enum.GetValues(effective).Cast<object>().ToList();
            var other = values.FirstOrDefault(v => !Equals(v, current)) ?? values[0];
            return other;
        }

        return current;
    }
}
