using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Persistência do FORMATO FÍSICO da máquina (notebook × desktop × tablet ×
/// servidor) no inventário de hardware — migration M294.
///
/// Estes testes cobrem os dois lados da retrocompatibilidade:
///   (a) payload NOVO (agente atualizado) popula isLaptop/machineType e os
///       sinais brutos (chassisType/pcSystemType);
///   (b) payload ANTIGO (ou VM sem WMI) deixa tudo NULL — o site NÃO mostra
///       selo, em vez de afirmar "não é notebook".
///
/// NULL nunca pode virar false: false é resposta positiva de "é desktop".
/// </summary>
public class AgentMachineTypePersistenceTests
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

    private sealed class CapturingHardwareRepository : IAgentHardwareRepository
    {
        public AgentHardwareInfo? LastHardware { get; private set; }
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId) => Task.FromResult<AgentHardwareInfo?>(LastHardware);
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

    private static async Task<AgentHardwareInfo> IngestAsync(object hardwarePayload)
    {
        var hardwareRepo = new CapturingHardwareRepository();
        var handler = new ReportAgentHardwareCommandHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "NOTE-ANA" }),
            hardwareRepo);

        var json = JsonSerializer.Serialize(new
        {
            agentId = AgentId,
            hostname = "NOTE-ANA",
            status = "Online",
            hardware = hardwarePayload,
            inventoryCollectedAt = "2026-11-23T12:00:00Z",
        }, WebJson);

        var cmd = JsonSerializer.Deserialize<ReportAgentHardwareCommand>(json, WebJson);
        Assert.That(cmd, Is.Not.Null, "payload do agent deveria desserializar em ReportAgentHardwareCommand");

        await handler.Handle(cmd!, CancellationToken.None);
        Assert.That(hardwareRepo.LastHardware, Is.Not.Null, "o handler deveria ter chamado UpsertAsync");
        return hardwareRepo.LastHardware!;
    }

    [Test]
    public async Task PayloadNovo_PersisteNotebookComSinaisBrutos()
    {
        var info = await IngestAsync(new
        {
            manufacturer = "Lenovo",
            model = "ThinkPad T14",
            // Nomes acordados com o agente Go (machine_profile_windows.go).
            isLaptop = true,
            machineType = "notebook",
            pcSystemType = 2,
            hasBattery = true,
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsLaptop, Is.True);
            Assert.That(info.MachineType, Is.EqualTo("notebook"));
            Assert.That(info.PcSystemType, Is.EqualTo(2));
            // O que já existia continua sendo persistido.
            Assert.That(info.Manufacturer, Is.EqualTo("Lenovo"));
        });
    }

    [Test]
    public async Task PayloadNovo_DesktopPersisteFalseExplicito()
    {
        var info = await IngestAsync(new
        {
            isLaptop = false,
            machineType = "desktop",
            pcSystemType = 1,
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsLaptop, Is.False, "false reportado é resposta positiva ('é desktop'), não ausência");
            Assert.That(info.MachineType, Is.EqualTo("desktop"));
        });
    }

    /// <summary>
    /// Tolerância de contrato: booleano como texto e aliases aceitos (agente de
    /// versão diferente não pode ter o campo descartado silenciosamente).
    /// </summary>
    [Test]
    public async Task PayloadNovo_AceitaTextoEAliases()
    {
        var info = await IngestAsync(new
        {
            isNotebook = "true",
            machine_type = "Desktop",
            pc_system_type = "1",
            chassisType = 10,
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsLaptop, Is.True);
            Assert.That(info.MachineType, Is.EqualTo("desktop"), "vocabulário é normalizado para minúsculo");
            Assert.That(info.PcSystemType, Is.EqualTo(1));
            Assert.That(info.ChassisType, Is.EqualTo(10));
        });
    }

    /// <summary>
    /// Valor fora do vocabulário (ou "unknown") é NÃO REPORTADO: não vira texto
    /// livre na coluna e NÃO sobrescreve o último tipo conhecido — agentes
    /// antigos enviam "unknown" quando o WMI falha, e gravar isso apagava o
    /// "notebook" já identificado (o painel mostrava "—").
    /// </summary>
    [TestCase("geladeira-inteligente")]
    [TestCase("unknown")]
    [TestCase("UNKNOWN")]
    public async Task PayloadNovo_MachineTypeForaDoVocabulario_NaoEhReportado(string machineType)
    {
        var info = await IngestAsync(new { machineType });

        Assert.That(info.MachineType, Is.Null);
    }

    [Test]
    public async Task PayloadAntigo_FormatoFisicoFicaNulo()
    {
        var info = await IngestAsync(new
        {
            // Conjunto que o agente emitia antes da M294.
            manufacturer = "Positivo",
            model = "Master D570",
            osName = "Windows 10 Pro",
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsLaptop, Is.Null, "null = não reportado; false significaria 'é desktop'");
            Assert.That(info.MachineType, Is.Null);
            Assert.That(info.ChassisType, Is.Null);
            Assert.That(info.PcSystemType, Is.Null);
        });
    }

    /// <summary>
    /// Windows Server: mesmo com bateria (UPS/gerenciador de energia), o agente
    /// envia isLaptop=false e o servidor preserva isso — o painel não pode
    /// marcar servidor como notebook.
    /// </summary>
    [Test]
    public async Task PayloadServidorComBateria_NaoViraNotebook()
    {
        var info = await IngestAsync(new
        {
            osName = "Windows Server 2022 Standard",
            osIsServer = true,
            isLaptop = false,
            machineType = "server",
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsServer, Is.True);
            Assert.That(info.IsLaptop, Is.False);
            Assert.That(info.MachineType, Is.EqualTo("server"));
        });
    }

    /// <summary>
    /// MachineType absurdo não estoura a coluna nem vira texto livre: é não
    /// reportado (null), então a linha anterior permanece intacta.
    /// </summary>
    [Test]
    public async Task PayloadNovo_MachineTypeLongo_NaoEstouraColuna()
    {
        var info = await IngestAsync(new { machineType = new string('X', 200) });

        Assert.That(info.MachineType, Is.Null);
    }
}
