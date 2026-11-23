using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Sync PARCIAL do inventário de hardware.
///
/// O agente tem dois caminhos que enviam APENAS <c>components</c>
/// (SyncNetworkConnections e SyncStartupAndScheduledTasks): sem o objeto
/// <c>hardware</c> e com <c>inventoryRaw: ""</c>. Como o UpsertAsync recopia
/// campo a campo, esses syncs APAGAVAM todo o hardware persistido (fabricante,
/// modelo, processador, SO, séries e o próprio inventoryRaw) — e o sync de
/// portas roda continuamente durante a vida do agente.
///
/// Contrato travado aqui: sync parcial NUNCA apaga hardware; sync completo
/// continua autoritativo e reflete mudanças legítimas.
/// </summary>
public class AgentHardwarePartialSyncTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

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
        public Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Agent?> GetByIdForDecommissionAsync(Guid id) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string hash, Guid clientId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    /// <summary>Repo que DEVOLVE o hardware já persistido e captura o novo upsert.</summary>
    private sealed class StoredHardwareRepository(AgentHardwareInfo? existing) : IAgentHardwareRepository
    {
        public AgentHardwareInfo? LastHardware { get; private set; }
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => Task.FromResult(existing);
        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId) => Task.FromResult(new AgentHardwareComponents());
        public Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, AgentHardwareInfo>>(new Dictionary<Guid, AgentHardwareInfo>());
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>>(new Dictionary<Guid, IReadOnlyList<DiskInfo>>());
        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null)
        {
            LastHardware = hardware;
            return Task.CompletedTask;
        }
    }

    private static AgentHardwareInfo StoredHardware() => new()
    {
        AgentId = AgentId,
        Manufacturer = "Lenovo",
        Model = "ThinkPad T14",
        Processor = "Intel Core i7-1260P",
        SerialNumber = "PF-123456",
        OsName = "Windows 11 Pro",
        IsServer = false,
        ProductType = "1",
        IsLaptop = true,
        MachineType = "notebook",
        InventoryRaw = "{\"battery\":[{\"percentRemaining\":63}]}",
        InventorySchemaVersion = "3",
        InventoryCollectedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc)
    };

    private static async Task<AgentHardwareInfo> RunAsync(StoredHardwareRepository repo, object payload)
    {
        var handler = new ReportAgentHardwareCommandHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "NOTE-ANA" }),
            repo);

        var json = JsonSerializer.Serialize(payload, WebJson);
        var cmd = JsonSerializer.Deserialize<ReportAgentHardwareCommand>(json, WebJson);
        Assert.That(cmd, Is.Not.Null);

        await handler.Handle(cmd!, CancellationToken.None);
        Assert.That(repo.LastHardware, Is.Not.Null, "o handler deveria ter chamado UpsertAsync");
        return repo.LastHardware!;
    }

    [Test]
    public async Task SyncParcialSoDeComponentes_NaoApagaHardware()
    {
        var repo = new StoredHardwareRepository(StoredHardware());

        // Exatamente o que SyncNetworkConnections envia: sem "hardware" e com
        // inventoryRaw vazio (o campo NÃO tem omitempty no envelope).
        var upserted = await RunAsync(repo, new
        {
            agentId = AgentId,
            status = "online",
            inventoryRaw = "",
            inventorySchemaVersion = "",
            inventoryCollectedAt = "2026-11-23T12:00:00Z",
            components = new
            {
                listeningPorts = new[] { new { processName = "svc", port = 80 } }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(upserted.Manufacturer, Is.EqualTo("Lenovo"), "sync parcial não pode apagar fabricante");
            Assert.That(upserted.Model, Is.EqualTo("ThinkPad T14"));
            Assert.That(upserted.Processor, Is.EqualTo("Intel Core i7-1260P"));
            Assert.That(upserted.SerialNumber, Is.EqualTo("PF-123456"));
            Assert.That(upserted.OsName, Is.EqualTo("Windows 11 Pro"));
            Assert.That(upserted.IsLaptop, Is.True, "o selo de notebook não pode sumir no sync de portas");
            Assert.That(upserted.MachineType, Is.EqualTo("notebook"));
            Assert.That(upserted.IsServer, Is.False);
            Assert.That(upserted.InventoryRaw, Is.EqualTo("{\"battery\":[{\"percentRemaining\":63}]}"),
                "inventoryRaw é o fallback de bateria/usuário — não pode ser apagado");
            Assert.That(upserted.InventoryCollectedAt, Is.EqualTo(StoredHardware().InventoryCollectedAt),
                "a coleta de hardware continua sendo a última cheia, não a do sync parcial");
        });
    }

    /// <summary>
    /// Sync COMPLETO continua autoritativo: valor reportado vence o anterior.
    /// </summary>
    [Test]
    public async Task SyncCompleto_AtualizaValoresReportados()
    {
        var repo = new StoredHardwareRepository(StoredHardware());

        var upserted = await RunAsync(repo, new
        {
            agentId = AgentId,
            status = "Online",
            inventoryRaw = "{\"hardware\":{}}",
            inventoryCollectedAt = "2026-11-23T12:00:00Z",
            hardware = new
            {
                osName = "Windows 10 Pro",
                manufacturer = "Dell Inc.",
                isLaptop = false,
                machineType = "desktop"
            },
            components = new { disks = Array.Empty<object>() }
        });

        Assert.Multiple(() =>
        {
            Assert.That(upserted.OsName, Is.EqualTo("Windows 10 Pro"), "sync completo deve refletir mudança real de SO");
            Assert.That(upserted.Manufacturer, Is.EqualTo("Dell Inc."));
            Assert.That(upserted.IsLaptop, Is.False, "false reportado vence o true anterior");
            Assert.That(upserted.MachineType, Is.EqualTo("desktop"));
            // Num sync COMPLETO o payload é a fonte da verdade: campo ausente
            // vira null (não há carry-over do estado anterior). É o contrato de
            // hoje; o sync parcial acima é o único que preserva o que não veio.
            Assert.That(upserted.Model, Is.Null);
        });
    }
}
