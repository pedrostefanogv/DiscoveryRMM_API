using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do caminho de UPDATE do inventário de hardware.
///
/// Histórico: a whitelist do <c>UpsertAsync</c> recopia campo a campo de uma
/// entidade DESTACADA — e as colunas de identidade de SO (M272) e de formato
/// físico (M294) tinham ficado FORA dela. Consequência: para agentes cujo
/// registro de hardware já existia (toda a frota instalada), is_laptop e
/// os_is_server ficariam NULL para sempre, e o painel nunca mostraria o selo de
/// notebook nem o bloco de energia.
///
/// Agravante: o agente envia payloads PARCIAIS (só Components, sem o objeto
/// "hardware") nos syncs de portas e de tarefas agendadas. Por isso a cópia dos
/// campos nullable é CONDICIONAL — copiar null apagaria o valor aprendido a
/// cada sync parcial.
/// </summary>
[TestFixture]
public class AgentHardwareRepositoryUpsertTests
{
    /// <summary>Contexto restrito a AgentHardwareInfo (o modelo completo não sobe no InMemory).</summary>
    private sealed class HardwareTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(Agent).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(AgentHardwareInfo)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<AgentHardwareInfo>(entity => entity.HasKey(info => info.Id));
        }
    }

    private static (DbContextOptions<DiscoveryDbContext> Options, string Name) NewOptions()
    {
        var name = $"hardware-upsert-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return (options, name);
    }

    private static AgentHardwareInfo Hardware(Guid agentId) => new()
    {
        AgentId = agentId,
        InventoryCollectedAt = DateTime.UtcNow,
    };

    [Test]
    public async Task Upsert_AtualizaColunasDeFormatoFisicoEIdentidade()
    {
        var (options, _) = NewOptions();
        var agentId = Guid.NewGuid();

        using (var db = new HardwareTestDbContext(options))
        {
            var repo = new AgentHardwareRepository(db);
            // Primeira sincronização: agente antigo, sem os campos.
            await repo.UpsertAsync(Hardware(agentId));

            // Segunda: agente atualizado reporta o formato físico.
            var reported = Hardware(agentId);
            reported.IsLaptop = true;
            reported.MachineType = "notebook";
            reported.PcSystemType = 2;
            reported.ChassisType = 10;
            reported.IsServer = false;
            reported.ProductType = "1";
            await repo.UpsertAsync(reported);
        }

        using (var db = new HardwareTestDbContext(options))
        {
            var stored = await db.AgentHardwareInfos.AsNoTracking().SingleAsync(info => info.AgentId == agentId);

            Assert.Multiple(() =>
            {
                Assert.That(stored.IsLaptop, Is.True, "o update precisa gravar is_laptop (M294)");
                Assert.That(stored.MachineType, Is.EqualTo("notebook"));
                Assert.That(stored.PcSystemType, Is.EqualTo(2));
                Assert.That(stored.ChassisType, Is.EqualTo(10));
                Assert.That(stored.IsServer, Is.False, "as colunas de SO (M272) também precisam ser copiadas");
                Assert.That(stored.ProductType, Is.EqualTo("1"));
            });
        }
    }

    /// <summary>
    /// Sync parcial (portas/tarefas: só Components) NÃO pode apagar o formato
    /// físico já aprendido — senão o selo de notebook desapareceria da listagem
    /// a cada sync de portas.
    /// </summary>
    [Test]
    public async Task Upsert_SyncParcial_NaoApagaFormatoFisicoNemIdentidade()
    {
        var (options, _) = NewOptions();
        var agentId = Guid.NewGuid();

        using (var db = new HardwareTestDbContext(options))
        {
            var repo = new AgentHardwareRepository(db);

            var reported = Hardware(agentId);
            reported.IsLaptop = true;
            reported.MachineType = "notebook";
            reported.IsServer = true;
            reported.ProductType = "3";
            await repo.UpsertAsync(reported);

            // Payload parcial: nenhum campo de hardware, apenas componentes.
            var partial = Hardware(agentId);
            partial.InventoryCollectedAt = DateTime.UtcNow.AddMinutes(5);
            await repo.UpsertAsync(partial, new AgentHardwareComponents
            {
                ListeningPorts =
                [
                    new ListeningPortInfo
                    {
                        Id = Guid.NewGuid(),
                        AgentId = agentId,
                        ProcessName = "svc",
                        Port = 80
                    }
                ]
            });
        }

        using (var db = new HardwareTestDbContext(options))
        {
            var stored = await db.AgentHardwareInfos.AsNoTracking().SingleAsync(info => info.AgentId == agentId);

            Assert.Multiple(() =>
            {
                Assert.That(stored.IsLaptop, Is.True, "sync parcial não pode apagar is_laptop");
                Assert.That(stored.MachineType, Is.EqualTo("notebook"));
                Assert.That(stored.IsServer, Is.True, "sync parcial não pode apagar a identidade de SO");
                Assert.That(stored.ProductType, Is.EqualTo("3"));
            });
        }
    }

    /// <summary>
    /// false reportado é valor POSITIVO (desktop) e precisa vencer o true
    /// anterior — só null preserva o valor armazenado.
    /// </summary>
    [Test]
    public async Task Upsert_FalseExplicitoVenceTrueAnterior()
    {
        var (options, _) = NewOptions();
        var agentId = Guid.NewGuid();

        using (var db = new HardwareTestDbContext(options))
        {
            var repo = new AgentHardwareRepository(db);

            var notebook = Hardware(agentId);
            notebook.IsLaptop = true;
            await repo.UpsertAsync(notebook);

            var desktop = Hardware(agentId);
            desktop.IsLaptop = false;
            desktop.MachineType = "desktop";
            await repo.UpsertAsync(desktop);
        }

        using (var db = new HardwareTestDbContext(options))
        {
            var stored = await db.AgentHardwareInfos.AsNoTracking().SingleAsync(info => info.AgentId == agentId);

            Assert.Multiple(() =>
            {
                Assert.That(stored.IsLaptop, Is.False, "false é resposta positiva (desktop) e deve vencer o true anterior");
                Assert.That(stored.MachineType, Is.EqualTo("desktop"));
            });
        }
    }

    /// <summary>
    /// A lista de baterias vive no JSON de componentes: precisa sobreviver ao
    /// round-trip do repositório (gravar serializa, ler desserializa).
    /// </summary>
    [Test]
    public async Task Upsert_PersisteBateriasNoJsonDeComponentes()
    {
        var (options, _) = NewOptions();
        var agentId = Guid.NewGuid();

        using (var db = new HardwareTestDbContext(options))
        {
            var repo = new AgentHardwareRepository(db);
            await repo.UpsertAsync(Hardware(agentId), new AgentHardwareComponents
            {
                Batteries =
                [
                    new AgentBatteryInfo
                    {
                        Id = Guid.NewGuid(),
                        AgentId = agentId,
                        Manufacturer = "SMP",
                        Model = "L19M3PD6",
                        PercentRemaining = 63,
                        OnAC = false,
                        Present = true,
                        Charging = false,
                        DesignedCapacityMAh = 45000,
                        MaxCapacityMAh = 41000,
                        CycleCount = 212,
                        CollectedAt = DateTime.UtcNow
                    }
                ]
            });
        }

        using (var db = new HardwareTestDbContext(options))
        {
            var repo = new AgentHardwareRepository(db);
            var components = await repo.GetComponentsAsync(agentId);

            Assert.That(components.Batteries, Has.Count.EqualTo(1));
            var battery = components.Batteries[0];
            Assert.Multiple(() =>
            {
                Assert.That(battery.Manufacturer, Is.EqualTo("SMP"));
                Assert.That(battery.PercentRemaining, Is.EqualTo(63));
                Assert.That(battery.OnAC, Is.False);
                Assert.That(battery.MaxCapacityMAh, Is.EqualTo(41000));
            });
        }
    }
}
