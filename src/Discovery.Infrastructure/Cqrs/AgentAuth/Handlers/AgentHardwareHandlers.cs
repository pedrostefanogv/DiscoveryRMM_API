using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Hardware;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentHardwareQueryHandler(
    IAgentHardwareRepository hardwareRepo
) : IRequestHandler<GetAgentHardwareQuery, Result<AgentHardwarePayloadDto>>
{
    public async Task<Result<AgentHardwarePayloadDto>> Handle(GetAgentHardwareQuery q, CancellationToken ct)
    {
        var hardware = await hardwareRepo.GetByAgentIdAsync(q.AgentId);
        var components = await hardwareRepo.GetComponentsAsync(q.AgentId);

        object? hardwareObj = null;
        if (hardware is not null)
        {
            hardwareObj = new
            {
                manufacturer = hardware.Manufacturer,
                model = hardware.Model,
                serial = hardware.SerialNumber,
                motherboardManufacturer = hardware.MotherboardManufacturer,
                motherboardModel = hardware.MotherboardModel,
                motherboardSerial = hardware.MotherboardSerialNumber,
                processor = hardware.Processor,
                processorCores = hardware.ProcessorCores,
                processorThreads = hardware.ProcessorThreads,
                processorArchitecture = hardware.ProcessorArchitecture,
                processorTdpWatts = hardware.ProcessorTdpWatts,
                processorSocket = hardware.ProcessorSocket,
                processorFrequencyGhz = hardware.ProcessorFrequencyGhz,
                processorReleaseDate = hardware.ProcessorReleaseDate,
                totalMemoryBytes = hardware.TotalMemoryBytes,
                gpuModel = hardware.GpuModel,
                gpuMemoryBytes = hardware.GpuMemoryBytes,
                gpuDriverVersion = hardware.GpuDriverVersion,
                biosVersion = hardware.BiosVersion,
                biosManufacturer = hardware.BiosManufacturer,
                biosDate = hardware.BiosDate,
                biosSerialNumber = hardware.BiosSerialNumber,
                osName = hardware.OsName,
                osVersion = hardware.OsVersion,
                osBuild = hardware.OsBuild,
                osArchitecture = hardware.OsArchitecture,
                osEdition = hardware.OsEdition,
                osIsServer = hardware.IsServer,
                osProductType = hardware.ProductType,
                // Formato físico da máquina (M294): null = não reportado.
                isLaptop = hardware.IsLaptop,
                machineType = hardware.MachineType,
                chassisType = hardware.ChassisType,
                pcSystemType = hardware.PcSystemType,
                osDomain = hardware.Domain,
                osWorkgroup = hardware.Workgroup,
                osPartOfDomain = hardware.PartOfDomain,
                machineScore = hardware.MachineScore
            };
        }

        var dto = new AgentHardwarePayloadDto(
            hardwareObj,
            components?.Disks,
            components?.NetworkAdapters,
            components?.MemoryModules,
            components?.Printers,
            components?.ListeningPorts,
            components?.OpenSockets
        );

        return Result<AgentHardwarePayloadDto>.Success(dto);
    }
}

public sealed class ReportAgentHardwareCommandHandler(
    IAgentRepository agentRepo,
    IAgentHardwareRepository hardwareRepo,
    ILabelRevaluationQueue? revaluationQueue = null
) : IRequestHandler<ReportAgentHardwareCommand, Result<VoidResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<Result<VoidResult>> Handle(ReportAgentHardwareCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        // Update agent fields
        if (!string.IsNullOrWhiteSpace(cmd.Hostname) && agent.Hostname != cmd.Hostname)
            agent.Hostname = cmd.Hostname;
        if (!string.IsNullOrWhiteSpace(cmd.DisplayName))
            agent.DisplayName = cmd.DisplayName;
        if (!string.IsNullOrWhiteSpace(cmd.OperatingSystem))
            agent.OperatingSystem = cmd.OperatingSystem;
        if (!string.IsNullOrWhiteSpace(cmd.OsVersion))
            agent.OsVersion = cmd.OsVersion;
        // Placeholders de build sem ldflags ("dev"/"0.0.0"/"unknown") nunca
        // sobrescrevem um valor real já conhecido (AgentVersionNormalizer).
        var reportedAgentVersion = AgentVersionNormalizer.PickVersion(cmd.AgentVersion);
        if (reportedAgentVersion is not null && agent.AgentVersion != reportedAgentVersion)
            agent.AgentVersion = reportedAgentVersion;
        var reportedCommitHash = AgentVersionNormalizer.NormalizeCommit(cmd.CommitHash);
        if (reportedCommitHash is not null && agent.CommitHash != reportedCommitHash)
            agent.CommitHash = reportedCommitHash;
        if (!string.IsNullOrWhiteSpace(cmd.LastIpAddress))
            agent.LastIpAddress = cmd.LastIpAddress;
        if (!string.IsNullOrWhiteSpace(cmd.MacAddress))
            agent.MacAddress = cmd.MacAddress;

        // Usuário logado no Windows (último valor conhecido). Preferência:
        // 1) campo explícito do envelope (mesmo valor do heartbeat);
        // 2) parse do inventoryRaw (agentes antigos, que já enviam loggedInUsers).
        // O valor ao vivo chega pelo heartbeat; persistir aqui mantém
        // display/busca para agentes offline. Nunca sobrescreve com vazio
        // (sync parcial de portas/tarefas não apaga).
        var reportedLoggedUser = !string.IsNullOrWhiteSpace(cmd.LoggedUser)
            ? cmd.LoggedUser.Trim()
            : HardwareInventoryParser.TryExtractPrimaryLoggedUser(cmd.InventoryRaw?.GetRawText());
        if (reportedLoggedUser is { Length: > 256 })
            reportedLoggedUser = reportedLoggedUser[..256];
        if (!string.IsNullOrWhiteSpace(reportedLoggedUser) && agent.LoggedUser != reportedLoggedUser)
            agent.LoggedUser = reportedLoggedUser;

        // Fingerprint de hardware (Recuperação de Dispositivos): TPM EK + SMBIOS UUID.
        // Persistido no agent para permitir recuperação futura. Só atualiza se mudou.
        if (cmd.Hardware is JsonElement { ValueKind: JsonValueKind.Object } hwFp)
        {
            string? tpmEk = null;
            string? smbiosUuid = null;
            if (hwFp.TryGetProperty("tpmEk", out var tpm)) tpmEk = tpm.GetString();
            if (hwFp.TryGetProperty("smbiosUuid", out var uuid)) smbiosUuid = uuid.GetString();

            if (!string.IsNullOrWhiteSpace(tpmEk) || !string.IsNullOrWhiteSpace(smbiosUuid))
            {
                var newFingerprint = DeviceFingerprint.ComputeHash(tpmEk, smbiosUuid);
                if (agent.TpmEkHash != tpmEk || agent.SmbiosUuid != smbiosUuid || agent.FingerprintHash != newFingerprint)
                {
                    agent.TpmEkHash = tpmEk;
                    agent.SmbiosUuid = smbiosUuid;
                    agent.FingerprintHash = newFingerprint;
                }
            }
        }

        await agentRepo.UpdateAsync(agent);

        // Build hardware info from command payload
        var hardwareInfo = new AgentHardwareInfo
        {
            AgentId = cmd.AgentId,
            InventoryRaw = cmd.InventoryRaw?.GetRawText(),
            InventorySchemaVersion = cmd.InventorySchemaVersion,
            InventoryCollectedAt = cmd.InventoryCollectedAt
        };

        // Parse hardware object (JSON-like from Agent)
        if (cmd.Hardware is JsonElement { ValueKind: JsonValueKind.Object } hw)
        {
            if (hw.TryGetProperty("manufacturer", out var m)) hardwareInfo.Manufacturer = m.GetString();
            if (hw.TryGetProperty("model", out var mod)) hardwareInfo.Model = mod.GetString();
            if (hw.TryGetProperty("serial", out var s)) hardwareInfo.SerialNumber = s.GetString();
            if (hw.TryGetProperty("motherboardManufacturer", out var mm)) hardwareInfo.MotherboardManufacturer = mm.GetString();
            if (hw.TryGetProperty("motherboardModel", out var mmod)) hardwareInfo.MotherboardModel = mmod.GetString();
            if (hw.TryGetProperty("motherboardSerial", out var ms)) hardwareInfo.MotherboardSerialNumber = ms.GetString();
            if (hw.TryGetProperty("processor", out var p)) hardwareInfo.Processor = p.GetString();
            if (hw.TryGetProperty("processorCores", out var pc) && pc.TryGetInt32(out var pcv)) hardwareInfo.ProcessorCores = pcv;
            if (hw.TryGetProperty("processorThreads", out var pt) && pt.TryGetInt32(out var ptv)) hardwareInfo.ProcessorThreads = ptv;
            if (hw.TryGetProperty("processorArchitecture", out var pa)) hardwareInfo.ProcessorArchitecture = pa.GetString();
            if (hw.TryGetProperty("processorTdpWatts", out var ptw) && ptw.TryGetInt32(out var ptwv)) hardwareInfo.ProcessorTdpWatts = ptwv;
            if (hw.TryGetProperty("processorSocket", out var ps)) hardwareInfo.ProcessorSocket = ps.GetString();
            if (hw.TryGetProperty("processorFrequencyGhz", out var pf) && pf.TryGetDecimal(out var pfv)) hardwareInfo.ProcessorFrequencyGhz = pfv;
            if (hw.TryGetProperty("processorReleaseDate", out var prd)) hardwareInfo.ProcessorReleaseDate = prd.GetString();
            if (hw.TryGetProperty("totalMemoryBytes", out var tmb) && tmb.TryGetInt64(out var tmbv)) hardwareInfo.TotalMemoryBytes = tmbv;
            if (hw.TryGetProperty("gpuModel", out var gm)) hardwareInfo.GpuModel = gm.GetString();
            if (hw.TryGetProperty("gpuMemoryBytes", out var gmb) && gmb.TryGetInt64(out var gmbv)) hardwareInfo.GpuMemoryBytes = gmbv;
            if (hw.TryGetProperty("gpuDriverVersion", out var gdv)) hardwareInfo.GpuDriverVersion = gdv.GetString();
            if (hw.TryGetProperty("biosVersion", out var bv)) hardwareInfo.BiosVersion = bv.GetString();
            if (hw.TryGetProperty("biosManufacturer", out var bm)) hardwareInfo.BiosManufacturer = bm.GetString();
            if (hw.TryGetProperty("biosDate", out var bd)) hardwareInfo.BiosDate = bd.GetString();
            if (hw.TryGetProperty("biosSerialNumber", out var bsn)) hardwareInfo.BiosSerialNumber = bsn.GetString();
            if (hw.TryGetProperty("osName", out var on)) hardwareInfo.OsName = on.GetString();
            if (hw.TryGetProperty("osVersion", out var ov)) hardwareInfo.OsVersion = ov.GetString();
            if (hw.TryGetProperty("osBuild", out var ob)) hardwareInfo.OsBuild = ob.GetString();
            if (hw.TryGetProperty("osArchitecture", out var oa)) hardwareInfo.OsArchitecture = oa.GetString();

            // Identidade do SO (Windows Server x estação / domínio). Aceita o
            // envelope Web sem distinção de caixa (osEdition, osIsServer, ...).
            var reportedEdition = ReadOsString(hw, "osEdition");
            if (reportedEdition is not null) hardwareInfo.OsEdition = Truncate(reportedEdition, 100);

            var reportedIsServer = ReadOsBool(hw, "osIsServer", ["isServer", "osServer"]);
            if (reportedIsServer is not null) hardwareInfo.IsServer = reportedIsServer;

            // ProductType bruto do Windows: 1=Workstation, 2=DomainController,
            // 3=Server/NT. Guardado como texto para preservar o valor original.
            var reportedProductType = ReadOsString(hw, "osProductType", ["productType", "osProductTypeName"]);
            if (reportedProductType is not null) hardwareInfo.ProductType = Truncate(reportedProductType, 50);

            var reportedDomain = ReadOsString(hw, "osDomain", ["domain"]);
            if (reportedDomain is not null) hardwareInfo.Domain = Truncate(reportedDomain, 255);

            var reportedWorkgroup = ReadOsString(hw, "osWorkgroup", ["workgroup"]);
            if (reportedWorkgroup is not null) hardwareInfo.Workgroup = Truncate(reportedWorkgroup, 255);

            var reportedPartOfDomain = ReadOsBool(hw, "osPartOfDomain", ["partOfDomain", "osIsDomainJoined"]);
            if (reportedPartOfDomain is not null) hardwareInfo.PartOfDomain = reportedPartOfDomain;

            // ── Formato físico da máquina (notebook × desktop × tablet × servidor) ──
            // Aliases tolerados para não descartar payload de versão diferente do
            // agente. Valor ausente NÃO apaga o que já está gravado.
            var reportedIsLaptop = ReadOsBool(hw, "isLaptop", ["isNotebook", "notebook"]);
            if (reportedIsLaptop is not null) hardwareInfo.IsLaptop = reportedIsLaptop;

            var reportedMachineType = ReadOsString(hw, "machineType", ["machine_type", "deviceType"]);
            if (reportedMachineType is not null)
            {
                // "unknown" (ou valor fora do vocabulário) é NÃO REPORTADO e não
                // sobrescreve o último tipo conhecido: agentes ANTIGOS enviam
                // "unknown" quando a leitura de WMI falha, e gravar isso apagava
                // o "notebook" já identificado — o painel passava a mostrar "—"
                // no tipo de máquina (e o ícone perdia a distinção visual).
                var normalizedMachineType = NormalizeMachineType(reportedMachineType);
                if (normalizedMachineType != "unknown")
                    hardwareInfo.MachineType = normalizedMachineType;
            }

            var reportedChassisType = ReadInt(hw, "chassisType", ["chassis_type"]);
            if (reportedChassisType is not null) hardwareInfo.ChassisType = reportedChassisType;

            var reportedPcSystemType = ReadInt(hw, "pcSystemType", ["pc_system_type", "systemType"]);
            if (reportedPcSystemType is not null) hardwareInfo.PcSystemType = reportedPcSystemType;
        }

        // MachineScore: enviado pelo agent no nível raiz do envelope (cmd.MachineScore),
        // e também suportado dentro do objeto "hardware" aninhado para compatibilidade.
        if (cmd.MachineScore.HasValue)
            hardwareInfo.MachineScore = cmd.MachineScore.Value;

        if (cmd.Hardware is JsonElement { ValueKind: JsonValueKind.Object } hw2)
        {
            if (hw2.TryGetProperty("machineScore", out var ms2) && ms2.TryGetInt32(out var ms2v))
                hardwareInfo.MachineScore ??= ms2v;
        }

        if (cmd.Hardware is not null && hardwareInfo.MachineScore is null)
        {
            // Fallback: deserializa o objeto cru para completar o que o parse
            // explícito acima não cobriu (ex.: campos sem bloco próprio).
            //
            // IMPORTANTE: o fallback PREENCHE LACUNAS, nunca substitui o
            // hardwareInfo já montado. Substituir apagava silenciosamente todo
            // campo cujo nome JSON difere do nome da propriedade C# — era o
            // caso de osIsServer/osProductType/osDomain/osPartOfDomain, que o
            // parse explícito lia e o fallback descartava logo em seguida
            // (só sobrevivia o que casava por nome, como osEdition).
            try
            {
                var json = JsonSerializer.Serialize(cmd.Hardware, JsonOptions);
                var parsed = JsonSerializer.Deserialize<AgentHardwareInfo>(json, JsonOptions);
                if (parsed is not null)
                    FillMissingHardwareFields(hardwareInfo, parsed);
            }
            catch { /* keep defaults */ }
        }

        // Merge de componentes: payload do agent + InventoryRaw + estado já
        // armazenado. Listas ausentes no payload NÃO são apagadas (sync parcial
        // de portas/sockets não perde impressoras/startup/tarefas).
        JsonElement? incomingComponents = null;
        if (cmd.Components is JsonElement comp)
        {
            incomingComponents = comp;
        }
        else if (cmd.Components is not null)
        {
            try
            {
                var compJson = JsonSerializer.Serialize(cmd.Components, JsonOptions);
                using var doc = JsonDocument.Parse(compJson);
                incomingComponents = doc.RootElement.Clone();
            }
            catch { /* invalid components, skip */ }
        }

        var existingComponents = await hardwareRepo.GetComponentsAsync(cmd.AgentId);
        var collectedAt = cmd.InventoryCollectedAt ?? DateTime.UtcNow;
        var components = HardwareInventoryParser.MergeComponents(
            incomingComponents,
            cmd.InventoryRaw?.GetRawText(),
            existingComponents,
            cmd.AgentId,
            collectedAt);

        // ── Sync PARCIAL (só componentes) ──────────────────────────────────
        //
        // SyncNetworkConnections e SyncStartupAndScheduledTasks enviam APENAS
        // o objeto "components" — sem "hardware" e sem "inventoryRaw". O
        // UpsertAsync recopia campo a campo, então sem este carregamento os
        // dados de hardware já persistidos (fabricante, modelo, processador,
        // SO, séries, inventoryRaw) eram APAGADOS a cada sync parcial — e o
        // sync de portas roda durante toda a vida do agente.
        //
        // Aqui o payload é claramente parcial, então preenchemos só o que ele
        // NÃO trouxe com o estado atual: um sync parcial nunca apaga hardware.
        // Um sync completo continua autoritativo (só campos nulos são
        // preenchidos), inclusive para refletir mudanças legítimas.
        // A condição é só "não veio o objeto hardware". O inventoryRaw, sozinho,
        // NÃO é fonte dos campos escalares (o handler nunca derivou escalar do
        // raw — só componentes), então qualquer payload sem `hardware` é
        // parcial para fins de hardware e não pode apagar o que está gravado.
        if (cmd.Hardware is null)
        {
            var current = await hardwareRepo.GetByAgentIdAsync(cmd.AgentId);
            if (current is not null)
            {
                // Preenche o que este payload NÃO trouxe (só campos nulos).
                FillMissingHardwareFields(hardwareInfo, current);

                // Estes dois o payload parcial SOBRESCREVE sem ter coletado
                // hardware: inventoryRaw chega como string vazia (o campo não
                // tem omitempty no envelope) e inventoryCollectedAt chega com a
                // hora do sync de portas. Restaurar evita perder o inventário
                // cru (fallback de bateria/BitLocker/usuário no site) e evita
                // anunciar uma coleta de hardware que não aconteceu.
                hardwareInfo.InventoryRaw = current.InventoryRaw ?? hardwareInfo.InventoryRaw;
                hardwareInfo.InventoryCollectedAt = current.InventoryCollectedAt ?? hardwareInfo.InventoryCollectedAt;
            }
        }

        await hardwareRepo.UpsertAsync(hardwareInfo, components);

        // Agendamento debounced: no-op quando AgentLabeling:TriggerOnInventorySync=false.
        revaluationQueue?.Schedule(cmd.AgentId);

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    /// <summary>
    /// Preenche <paramref name="target"/> apenas com os campos que ainda estão
    /// null, vindos de <paramref name="source"/> (o objeto cru desserializado).
    ///
    /// Usado pelo fallback de MachineScore. O comportamento anterior — trocar o
    /// objeto inteiro — perdia todo campo cujo nome JSON não casasse com o nome
    /// da propriedade C# (osIsServer, osProductType, osDomain, osPartOfDomain),
    /// mesmo depois de o parse explícito tê-los lido corretamente.
    /// </summary>
    private static void FillMissingHardwareFields(AgentHardwareInfo target, AgentHardwareInfo source)
    {
        target.InventoryRaw ??= source.InventoryRaw;
        target.InventorySchemaVersion ??= source.InventorySchemaVersion;
        target.InventoryCollectedAt ??= source.InventoryCollectedAt;

        target.Manufacturer ??= source.Manufacturer;
        target.Model ??= source.Model;
        target.SerialNumber ??= source.SerialNumber;
        target.MotherboardManufacturer ??= source.MotherboardManufacturer;
        target.MotherboardModel ??= source.MotherboardModel;
        target.MotherboardSerialNumber ??= source.MotherboardSerialNumber;

        target.Processor ??= source.Processor;
        target.ProcessorCores ??= source.ProcessorCores;
        target.ProcessorThreads ??= source.ProcessorThreads;
        target.ProcessorArchitecture ??= source.ProcessorArchitecture;
        target.ProcessorTdpWatts ??= source.ProcessorTdpWatts;
        target.ProcessorSocket ??= source.ProcessorSocket;
        target.ProcessorFrequencyGhz ??= source.ProcessorFrequencyGhz;
        target.ProcessorReleaseDate ??= source.ProcessorReleaseDate;

        target.TotalMemoryBytes ??= source.TotalMemoryBytes;

        target.GpuModel ??= source.GpuModel;
        target.GpuMemoryBytes ??= source.GpuMemoryBytes;
        target.GpuDriverVersion ??= source.GpuDriverVersion;

        target.TotalDisksCount ??= source.TotalDisksCount;

        target.BiosVersion ??= source.BiosVersion;
        target.BiosManufacturer ??= source.BiosManufacturer;
        target.BiosDate ??= source.BiosDate;
        target.BiosSerialNumber ??= source.BiosSerialNumber;

        target.MachineScore ??= source.MachineScore;

        target.OsName ??= source.OsName;
        target.OsVersion ??= source.OsVersion;
        target.OsBuild ??= source.OsBuild;
        target.OsArchitecture ??= source.OsArchitecture;

        // Identidade do SO (Windows Server / domínio).
        target.OsEdition ??= source.OsEdition;
        target.IsServer ??= source.IsServer;
        target.ProductType ??= source.ProductType;
        target.Domain ??= source.Domain;
        target.Workgroup ??= source.Workgroup;
        target.PartOfDomain ??= source.PartOfDomain;

        // Formato físico da máquina.
        //
        // MachineType NÃO é copiado cru: este caminho (fallback de desserialização
        // do objeto "hardware") não passa pela normalização do parse explícito, e
        // copiar o valor bruto gravava texto livre na coluna (inclusive acima de
        // varchar(20), o que derrubaria o sync no banco real) e reescrevia
        // "unknown" por cima de um tipo já conhecido.
        target.IsLaptop ??= source.IsLaptop;
        if (target.MachineType is null && source.MachineType is not null)
        {
            var normalizedMachineType = NormalizeMachineType(source.MachineType);
            if (normalizedMachineType != "unknown")
                target.MachineType = normalizedMachineType;
        }

        target.ChassisType ??= source.ChassisType;
        target.PcSystemType ??= source.PcSystemType;
    }

    /// <summary>MachineType canônico usado pelo painel (mesmo vocabulário do agente).</summary>
    private static readonly HashSet<string> KnownMachineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "notebook",
        "desktop",
        "tablet",
        "server",
        "unknown"
    };

    /// <summary>
    /// Normaliza <c>machineType</c> para o vocabulário fechado. Qualquer valor
    /// fora dele vira "unknown" — o painel não deve receber texto livre, e a
    /// coluna é varchar(20) com semântica conhecida.
    /// </summary>
    private static string NormalizeMachineType(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return "unknown";

        return KnownMachineTypes.Contains(trimmed) ? trimmed.ToLowerInvariant() : "unknown";
    }

    /// <summary>
    /// Lê uma string do objeto "hardware" tentando a chave principal e as
    /// variações aceitas. Devolve null quando a chave não existe, é nula ou é
    /// vazia — assim um sync parcial NUNCA apaga o valor já persistido.
    /// Números também são aceitos (ex.: productType vindo como 3 e não "3").
    /// </summary>
    private static string? ReadOsString(JsonElement hw, string key, params string[] alternates)
    {
        foreach (var candidate in new[] { key }.Concat(alternates))
        {
            if (!hw.TryGetProperty(candidate, out var value))
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    /// <summary>
    /// Lê um booleano do objeto "hardware". Aceita booleano JSON nativo e, por
    /// tolerância, as formas textuais "true"/"false"/"1"/"0"/"sim"/"nao".
    /// Retorna null quando a chave está ausente ou ilegível, distinguindo
    /// "não reportado" (null) de "reportado como false".
    /// </summary>
    private static bool? ReadOsBool(JsonElement hw, string key, params string[] alternates)
    {
        foreach (var candidate in new[] { key }.Concat(alternates))
        {
            if (!hw.TryGetProperty(candidate, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number != 0;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim().ToLowerInvariant();
                if (text is "true" or "1" or "sim" or "yes") return true;
                if (text is "false" or "0" or "nao" or "não" or "no") return false;
            }
        }

        return null;
    }

    /// <summary>
    /// Lê um inteiro do objeto "hardware". Aceita número JSON e, por
    /// tolerância, texto numérico ("2"). Devolve null quando ausente/ilegível —
    /// assim um sync parcial NUNCA apaga o valor já persistido.
    /// </summary>
    private static int? ReadInt(JsonElement hw, string key, params string[] alternates)
    {
        foreach (var candidate in new[] { key }.Concat(alternates))
        {
            if (!hw.TryGetProperty(candidate, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>Corta o valor no limite da coluna para não estourar o INSERT.</summary>
    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
