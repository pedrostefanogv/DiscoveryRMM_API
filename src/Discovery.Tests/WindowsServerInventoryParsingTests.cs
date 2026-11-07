using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Parse do inventário de Windows Server em colunas tipadas.
///
/// O agente Go só coleta esses blocos quando a máquina é Windows Server (gate
/// por IsServer). Antes desta mudança os dados chegavam e ficavam presos no
/// JSON do InventoryRaw — gravados, mas não consultáveis/filtráveis.
///
/// O contrato coberto aqui é o MESMO travado pelo teste E2E do lado Go
/// (Discovery/src/app/inventory/contract_e2e_windows_test.go): as chaves
/// osRoles, serverHardware, hyperV, cluster e storageRds.
///
/// O parser é interno ao assembly de Infrastructure, por isso é acessado via
/// reflection (mesmo padrão de AgentHardwareInventoryParsingTests).
/// </summary>
public class WindowsServerInventoryParsingTests
{
    private static readonly Guid AgentId = Guid.Parse("019faa6f-8eaa-7c38-9878-36cc96f9be3e");
    private static readonly DateTime CollectedAt = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Payload COMPLETO de um servidor (todas as seções preenchidas).</summary>
    private static object FullServerPayload() => new
    {
        // Discos obrigatórios? Não: o teste do "só servidor" cobre a ausência.
        disks = new object[]
        {
            new { driveLetter = "C:", fileSystem = "NTFS", totalSizeBytes = 500_000_000_000L, freeSpaceBytes = 200_000_000_000L }
        },
        osName = "Windows Server 2022 Datacenter",
        osIsServer = true,
        osProductType = "3",

        osRoles = new object[]
        {
            new { name = "AD-Domain-Services", displayName = "Active Directory Domain Services", installed = true, installState = "Installed" },
            new { name = "DNS", displayName = "DNS Server", installed = true, installState = "Installed" },
            new { name = "Hyper-V", displayName = "Hyper-V", installed = false, installState = "Available" },
        },

        serverHardware = new
        {
            isServer = true,
            raidTools = new object[]
            {
                new
                {
                    name = "perccli", vendor = "Dell", path = @"C:\Program Files\perccli\perccli64.exe",
                    ok = true, controllers = 1, virtualDisks = 2, physicalDisks = 8,
                    degraded = new object[] { "Virtual Disk 1: Degraded" }
                },
                new { name = "storcli", vendor = "LSI", ok = false, error = "timeout ao executar" }
            },
            bmc = new object[] { new { name = "iDRAC", vendor = "Dell", path = "https://10.0.0.5" } },
            reason = ""
        },

        hyperV = new
        {
            enabled = true,
            vms = new object[]
            {
                new
                {
                    name = "SRV-DB01", state = "Running", stateCode = 2,
                    memoryAssignedMb = 16384, uptimeSeconds = 864000L, generation = 2,
                    integrationServicesVersion = "6.3.9600.0"
                },
                new { name = "SRV-APP01", state = "Off", stateCode = 3 }
            },
            reason = ""
        },

        cluster = new
        {
            present = true,
            clusters = new object[]
            {
                new
                {
                    name = "CLUSTER-PROD",
                    nodes = new object[]
                    {
                        new { name = "SRV-A", state = "Up", vote = "1" },
                        new { name = "SRV-B", state = "Up", vote = "1" }
                    },
                    resources = new object[]
                    {
                        new { name = "SQL Server", state = "Online", group = "SQL Group", type = "SQL Server" }
                    },
                    quorumType = "NodeAndDiskMajority",
                    quorumResource = "Cluster Disk 1",
                    degradedResources = 0
                }
            },
            reason = ""
        },

        storageRds = new
        {
            storagePools = new object[]
            {
                new
                {
                    name = "Primordial", healthStatus = "Healthy", operationalStatus = "OK",
                    sizeGb = 8000, allocatedGb = 0, diskCount = 4, isPrimordial = true
                },
                new
                {
                    name = "Pool-Dados", healthStatus = "Warning", operationalStatus = "Degraded",
                    sizeGb = 4000, allocatedGb = 3500, diskCount = 3, isPrimordial = false
                }
            },
            rdsHost = true,
            rdsSessions = new object[]
            {
                new { sessionId = 2, username = @"CORP\pedro", state = "Active", sessionType = "rdp-tcp" },
                new { sessionId = 3, username = @"CORP\ana", state = "Disconnected", sessionType = "rdp-tcp" }
            },
            reason = ""
        }
    };

    /// <summary>Payload de uma ESTAÇÃO de trabalho: nenhum bloco de servidor.</summary>
    private static object WorkstationPayload() => new
    {
        disks = new object[]
        {
            new { driveLetter = "C:", fileSystem = "NTFS", totalSizeBytes = 256_000_000_000L, freeSpaceBytes = 100_000_000_000L }
        },
        osName = "Windows 11 Pro",
        osIsServer = false,
        osProductType = "1",
    };

    private static AgentHardwareComponents? InvokeTryBuild(string inventoryRaw)
    {
        var parserType = typeof(HardwareInventoryParser);
        var method = parserType.GetMethod(
            "TryBuildFromInventoryRaw",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "não encontrei TryBuildFromInventoryRaw");
        return method!.Invoke(null, [inventoryRaw, AgentId, CollectedAt]) as AgentHardwareComponents;
    }

    private static AgentHardwareComponents? Parse(object payload)
        => InvokeTryBuild(JsonSerializer.Serialize(payload));

    // ─────────────────────────────────────────────────────────────────────
    // (a) Servidor completo.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void ServidorCompleto_ParseiaTodasAsSecoes()
    {
        var result = Parse(FullServerPayload());

        Assert.That(result, Is.Not.Null, "servidor com dados não pode ser descartado");
        Assert.Multiple(() =>
        {
            Assert.That(result!.WindowsRoles, Has.Count.EqualTo(3));
            Assert.That(result.RaidControllers, Has.Count.EqualTo(2));
            Assert.That(result.VirtualMachines, Has.Count.EqualTo(2));
            Assert.That(result.Clusters, Has.Count.EqualTo(1));
            Assert.That(result.StoragePools, Has.Count.EqualTo(2));
            Assert.That(result.RdsSessions, Has.Count.EqualTo(2));
            Assert.That(result.IsRdsHost, Is.True);
        });
    }

    [Test]
    public void ServidorCompleto_RolesInstaladasEEstado()
    {
        var roles = Parse(FullServerPayload())!.WindowsRoles;

        var addDs = roles.Single(role => role.Name == "AD-Domain-Services");
        Assert.Multiple(() =>
        {
            Assert.That(addDs.DisplayName, Is.EqualTo("Active Directory Domain Services"));
            Assert.That(addDs.Installed, Is.True);
            Assert.That(addDs.InstallState, Is.EqualTo("Installed"));
            Assert.That(addDs.AgentId, Is.EqualTo(AgentId));
            Assert.That(addDs.CollectedAt, Is.EqualTo(CollectedAt));

            // Role NÃO instalada continua sendo reportada (com installed=false).
            var hyperV = roles.Single(role => role.Name == "Hyper-V");
            Assert.That(hyperV.Installed, Is.False);
            Assert.That(hyperV.InstallState, Is.EqualTo("Available"));
        });
    }

    /// <summary>
    /// A ferramenta RAID que FALHOU precisa ser persistida: é o que permite ao
    /// site distinguir "servidor sem RAID" de "não consegui ler o RAID".
    /// </summary>
    [Test]
    public void ServidorCompleto_RaidPreservaFalhaEDegradados()
    {
        var raids = Parse(FullServerPayload())!.RaidControllers;

        var perccli = raids.Single(raid => raid.Name == "perccli");
        Assert.Multiple(() =>
        {
            Assert.That(perccli.Ok, Is.True);
            Assert.That(perccli.Vendor, Is.EqualTo("Dell"));
            Assert.That(perccli.Controllers, Is.EqualTo(1));
            Assert.That(perccli.VirtualDisks, Is.EqualTo(2));
            Assert.That(perccli.PhysicalDisks, Is.EqualTo(8));
            Assert.That(perccli.DegradedJson, Does.Contain("Degraded"));
        });

        var storcli = raids.Single(raid => raid.Name == "storcli");
        Assert.Multiple(() =>
        {
            Assert.That(storcli.Ok, Is.False);
            Assert.That(storcli.Error, Is.EqualTo("timeout ao executar"));
            Assert.That(storcli.Path, Is.Null);
        });
    }

    [Test]
    public void ServidorCompleto_HyperVParseiaVms()
    {
        var vms = Parse(FullServerPayload())!.VirtualMachines;

        var db01 = vms.Single(vm => vm.Name == "SRV-DB01");
        Assert.Multiple(() =>
        {
            Assert.That(db01.State, Is.EqualTo("Running"));
            Assert.That(db01.StateCode, Is.EqualTo(2));
            Assert.That(db01.MemoryAssignedMb, Is.EqualTo(16384));
            Assert.That(db01.UptimeSeconds, Is.EqualTo(864000));
            Assert.That(db01.Generation, Is.EqualTo(2));
            Assert.That(db01.IntegrationServicesVersion, Is.EqualTo("6.3.9600.0"));

            // VM desligada: campos opcionais ausentes viram null, não zero.
            var app01 = vms.Single(vm => vm.Name == "SRV-APP01");
            Assert.That(app01.State, Is.EqualTo("Off"));
            Assert.That(app01.MemoryAssignedMb, Is.Null);
        });
    }

    [Test]
    public void ServidorCompleto_ClusterPreservaNosERecursos()
    {
        var cluster = Parse(FullServerPayload())!.Clusters.Single();

        Assert.Multiple(() =>
        {
            Assert.That(cluster.Name, Is.EqualTo("CLUSTER-PROD"));
            Assert.That(cluster.QuorumType, Is.EqualTo("NodeAndDiskMajority"));
            Assert.That(cluster.QuorumResource, Is.EqualTo("Cluster Disk 1"));
            Assert.That(cluster.DegradedResources, Is.EqualTo(0));
            Assert.That(cluster.NodesJson, Does.Contain("SRV-A"));
            Assert.That(cluster.NodesJson, Does.Contain("SRV-B"));
            Assert.That(cluster.ResourcesJson, Does.Contain("SQL Server"));
        });
    }

    [Test]
    public void ServidorCompleto_StoragePoolsERds()
    {
        var result = Parse(FullServerPayload())!;

        var primordial = result.StoragePools.Single(pool => pool.Name == "Primordial");
        Assert.Multiple(() =>
        {
            Assert.That(primordial.IsPrimordial, Is.True);
            Assert.That(primordial.HealthStatus, Is.EqualTo("Healthy"));
            Assert.That(primordial.SizeGb, Is.EqualTo(8000));
            Assert.That(primordial.DiskCount, Is.EqualTo(4));

            var dados = result.StoragePools.Single(pool => pool.Name == "Pool-Dados");
            Assert.That(dados.IsPrimordial, Is.False);
            Assert.That(dados.HealthStatus, Is.EqualTo("Warning"));
            Assert.That(dados.AllocatedGb, Is.EqualTo(3500));

            var sessao = result.RdsSessions.Single(session => session.SessionId == 2);
            Assert.That(sessao.Username, Is.EqualTo(@"CORP\pedro"));
            Assert.That(sessao.State, Is.EqualTo("Active"));
            Assert.That(sessao.SessionType, Is.EqualTo("rdp-tcp"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // (b) ARMADILHA: servidor que só reporta dados de servidor.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sem incluir as listas novas na checagem de "inventário vazio", este
    /// payload seria descartado (null) e o servidor perderia todo o inventário.
    /// </summary>
    [Test]
    public void ServidorSomenteDadosDeServidor_NaoEDescartado()
    {
        var payload = new
        {
            osName = "Windows Server 2022",
            osIsServer = true,
            osRoles = new object[]
            {
                new { name = "DNS", installed = true, installState = "Installed" }
            }
        };

        var result = Parse(payload);

        Assert.That(result, Is.Not.Null, "servidor só com osRoles foi descartado como inventário vazio");
        Assert.That(result!.WindowsRoles, Has.Count.EqualTo(1));
    }

    [Test]
    public void ServidorSomenteRdsHost_NaoEDescartado()
    {
        var payload = new { storageRds = new { rdsHost = true, storagePools = Array.Empty<object>() } };

        var result = Parse(payload);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null, "flag rdsHost sozinha é sinal de coleta de servidor");
            Assert.That(result!.IsRdsHost, Is.True);
        });
    }

    [Test]
    public void ServidorSomenteReason_NaoEDescartado()
    {
        var payload = new { serverInventoryReason = "acesso negado ao WMI" };

        var result = Parse(payload);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ServerInventoryReason, Is.EqualTo("acesso negado ao WMI"));
        });
    }

    /// <summary>Seções presentes porém vazias não devem virar linhas fantasmas.</summary>
    [Test]
    public void ServidorComSecoesVazias_NaoGeraLinhasFantasmas()
    {
        var payload = new
        {
            disks = new object[] { new { driveLetter = "C:" } },
            osRoles = Array.Empty<object>(),
            serverHardware = new { isServer = true, raidTools = Array.Empty<object>() },
            hyperV = new { enabled = false, vms = Array.Empty<object>() },
            cluster = new { present = false, clusters = Array.Empty<object>() },
            storageRds = new { storagePools = Array.Empty<object>(), rdsSessions = Array.Empty<object>() }
        };

        var result = Parse(payload);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.WindowsRoles, Is.Empty);
            Assert.That(result.RaidControllers, Is.Empty);
            Assert.That(result.VirtualMachines, Is.Empty);
            Assert.That(result.Clusters, Is.Empty);
            Assert.That(result.StoragePools, Is.Empty);
            Assert.That(result.RdsSessions, Is.Empty);
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // (c) Estação de trabalho / payload ausente — retrocompatibilidade.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EstacaoDeTrabalho_NaoQuebraEListasVazias()
    {
        var result = Parse(WorkstationPayload());

        Assert.That(result, Is.Not.Null, "estação com discos deve continuar sendo parseada");
        Assert.Multiple(() =>
        {
            Assert.That(result!.WindowsRoles, Is.Empty);
            Assert.That(result.RaidControllers, Is.Empty);
            Assert.That(result.VirtualMachines, Is.Empty);
            Assert.That(result.Clusters, Is.Empty);
            Assert.That(result.StoragePools, Is.Empty);
            Assert.That(result.RdsSessions, Is.Empty);
            // Null = "não reportado"; nunca false (o que afirmaria "não é RDS").
            Assert.That(result.IsRdsHost, Is.Null);
            Assert.That(result.ServerInventoryReason, Is.Null);
        });
    }

    [Test]
    public void PayloadAusenteOuInvalido_RetornaNullSemLancar()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InvokeTryBuild(null!), Is.Null);
            Assert.That(InvokeTryBuild(""), Is.Null);
            Assert.That(InvokeTryBuild("   "), Is.Null);
            Assert.That(InvokeTryBuild("not-json"), Is.Null);
            Assert.That(InvokeTryBuild("[1,2,3]"), Is.Null, "array na raiz não é inventário");
        });
    }

    /// <summary>Payload malformado nas seções de servidor não pode derrubar o sync.</summary>
    [Test]
    public void SecoesDeServidorMalformadas_NaoLancam()
    {
        var payload = new
        {
            disks = new object[] { new { driveLetter = "C:" } },
            osRoles = "isso-deveria-ser-array",
            serverHardware = "nem-objeto",
            hyperV = new object[] { 1, 2, 3 },
            cluster = 42,
            storageRds = new { storagePools = new object[] { "string-solta", 123, new { } } }
        };

        Assert.DoesNotThrow(() =>
        {
            var result = Parse(payload);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.WindowsRoles, Is.Empty);
            Assert.That(result.RaidControllers, Is.Empty);
            Assert.That(result.VirtualMachines, Is.Empty);
            Assert.That(result.Clusters, Is.Empty);
            // Itens sem "name" são ignorados (pool sem nome não é pool).
            Assert.That(result.StoragePools, Is.Empty);
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // (d) Merge: sync parcial não pode apagar inventário de servidor.
    // ─────────────────────────────────────────────────────────────────────

    private static AgentHardwareComponents MergeWith(
        string? incomingComponentsJson,
        string? inventoryRaw,
        AgentHardwareComponents? existing)
    {
        var method = typeof(HardwareInventoryParser).GetMethod(
            "MergeComponents",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.That(method, Is.Not.Null, "não encontrei MergeComponents");

        JsonElement? incoming = null;
        if (incomingComponentsJson is not null)
        {
            using var doc = JsonDocument.Parse(incomingComponentsJson);
            incoming = doc.RootElement.Clone();
        }

        return (AgentHardwareComponents)method!.Invoke(
            null, [incoming, inventoryRaw, existing, AgentId, CollectedAt])!;
    }

    /// <summary>
    /// Um sync parcial (ex.: só portas) não traz as seções de servidor. Os
    /// dados já coletados precisam sobreviver — os blocos são aninhados
    /// (serverHardware.raidTools, hyperV.vms, ...), então exigem regra própria
    /// de merge; procurá-los na raiz os perderia silenciosamente.
    /// </summary>
    [Test]
    public void MergeParcial_PreservaInventarioDeServidor()
    {
        var existing = Parse(FullServerPayload())!;

        // Sync parcial: só a lista de portas.
        var merged = MergeWith("""{"listeningPorts":[{"processName":"svc.exe","processId":10,"protocol":"tcp","address":"0.0.0.0","port":8080}]}""", null, existing);

        Assert.Multiple(() =>
        {
            Assert.That(merged.ListeningPorts, Has.Count.EqualTo(1));
            Assert.That(merged.WindowsRoles, Has.Count.EqualTo(3), "roles perdidas no merge parcial");
            Assert.That(merged.RaidControllers, Has.Count.EqualTo(2), "RAID perdido no merge parcial");
            Assert.That(merged.VirtualMachines, Has.Count.EqualTo(2), "VMs perdidas no merge parcial");
            Assert.That(merged.Clusters, Has.Count.EqualTo(1), "cluster perdido no merge parcial");
            Assert.That(merged.StoragePools, Has.Count.EqualTo(2), "storage pools perdidos no merge parcial");
            Assert.That(merged.RdsSessions, Has.Count.EqualTo(2), "sessões RDS perdidas no merge parcial");
            Assert.That(merged.IsRdsHost, Is.True);
        });
    }

    /// <summary>Coleta nova de servidor substitui a anterior (é snapshot).</summary>
    [Test]
    public void MergeComColetaNova_SubstituiInventarioDeServidor()
    {
        var existing = Parse(FullServerPayload())!;

        var novoRaw = JsonSerializer.Serialize(new
        {
            osRoles = new object[] { new { name = "DNS", installed = true, installState = "Installed" } }
        });

        var merged = MergeWith(null, novoRaw, existing);

        Assert.Multiple(() =>
        {
            Assert.That(merged.WindowsRoles, Has.Count.EqualTo(1));
            Assert.That(merged.WindowsRoles[0].Name, Is.EqualTo("DNS"));
            // Seção ausente na coleta nova preserva o valor anterior.
            Assert.That(merged.VirtualMachines, Has.Count.EqualTo(2));
        });
    }
}
