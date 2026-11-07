using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Cqrs.Agents.Inventory.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;
// Existem DOIS GetAgentHardwareQueryHandler (envelope do agente e DTO do site).
// Os testes do DTO usam o do site, nomeado explicitamente.
using SiteGetAgentHardwareQuery = Discovery.Core.Cqrs.Agents.Inventory.Queries.GetAgentHardwareQuery;
using SiteGetAgentHardwareQueryHandler = Discovery.Infrastructure.Cqrs.Agents.QueryHandlers.GetAgentHardwareQueryHandler;

namespace Discovery.Tests;

/// <summary>
/// Persistência da identidade do SO no inventário de hardware (Windows Server).
///
/// Histórico do defeito: o agente Go já enviava <c>osEdition</c> no envelope de
/// hardware, mas o handler de ingestão parava de ler em <c>osArchitecture</c> —
/// o dado chegava no fio e era descartado (0 ocorrências de "Edition" no C#).
///
/// Estes testes cobrem os dois lados da retrocompatibilidade:
///   (a) payload NOVO (agente atualizado) popula os seis campos e eles chegam
///       ao DTO que o site consome;
///   (b) payload ANTIGO (sem os campos) não quebra e deixa tudo null, para o
///       site exibir "desconhecido" em vez de mentir "não é servidor".
/// </summary>
public class AgentOsIdentityPersistenceTests
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
        public Task ApproveZeroTouchAsync(Guid agentId) => Task.CompletedTask;
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => Task.CompletedTask;
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string hash, Guid clientId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Agent>>([]);
    }

    /// <summary>Repositório que guarda o AgentHardwareInfo passado ao Upsert.</summary>
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

    private static ReportAgentHardwareCommand BuildCommand(object payload)
    {
        var json = JsonSerializer.Serialize(payload, WebJson);
        var cmd = JsonSerializer.Deserialize<ReportAgentHardwareCommand>(json, WebJson);
        Assert.That(cmd, Is.Not.Null, "payload do agent deveria desserializar em ReportAgentHardwareCommand");
        return cmd!;
    }

    /// <summary>Roda o handler de ingestão e devolve o hardware persistido.</summary>
    private static async Task<AgentHardwareInfo> IngestAsync(object hardwarePayload)
    {
        var hardwareRepo = new CapturingHardwareRepository();
        var handler = new ReportAgentHardwareCommandHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "SRVHOST" }),
            hardwareRepo);

        var cmd = BuildCommand(new
        {
            agentId = AgentId,
            hostname = "SRVHOST",
            status = "Online",
            hardware = hardwarePayload,
            inventoryCollectedAt = "2026-11-06T12:00:00Z",
        });

        await handler.Handle(cmd, CancellationToken.None);
        Assert.That(hardwareRepo.LastHardware, Is.Not.Null, "o handler deveria ter chamado UpsertAsync");
        return hardwareRepo.LastHardware!;
    }

    // ─────────────────────────────────────────────────────────────────────
    // (a) Payload NOVO: agente atualizado envia os seis campos.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PayloadNovo_PersisteIdentidadeDoSoDeServidor()
    {
        var info = await IngestAsync(new
        {
            manufacturer = "Dell Inc.",
            model = "PowerEdge R650",
            osName = "Windows Server 2022 Datacenter",
            osVersion = "10.0.20348",
            osBuild = "20348",
            osArchitecture = "x64",
            // Nomes acordados com o agente Go (F1/F2/F4).
            osEdition = "Windows Server 2022 Datacenter",
            osIsServer = true,
            osProductType = "3",
            osDomain = "corp.contoso.local",
            osWorkgroup = (string?)null,
            osPartOfDomain = true,
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.OsEdition, Is.EqualTo("Windows Server 2022 Datacenter"));
            Assert.That(info.IsServer, Is.True);
            Assert.That(info.ProductType, Is.EqualTo("3"));
            Assert.That(info.Domain, Is.EqualTo("corp.contoso.local"));
            Assert.That(info.Workgroup, Is.Null);
            Assert.That(info.PartOfDomain, Is.True);

            // Os campos preexistentes continuam funcionando.
            Assert.That(info.OsName, Is.EqualTo("Windows Server 2022 Datacenter"));
            Assert.That(info.OsArchitecture, Is.EqualTo("x64"));
            Assert.That(info.Model, Is.EqualTo("PowerEdge R650"));
        });
    }

    /// <summary>
    /// Controlador de domínio: ProductType 2 e partOfDomain true — é o caso que
    /// o ícone/filtro de servidor precisa distinguir de uma estação.
    /// </summary>
    [Test]
    public async Task PayloadNovo_ControladorDeDominio_PersisteProductTypeComoTexto()
    {
        var info = await IngestAsync(new
        {
            osName = "Windows Server 2019 Standard",
            osIsServer = true,
            osProductType = 2,
            osDomain = "ad.empresa.com.br",
            osPartOfDomain = true,
            osEdition = "Windows Server 2019 Standard",
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsServer, Is.True);
            Assert.That(info.ProductType, Is.EqualTo("2"), "número JSON deve ser preservado como texto");
            Assert.That(info.Domain, Is.EqualTo("ad.empresa.com.br"));
            Assert.That(info.PartOfDomain, Is.True);
        });
    }

    /// <summary>Estação fora de domínio: servidor = false e workgroup preenchido.</summary>
    [Test]
    public async Task PayloadNovo_EstacaoForaDeDominio_PersisteWorkgroup()
    {
        var info = await IngestAsync(new
        {
            osName = "Windows 11 Pro",
            osEdition = "Windows 11 Pro",
            osIsServer = false,
            osProductType = "1",
            osWorkgroup = "WORKGROUP",
            osPartOfDomain = false,
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsServer, Is.False, "false reportado deve ser preservado (não confundir com null)");
            Assert.That(info.ProductType, Is.EqualTo("1"));
            Assert.That(info.Workgroup, Is.EqualTo("WORKGROUP"));
            Assert.That(info.PartOfDomain, Is.False);
            Assert.That(info.Domain, Is.Null);
        });
    }

    /// <summary>
    /// Tolerância de contrato: booleano enviado como texto ("true"/"0") e
    /// variações camelCase sem o prefixo "os". Documentado no handler.
    /// </summary>
    [Test]
    public async Task PayloadNovo_AceitaBooleanoEmTextoEVariacoesCamelCase()
    {
        var info = await IngestAsync(new
        {
            osName = "Windows Server 2025",
            osIsServer = "true",
            osPartOfDomain = "0",
            productType = "3",
            domain = "lab.local",
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.IsServer, Is.True);
            Assert.That(info.PartOfDomain, Is.False);
            Assert.That(info.ProductType, Is.EqualTo("3"), "variação 'productType' sem prefixo os deve ser aceita");
            Assert.That(info.Domain, Is.EqualTo("lab.local"), "variação 'domain' sem prefixo os deve ser aceita");
        });
    }

    /// <summary>Valores maiores que a coluna são cortados, não derrubam o INSERT.</summary>
    [Test]
    public async Task PayloadNovo_TruncaValoresAcimaDoLimiteDaColuna()
    {
        var info = await IngestAsync(new
        {
            osEdition = new string('E', 300),
            osProductType = new string('P', 90),
            osDomain = new string('D', 400),
            osWorkgroup = new string('W', 400),
        });

        Assert.Multiple(() =>
        {
            Assert.That(info.OsEdition, Has.Length.EqualTo(100));
            Assert.That(info.ProductType, Has.Length.EqualTo(50));
            Assert.That(info.Domain, Has.Length.EqualTo(255));
            Assert.That(info.Workgroup, Has.Length.EqualTo(255));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // (b) Payload ANTIGO: agente sem os campos novome.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PayloadAntigo_NaoQuebraEDeixaCamposNulos()
    {
        var info = await IngestAsync(new
        {
            // Exatamente o conjunto que o agente emitia antes do F1/F2/F4.
            manufacturer = "Positivo",
            model = "Master D570",
            osName = "Windows 10 Pro",
            osVersion = "10.0.19045",
            osBuild = "19045",
            osArchitecture = "x64",
        });

        Assert.Multiple(() =>
        {
            // NULL = "não reportado"; o site deve exibir desconhecido, nunca
            // inferir "não é servidor".
            Assert.That(info.IsServer, Is.Null);
            Assert.That(info.OsEdition, Is.Null);
            Assert.That(info.ProductType, Is.Null);
            Assert.That(info.Domain, Is.Null);
            Assert.That(info.Workgroup, Is.Null);
            Assert.That(info.PartOfDomain, Is.Null);

            // O que já existia continua sendo persistido.
            Assert.That(info.OsName, Is.EqualTo("Windows 10 Pro"));
            Assert.That(info.OsBuild, Is.EqualTo("19045"));
        });
    }

    /// <summary>Sync sem objeto "hardware" nenhum não pode lançar.</summary>
    [Test]
    public async Task PayloadAntigo_SemHardware_NaoQuebra()
    {
        var hardwareRepo = new CapturingHardwareRepository();
        var handler = new ReportAgentHardwareCommandHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "HOST" }),
            hardwareRepo);

        var cmd = BuildCommand(new
        {
            agentId = AgentId,
            hostname = "HOST",
            status = "Online",
        });

        await Assert.DoesNotThrowAsync(() => handler.Handle(cmd, CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────
    // (c) DTO exposto ao site.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O DTO que o site consome precisa carregar os campos — senão o frontend
    /// não tem como desenhar o ícone de servidor nem filtrar.
    /// </summary>
    [Test]
    public async Task DtoDoSite_ExpoeOsSeisCampos()
    {
        var hardware = new AgentHardwareInfo
        {
            AgentId = AgentId,
            Manufacturer = "Dell Inc.",
            Model = "PowerEdge R650",
            OsName = "Windows Server 2022 Datacenter",
            OsEdition = "Windows Server 2022 Datacenter",
            IsServer = true,
            ProductType = "3",
            Domain = "corp.contoso.local",
            PartOfDomain = true,
        };

        var hardwareRepo = new CapturingHardwareRepository();
        await hardwareRepo.UpsertAsync(hardware);

        var handler = new SiteGetAgentHardwareQueryHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "SRVHOST" }),
            hardwareRepo);

        var result = await handler.Handle(new SiteGetAgentHardwareQuery(AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var dto = result.Value;
        Assert.Multiple(() =>
        {
            Assert.That(dto.OsEdition, Is.EqualTo("Windows Server 2022 Datacenter"));
            Assert.That(dto.IsServer, Is.True);
            Assert.That(dto.ProductType, Is.EqualTo("3"));
            Assert.That(dto.Domain, Is.EqualTo("corp.contoso.local"));
            Assert.That(dto.Workgroup, Is.Null);
            Assert.That(dto.PartOfDomain, Is.True);
        });

        // E o JSON entregue ao site usa as chaves camelCase esperadas.
        var json = JsonSerializer.Serialize(dto, WebJson);
        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"osEdition\""));
            Assert.That(json, Does.Contain("\"isServer\""));
            Assert.That(json, Does.Contain("\"productType\""));
            Assert.That(json, Does.Contain("\"domain\""));
            Assert.That(json, Does.Contain("\"workgroup\""));
            Assert.That(json, Does.Contain("\"partOfDomain\""));
        });
    }

    /// <summary>
    /// Agente antigo visto pelo site: campos ausentes chegam como null, e o DTO
    /// continua serializável.
    /// </summary>
    [Test]
    public async Task DtoDoSite_AgenteAntigo_CamposNulos()
    {
        var hardware = new AgentHardwareInfo
        {
            AgentId = AgentId,
            Manufacturer = "Positivo",
            Model = "Master D570",
            OsName = "Windows 10 Pro",
        };

        var hardwareRepo = new CapturingHardwareRepository();
        await hardwareRepo.UpsertAsync(hardware);

        var handler = new SiteGetAgentHardwareQueryHandler(
            new FakeAgentRepository(new Agent { Id = AgentId, Hostname = "HOST" }),
            hardwareRepo);

        var result = await handler.Handle(new SiteGetAgentHardwareQuery(AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Value.IsServer, Is.Null);
            Assert.That(result.Value.OsEdition, Is.Null);
            Assert.That(result.Value.ProductType, Is.Null);
            Assert.That(result.Value.PartOfDomain, Is.Null);
        });
    }
}