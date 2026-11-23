using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

namespace Discovery.Tests;

/// <summary>
/// Cobre a propagação da identidade da máquina do hardware para o AgentDto:
/// Windows Server (ícone de servidor) e formato físico — notebook × desktop ×
/// tablet × servidor (selo de notebook na listagem).
///
/// Contrato que este teste trava: NULL significa "não reportado" (agente antigo
/// ou inventário de hardware ainda não recebido), NUNCA "não é servidor" nem
/// "não é notebook". Se alguém trocar null por false no mapeamento, o site
/// passaria a pintar todo agente sem inventário como estação de trabalho e
/// esconderia o selo de notebook de máquinas sem coleta.
/// </summary>
[TestFixture]
public class AgentOsIdentityDtoTests
{
    private static Agent BuildAgent(Guid id) => new()
    {
        Id = id,
        SiteId = Guid.NewGuid(),
        Hostname = "SRV-DC01",
        OperatingSystem = "Windows Server 2022 Standard",
        Status = Discovery.Core.Enums.AgentStatus.Offline
    };

    /// <summary>Fake mínimo: só a consulta em lote é usada pelo mapeamento.</summary>
    private sealed class FakeHardwareRepo(IReadOnlyDictionary<Guid, AgentHardwareInfo> items) : IAgentHardwareRepository
    {
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId)
            => Task.FromResult(items.GetValueOrDefault(agentId));

        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId)
            => Task.FromResult(new AgentHardwareComponents());

        public Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(
            IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, AgentHardwareInfo>>(
                items.Where(kv => agentIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(
            IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>>(
                new Dictionary<Guid, IReadOnlyList<DiskInfo>>());

        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null)
            => Task.CompletedTask;
    }

    [Test]
    public async Task DomainController_ProdutoLanmanNT_ChegaNoDto()
    {
        var agentId = Guid.NewGuid();
        var repo = new FakeHardwareRepo(new Dictionary<Guid, AgentHardwareInfo>
        {
            [agentId] = new() { AgentId = agentId, IsServer = true, ProductType = "LanmanNT" }
        });

        var map = await AgentQueryHelper.GetMachineIdentityByAgentAsync(repo, new[] { agentId }, CancellationToken.None);

        Assert.That(map[agentId].IsServer, Is.True);
        Assert.That(map[agentId].ProductType, Is.EqualTo("LanmanNT"),
            "LanmanNT é o único jeito de o site distinguir controlador de domínio");
    }

    [Test]
    public async Task AgenteSemInventario_NaoApareceNoMapa_PreservandoNull()
    {
        var comInventario = Guid.NewGuid();
        var semInventario = Guid.NewGuid();
        var repo = new FakeHardwareRepo(new Dictionary<Guid, AgentHardwareInfo>
        {
            [comInventario] = new() { AgentId = comInventario, IsServer = false, ProductType = "WinNT" }
        });

        var map = await AgentQueryHelper.GetMachineIdentityByAgentAsync(
            repo, new[] { comInventario, semInventario }, CancellationToken.None);

        Assert.That(map.ContainsKey(comInventario), Is.True);
        Assert.That(map.ContainsKey(semInventario), Is.False,
            "agente sem hardware deve ficar FORA do mapa para o DTO manter null (desconhecido)");

        // E o DTO, ao receber o default, mantém null — nunca false.
        var dto = AgentQueryHelper.MapToDto(BuildAgent(semInventario), null, map.GetValueOrDefault(semInventario));
        Assert.That(dto.IsServer, Is.Null, "null = não reportado; false significaria 'é cliente'");
        Assert.That(dto.ProductType, Is.Null);
    }

    /// <summary>Repo que explode se for consultado — prova que lista vazia não faz query.</summary>
    private sealed class ExplodingHardwareRepo : IAgentHardwareRepository
    {
        public Task<AgentHardwareInfo?> GetByAgentIdAsync(Guid agentId)
            => throw new InvalidOperationException("não deveria ser chamado");

        public Task<AgentHardwareComponents> GetComponentsAsync(Guid agentId)
            => throw new InvalidOperationException("não deveria ser chamado");

        public Task<IReadOnlyDictionary<Guid, AgentHardwareInfo>> GetByAgentIdsAsync(
            IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => throw new InvalidOperationException("lista vazia não deve consultar o banco");

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<DiskInfo>>> GetDisksByAgentIdsAsync(
            IReadOnlyCollection<Guid> agentIds, CancellationToken ct = default)
            => throw new InvalidOperationException("não deveria ser chamado");

        public Task UpsertAsync(AgentHardwareInfo hardware, AgentHardwareComponents? components = null)
            => Task.CompletedTask;
    }

    [Test]
    public async Task ListaVazia_NaoConsultaRepositorio()
    {
        var map = await AgentQueryHelper.GetMachineIdentityByAgentAsync(
            new ExplodingHardwareRepo(), Array.Empty<Guid>(), CancellationToken.None);

        Assert.That(map, Is.Empty);
    }

    /// <summary>
    /// Notebook: isLaptop/machineType precisam chegar ao DTO para o site mostrar
    /// o selo na listagem.
    /// </summary>
    [Test]
    public async Task Notebook_ChegaNoDtoComMachineType()
    {
        var agentId = Guid.NewGuid();
        var repo = new FakeHardwareRepo(new Dictionary<Guid, AgentHardwareInfo>
        {
            [agentId] = new() { AgentId = agentId, IsServer = false, IsLaptop = true, MachineType = "notebook" }
        });

        var map = await AgentQueryHelper.GetMachineIdentityByAgentAsync(repo, new[] { agentId }, CancellationToken.None);
        var dto = AgentQueryHelper.MapToDto(BuildAgent(agentId), null, map[agentId]);

        Assert.That(dto.IsLaptop, Is.True);
        Assert.That(dto.MachineType, Is.EqualTo("notebook"));
    }

    /// <summary>
    /// Desktop é resposta POSITIVA (false explícito), não ausência de dado.
    /// </summary>
    [Test]
    public async Task Desktop_ChegaNoDtoComoFalseExplicito()
    {
        var agentId = Guid.NewGuid();
        var repo = new FakeHardwareRepo(new Dictionary<Guid, AgentHardwareInfo>
        {
            [agentId] = new() { AgentId = agentId, IsServer = false, IsLaptop = false, MachineType = "desktop" }
        });

        var map = await AgentQueryHelper.GetMachineIdentityByAgentAsync(repo, new[] { agentId }, CancellationToken.None);
        var dto = AgentQueryHelper.MapToDto(BuildAgent(agentId), null, map[agentId]);

        Assert.That(dto.IsLaptop, Is.False);
        Assert.That(dto.MachineType, Is.EqualTo("desktop"));
    }

    /// <summary>
    /// Sem inventário (ou agente antigo), isLaptop/machineType ficam NULL — o
    /// site não pinta selo nenhum. Este é o caso mais comum na frota real.
    /// </summary>
    [Test]
    public void AgenteSemInventario_IsLaptopFicaNull()
    {
        var dto = AgentQueryHelper.MapToDto(BuildAgent(Guid.NewGuid()), null, default);

        Assert.That(dto.IsLaptop, Is.Null, "null = não reportado; false significaria 'é desktop'");
        Assert.That(dto.MachineType, Is.Null);
    }
}
