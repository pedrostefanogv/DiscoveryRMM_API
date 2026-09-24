using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Regressão do merge de componentes de hardware (HTTP 400 em produção):
/// os parseadores do HardwareInventoryParser esperam um objeto raiz, mas o
/// merge passava o ARRAY isolado da propriedade — qualquer sync de hardware
/// lançava InvalidOperationException e o agent nunca persistia inventário.
///
/// O payload é montado por objetos anônimos (não JSON literal) para espelhar
/// o envelope real do agent: components com arrays por lista e inventoryRaw
/// como STRING contendo o JSON do inventário.
/// </summary>
public class AgentHardwareMergeTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    private sealed class FakeAgentRepository(Agent agent) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult<Agent?>(agent);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Agent>>(ids.Contains(agent.Id) ? [agent] : []);
        public Task<IEnumerable<Agent>> GetAllAsync() => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => Task.FromResult<IEnumerable<Agent>>([agent]);
        public Task<Agent> CreateAsync(Agent a) => Task.FromResult(agent);
        public Task UpdateAsync(Agent a) => Task.CompletedTask;
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ip) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([agent]);
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string hash, Guid clientId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    private sealed class CapturingHardwareRepository(AgentHardwareComponents existing) : IAgentHardwareRepository
    {
        public AgentHardwareComponents? LastComponents { get; private set; }
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => Task.FromResult<AgentHardwareInfo?>(null);
        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId) => Task.FromResult(existing);
        public Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, AgentHardwareInfo>>(
                new Dictionary<Guid, AgentHardwareInfo>());
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>>(
                new Dictionary<Guid, IReadOnlyList<DiskInfo>>());
        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null)
        {
            LastComponents = components;
            return Task.CompletedTask;
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static ReportAgentHardwareCommand BuildCommand(object payload)
    {
        var json = JsonSerializer.Serialize(payload, WebJson);
        var cmd = JsonSerializer.Deserialize<ReportAgentHardwareCommand>(json, WebJson);
        Assert.That(cmd, Is.Not.Null, "payload do agent deveria desserializar em ReportAgentHardwareCommand");
        return cmd!;
    }

    private static Task RunHandler(ReportAgentHardwareCommand cmd, IAgentHardwareRepository hardwareRepo)
        => new ReportAgentHardwareCommandHandler(
                new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "HOST" }),
                hardwareRepo)
            .Handle(cmd, CancellationToken.None);

    /// <summary>Inventário cru (vai como string em inventoryRaw).</summary>
    private static object InventoryRaw() => new
    {
        startupItems = new object[]
        {
            new { name = "SecurityHealth", path = "C:/Windows/system32/SecurityHealthSystray.exe", type = "registry", source = "HKLM Run", status = "enabled" },
            new { name = "ServicoX", path = "C:/svc.exe", type = "service", source = "Servico", status = "disabled", detail = "Automatico", hive = "HKLM" },
            new { name = "ItemDeOutroUsuario", path = "C:/other.exe", type = "registry", source = "HKCU Run", status = "enabled", username = "pedro", hive = "HKU:S-1-5-21-907735816-3234815851-748069289-1001" },
        },
        scheduledTasks = new object[]
        {
            new
            {
                taskPath = "/",
                taskName = "TarefaDiaria",
                state = "enabled",
                status = "Ready",
                author = "Contoso",
                actionPath = "C:/app.exe",
                actionArgs = "--run",
                triggerType = "daily",
                triggerDesc = "Diario as 03:00",
                nextRunTime = "2026-09-23T06:00:00Z",
                lastRunTime = "2026-09-22T06:00:00Z",
                lastResult = 0
            },
        },
    };

    private static object FullPayload() => new
    {
        agentId = AgentId,
        hostname = "HOST",
        displayName = "HOST",
        status = "Online",
        inventoryCollectedAt = "2026-09-22T19:00:00Z",
        components = new
        {
            disks = new object[] { new { driveLetter = "C:", label = "OS", fileSystem = "NTFS", totalSizeBytes = 1000, freeSpaceBytes = 500 } },
            networkAdapters = new object[] { new { name = "Ethernet", macAddress = "10:FF:E0:2A:93:23" } },
            memoryModules = new object[] { new { slot = "DIMM0", capacityBytes = 8589934592L } },
            printers = new object[] { new { name = "Impressora 1" } },
            listeningPorts = new object[] { new { processName = "svc.exe", processId = 10, protocol = "tcp", address = "0.0.0.0", port = 41080 } },
            openSockets = new object[] { new { processName = "svc.exe", processId = 10, protocol = "tcp", family = "2", localPort = 5000, remotePort = 443 } },
        },
        inventoryRaw = JsonSerializer.Serialize(InventoryRaw(), WebJson),
    };

    [Test]
    public async Task FullAgentPayload_MergesAllComponentLists()
    {
        var hardwareRepo = new CapturingHardwareRepository(new AgentHardwareComponents());
        var cmd = BuildCommand(FullPayload());

        Assert.DoesNotThrowAsync(async () => await RunHandler(cmd, hardwareRepo));

        var components = hardwareRepo.LastComponents;
        Assert.That(components, Is.Not.Null, "componentes deveriam ter sido persistidos");
        Assert.Multiple(() =>
        {
            Assert.That(components!.Disks, Has.Count.EqualTo(1));
            Assert.That(components.NetworkAdapters, Has.Count.EqualTo(1));
            Assert.That(components.MemoryModules, Has.Count.EqualTo(1));
            Assert.That(components.Printers, Has.Count.EqualTo(1));
            Assert.That(components.ListeningPorts, Has.Count.EqualTo(1));
            Assert.That(components.OpenSockets, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task InventoryRawOnly_ExtractsStartupItemsAndScheduledTasks()
    {
        var hardwareRepo = new CapturingHardwareRepository(new AgentHardwareComponents());
        var cmd = BuildCommand(FullPayload());

        await RunHandler(cmd, hardwareRepo);

        var components = hardwareRepo.LastComponents!;
        Assert.Multiple(() =>
        {
            Assert.That(components.StartupItems, Has.Count.EqualTo(3), "startupItems do inventoryRaw");
            Assert.That(components.ScheduledTasks, Has.Count.EqualTo(1), "scheduledTasks do inventoryRaw");
            Assert.That(components.StartupItems[0].Source, Is.EqualTo("HKLM Run"));
            Assert.That(components.StartupItems[1].Status, Is.EqualTo("disabled"));
            Assert.That(components.StartupItems[1].Hive, Is.EqualTo("HKLM"));
            Assert.That(components.StartupItems[2].Hive, Is.EqualTo("HKU:S-1-5-21-907735816-3234815851-748069289-1001"));
            Assert.That(components.StartupItems[2].Username, Is.EqualTo("pedro"));
            Assert.That(components.ScheduledTasks[0].TaskName, Is.EqualTo("TarefaDiaria"));
            Assert.That(components.ScheduledTasks[0].TriggerDesc, Is.EqualTo("Diario as 03:00"));
            Assert.That(components.ScheduledTasks[0].Author, Is.EqualTo("Contoso"));
        });
    }

    [Test]
    public async Task PartialSync_PreservesListsNotReported()
    {
        var existing = new AgentHardwareComponents
        {
            Printers = [new PrinterInfo { Name = "Impressora antiga" }],
            StartupItems = [new StartupItemInfo { Name = "Item antigo", Type = "registry", Source = "HKCU Run", Status = "enabled" }],
            ScheduledTasks = [new ScheduledTaskInfo { TaskName = "Tarefa antiga", State = "enabled" }],
        };
        var hardwareRepo = new CapturingHardwareRepository(existing);

        // Sync parcial (refresh on-demand de portas/sockets): só essas listas vão.
        var payload = new
        {
            agentId = AgentId,
            hostname = "HOST",
            status = "Online",
            inventoryCollectedAt = "2026-09-22T19:05:00Z",
            components = new
            {
                listeningPorts = new object[] { new { processName = "svc.exe", processId = 10, protocol = "tcp", address = "0.0.0.0", port = 41080 } },
                openSockets = Array.Empty<object>(),
            },
        };
        var cmd = BuildCommand(payload);

        await RunHandler(cmd, hardwareRepo);

        var components = hardwareRepo.LastComponents!;
        Assert.Multiple(() =>
        {
            Assert.That(components.ListeningPorts, Has.Count.EqualTo(1));
            Assert.That(components.OpenSockets, Is.Empty);
            // Listas ausentes no payload não podem ser apagadas pelo sync parcial.
            Assert.That(components.Printers, Has.Count.EqualTo(1));
            Assert.That(components.StartupItems, Has.Count.EqualTo(1));
            Assert.That(components.ScheduledTasks, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// Payload hostil: "hardware" vem como ARRAY (não objeto). Antes o handler
    /// chamava TryGetProperty no array e estourava a mesma InvalidOperationException
    /// ("requires an element of type 'Object'"). Deve ser ignorado sem quebrar o merge.
    /// </summary>
    [Test]
    public async Task HardwareAsArray_DoesNotThrow_AndStillMergesComponents()
    {
        var hardwareRepo = new CapturingHardwareRepository(new AgentHardwareComponents());
        var payload = new
        {
            agentId = AgentId,
            hostname = "HOST",
            status = "Online",
            hardware = new object[] { new { manufacturer = "ACME" } },
            inventoryCollectedAt = "2026-09-22T19:15:00Z",
            components = new
            {
                startupItems = new object[]
                {
                    new { name = "Item", type = "registry", source = "HKLM Run", status = "enabled" },
                },
            },
        };
        var cmd = BuildCommand(payload);

        Assert.DoesNotThrowAsync(async () => await RunHandler(cmd, hardwareRepo));
        Assert.That(hardwareRepo.LastComponents!.StartupItems, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Payload hostil: "components" vem como ARRAY em vez de objeto.
    /// </summary>
    [Test]
    public async Task ComponentsAsArray_DoesNotThrow()
    {
        var hardwareRepo = new CapturingHardwareRepository(new AgentHardwareComponents());
        var payload = new
        {
            agentId = AgentId,
            hostname = "HOST",
            status = "Online",
            components = Array.Empty<object>(),
        };
        var cmd = BuildCommand(payload);

        Assert.DoesNotThrowAsync(async () => await RunHandler(cmd, hardwareRepo));
    }

    /// <summary>
    /// inventoryRaw como ARRAY (JSON inválido para o contrato): deve ser ignorado,
    /// não lançar "requires an element of type 'Object'".
    /// </summary>
    [Test]
    public async Task InventoryRawAsArray_DoesNotThrow()
    {
        var hardwareRepo = new CapturingHardwareRepository(new AgentHardwareComponents());
        var payload = new
        {
            agentId = AgentId,
            hostname = "HOST",
            status = "Online",
            inventoryRaw = new object[] { new { name = "x" } },
        };
        var cmd = BuildCommand(payload);

        Assert.DoesNotThrowAsync(async () => await RunHandler(cmd, hardwareRepo));
    }
}
