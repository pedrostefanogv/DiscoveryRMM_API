using System.Text.Json;
using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Shared JSON parsing helpers extracted from AgentAuthController.
/// </summary>
internal static class HardwareInventoryParser
{
    // Limites de itens persistidos por coleta. Devem ser espelhados no frontend
    // (DiscoveryRMM_Site/src/pages/agents/AgentDetail.tsx) para o aviso de lista
    // truncada e a detecção de "Todos".
    internal const int MaxListeningPorts = 200;
    internal const int MaxOpenSockets = 5000;

    public static AgentHardwareComponents? TryBuildFromInventoryRaw(string? inventoryRaw, Guid agentId, DateTime collectedAt)
    {
        if (string.IsNullOrWhiteSpace(inventoryRaw))
            return null;

        if (!TryParseInventoryRoot(inventoryRaw, out var root))
            return null;

        var result = new AgentHardwareComponents
        {
            Disks = ParseDisks(root, agentId, collectedAt),
            NetworkAdapters = ParseNetworkAdapters(root, agentId, collectedAt),
            MemoryModules = ParseMemoryModules(root, agentId, collectedAt),
            Printers = ParsePrinters(root, agentId, collectedAt),
            ListeningPorts = ParseListeningPorts(root, agentId, collectedAt),
            OpenSockets = ParseOpenSockets(root, agentId, collectedAt),
            StartupItems = ParseStartupItems(root, agentId, collectedAt),
            ScheduledTasks = ParseScheduledTasks(root, agentId, collectedAt),

            // Windows Server: roles, RAID/BMC, Hyper-V, cluster, storage/RDS.
            WindowsRoles = ParseWindowsRoles(root, agentId, collectedAt),
            RaidControllers = ParseRaidControllers(root, agentId, collectedAt),
            VirtualMachines = ParseVirtualMachines(root, agentId, collectedAt),
            Clusters = ParseClusters(root, agentId, collectedAt),
            StoragePools = ParseStoragePools(root, agentId, collectedAt),
            RdsSessions = ParseRdsSessions(root, agentId, collectedAt)
        };

        result.IsRdsHost = ParseJson.TryGetObjectProperty(root, out var storageRoot, "storageRds")
            ? ParseJson.GetNullableBool(storageRoot, "rdsHost")
            : null;

        result.ServerInventoryReason = ParseJson.GetString(root, "serverInventoryReason");

        // As listas de Windows Server entram nesta checagem: um servidor que só
        // reportasse dados de servidor (sem discos/portas/tarefas) seria
        // descartado como "inventário vazio". IsRdsHost/ServerInventoryReason
        // também contam, pois são sinal de que houve coleta de servidor.
        return result.Disks.Count == 0
            && result.NetworkAdapters.Count == 0
            && result.MemoryModules.Count == 0
            && result.Printers.Count == 0
            && result.ListeningPorts.Count == 0
            && result.OpenSockets.Count == 0
            && result.StartupItems.Count == 0
            && result.ScheduledTasks.Count == 0
            && result.WindowsRoles.Count == 0
            && result.RaidControllers.Count == 0
            && result.VirtualMachines.Count == 0
            && result.Clusters.Count == 0
            && result.StoragePools.Count == 0
            && result.RdsSessions.Count == 0
            && result.IsRdsHost is null
            && string.IsNullOrWhiteSpace(result.ServerInventoryReason)
            ? null
            : result;
    }

    /// <summary>
    /// Extrai o usuário logado primário do inventário cru
    /// (<c>inventoryRaw.loggedInUsers[]</c>), usado como "último usuário
    /// conhecido" do agent. Prefere a sessão marcada como "active"; na ausência
    /// usa o primeiro usuário. Retorna null quando não há usuários ou o payload
    /// é inválido. O valor ao vivo chega separadamente pelo heartbeat.
    /// </summary>
    public static string? TryExtractPrimaryLoggedUser(string? inventoryRaw)
    {
        if (string.IsNullOrWhiteSpace(inventoryRaw))
            return null;

        if (!TryParseInventoryRoot(inventoryRaw, out var root))
            return null;

        if (!ParseJson.TryGetArrayProperty(root, out var users, "loggedInUsers", "logged_in_users"))
            return null;

        string? firstUser = null;
        foreach (var item in users.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var user = ParseJson.GetString(item, "user")?.Trim();
            if (string.IsNullOrWhiteSpace(user))
                continue;

            if (user.Length > 256)
                user = user[..256];

            var type = ParseJson.GetString(item, "type")?.Trim();
            if (string.Equals(type, "active", StringComparison.OrdinalIgnoreCase))
                return user;

            firstUser ??= user;
        }

        return firstUser;
    }

    private static bool TryParseInventoryRoot(string inventoryRaw, out JsonElement root)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(inventoryRaw);
            var element = doc.RootElement;

            if (element.ValueKind == JsonValueKind.String)
            {
                var innerJson = element.GetString();
                if (string.IsNullOrWhiteSpace(innerJson))
                    return false;

                using var innerDoc = JsonDocument.Parse(innerJson);
                root = innerDoc.RootElement.Clone();
                return true;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            root = element.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<DiskInfo> ParseDisks(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<DiskInfo>();
        if (!root.TryGetProperty("disks", out var disksElement) || disksElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in disksElement.EnumerateArray())
        {
            var driveLetter = ParseJson.GetString(item, "driveLetter")?.Trim();
            if (string.IsNullOrWhiteSpace(driveLetter))
                continue;

            result.Add(new DiskInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                DriveLetter = driveLetter,
                Label = ParseJson.GetString(item, "label"),
                FileSystem = ParseJson.GetString(item, "fileSystem"),
                TotalSizeBytes = ParseJson.GetLong(item, "totalSizeBytes"),
                FreeSpaceBytes = ParseJson.GetLong(item, "freeSpaceBytes"),
                MediaType = ParseJson.GetString(item, "mediaType"),
                SmartStatus = ParseJson.GetString(item, "smartStatus"),
                TemperatureC = ParseJson.GetNullableInt(item, "temperatureC"),
                PowerOnHours = ParseJson.GetNullableInt(item, "powerOnHours"),
                ReallocatedSectors = ParseJson.GetNullableInt(item, "reallocatedSectors"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<NetworkAdapterInfo> ParseNetworkAdapters(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<NetworkAdapterInfo>();
        if (!root.TryGetProperty("networkAdapters", out var adaptersElement) || adaptersElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in adaptersElement.EnumerateArray())
        {
            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new NetworkAdapterInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = name,
                MacAddress = ParseJson.GetString(item, "macAddress"),
                IpAddress = ParseJson.GetString(item, "ipAddress"),
                Ipv6Address = ParseJson.GetString(item, "ipv6Address"),
                SubnetMask = ParseJson.GetString(item, "subnetMask"),
                Gateway = ParseJson.GetString(item, "gateway"),
                DnsServers = ParseJson.GetString(item, "dnsServers"),
                IsDhcpEnabled = ParseJson.GetBool(item, "isDhcpEnabled"),
                AdapterType = ParseJson.GetString(item, "adapterType"),
                Speed = ParseJson.GetString(item, "speed"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<MemoryModuleInfo> ParseMemoryModules(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<MemoryModuleInfo>();
        if (!root.TryGetProperty("memoryModules", out var modulesElement) || modulesElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in modulesElement.EnumerateArray())
        {
            var capacityBytes = ParseJson.GetLong(item, "capacityBytes");
            if (capacityBytes <= 0)
                continue;

            result.Add(new MemoryModuleInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Slot = ParseJson.GetString(item, "slot"),
                CapacityBytes = capacityBytes,
                SpeedMhz = ParseJson.GetNullableInt(item, "speedMhz"),
                MemoryType = ParseJson.GetString(item, "memoryType"),
                Manufacturer = ParseJson.GetString(item, "manufacturer"),
                PartNumber = ParseJson.GetString(item, "partNumber"),
                SerialNumber = ParseJson.GetString(item, "serialNumber"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<PrinterInfo> ParsePrinters(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<PrinterInfo>();
        if (!root.TryGetProperty("printers", out var printersElement) || printersElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in printersElement.EnumerateArray())
        {
            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new PrinterInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = name,
                DriverName = ParseJson.GetString(item, "driverName"),
                PortName = ParseJson.GetString(item, "portName"),
                PrinterStatus = ParseJson.GetString(item, "printerStatus"),
                IsDefault = ParseJson.GetBool(item, "isDefault"),
                IsNetworkPrinter = ParseJson.GetBool(item, "isNetworkPrinter"),
                Shared = ParseJson.GetBool(item, "shared"),
                ShareName = ParseJson.GetString(item, "shareName"),
                Location = ParseJson.GetString(item, "location"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<ListeningPortInfo> ParseListeningPorts(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<ListeningPortInfo>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!ParseJson.TryGetArrayProperty(root, out var portsElement, "listeningPorts", "listening_ports"))
            return result;

        foreach (var item in portsElement.EnumerateArray())
        {
            var port = ParseJson.GetInt(item, "port");
            if (port <= 0)
                continue;

            var protocol = ParseJson.GetString(item, "protocol") ?? string.Empty;
            var address = ParseJson.GetString(item, "address") ?? string.Empty;
            var processId = ParseJson.GetInt(item, "processId", "pid");
            var dedupeKey = string.Concat(protocol, "|", address, "|", port, "|", processId);
            if (!dedupe.Add(dedupeKey))
                continue;

            result.Add(new ListeningPortInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                ProcessName = ParseJson.GetString(item, "processName", "name"),
                ProcessId = processId,
                ProcessPath = ParseJson.GetString(item, "processPath", "path"),
                Protocol = protocol,
                Address = address,
                Port = port,
                CollectedAt = collectedAt
            });

            if (result.Count >= MaxListeningPorts)
                break;
        }

        return result;
    }

    private static List<OpenSocketInfo> ParseOpenSockets(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<OpenSocketInfo>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!ParseJson.TryGetArrayProperty(root, out var socketsElement, "openSockets", "open_sockets", "process_open_sockets"))
            return result;

        foreach (var item in socketsElement.EnumerateArray())
        {
            var localPort = ParseJson.GetInt(item, "localPort", "local_port");
            var remotePort = ParseJson.GetInt(item, "remotePort", "remote_port");
            if (localPort <= 0 && remotePort <= 0)
                continue;

            var protocol = ParseJson.GetString(item, "protocol") ?? string.Empty;
            var family = ParseJson.GetString(item, "family") ?? string.Empty;
            var state = ParseJson.GetString(item, "state");
            var localAddress = ParseJson.GetString(item, "localAddress", "local_address") ?? string.Empty;
            var remoteAddress = ParseJson.GetString(item, "remoteAddress", "remote_address") ?? string.Empty;
            var processId = ParseJson.GetInt(item, "processId", "pid");

            var dedupeKey = string.Concat(
                protocol, "|",
                family, "|",
                localAddress, "|",
                localPort, "|",
                remoteAddress, "|",
                remotePort, "|",
                processId);

            if (!dedupe.Add(dedupeKey))
                continue;

            result.Add(new OpenSocketInfo
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                ProcessName = ParseJson.GetString(item, "processName", "name"),
                ProcessId = processId,
                ProcessPath = ParseJson.GetString(item, "processPath", "path"),
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                Protocol = protocol,
                Family = family,
                State = state,
                CollectedAt = collectedAt
            });

            if (result.Count >= MaxOpenSockets)
                break;
        }

        return result;
    }

    private static List<StartupItemInfo> ParseStartupItems(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<StartupItemInfo>();
        if (!ParseJson.TryGetArrayProperty(root, out var itemsElement, "startupItems", "startup_items"))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in itemsElement.EnumerateArray())
        {
            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var source = ParseJson.GetString(item, "source") ?? string.Empty;
            var itemType = ParseJson.GetString(item, "type") ?? "registry";
            var dedupeKey = string.Concat(itemType, "|", source, "|", name, "|", ParseJson.GetString(item, "username") ?? string.Empty);
            if (!seen.Add(dedupeKey))
                continue;

            result.Add(new StartupItemInfo
            {
                Name = name,
                Path = ParseJson.GetString(item, "path") ?? string.Empty,
                Args = ParseJson.GetString(item, "args") ?? string.Empty,
                Type = itemType,
                Source = source,
                Status = ParseJson.GetString(item, "status", "Status") ?? "enabled",
                Username = ParseJson.GetString(item, "username") ?? string.Empty,
                Detail = ParseJson.GetString(item, "detail") ?? string.Empty,
                Hive = ParseJson.GetString(item, "hive") ?? string.Empty
            });

            if (result.Count >= 500)
                break;
        }

        return result;
    }

    private static List<ScheduledTaskInfo> ParseScheduledTasks(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<ScheduledTaskInfo>();
        if (!ParseJson.TryGetArrayProperty(root, out var tasksElement, "scheduledTasks", "scheduled_tasks"))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tasksElement.EnumerateArray())
        {
            var taskName = ParseJson.GetString(item, "taskName", "task_name")?.Trim();
            if (string.IsNullOrWhiteSpace(taskName))
                continue;

            var taskPath = ParseJson.GetString(item, "taskPath", "task_path") ?? string.Empty;
            var dedupeKey = string.Concat(taskPath, "|", taskName);
            if (!seen.Add(dedupeKey))
                continue;

            result.Add(new ScheduledTaskInfo
            {
                TaskPath = taskPath,
                TaskName = taskName,
                State = ParseJson.GetString(item, "state") ?? "enabled",
                Status = ParseJson.GetString(item, "status") ?? string.Empty,
                Author = ParseJson.GetString(item, "author") ?? string.Empty,
                ActionPath = ParseJson.GetString(item, "actionPath", "action_path") ?? string.Empty,
                ActionArgs = ParseJson.GetString(item, "actionArgs", "action_args") ?? string.Empty,
                TriggerType = ParseJson.GetString(item, "triggerType", "trigger_type") ?? string.Empty,
                TriggerDesc = ParseJson.GetString(item, "triggerDesc", "trigger_desc") ?? string.Empty,
                NextRunTime = ParseJson.GetString(item, "nextRunTime", "next_run_time") ?? string.Empty,
                LastRunTime = ParseJson.GetString(item, "lastRunTime", "last_run_time") ?? string.Empty,
                LastResult = ParseJson.GetLong(item, "lastResult", "last_result")
            });

            if (result.Count >= 1000)
                break;
        }

        return result;
    }

    /// <summary>
    /// Faz o merge entre o payload de componentes recebido do agent, o
    /// InventoryRaw e os componentes já armazenados. Regra por lista:
    /// 1) propriedade presente no payload do agent (mesmo array vazio) → usa o payload;
    /// 2) senão, presente no InventoryRaw → usa o raw;
    /// 3) senão, preserva a lista já armazenada.
    /// Garante que sincronizações parciais (ex.: apenas portas) não apaguem
    /// listas não reportadas (impressoras, startup, tarefas agendadas...).
    /// </summary>
    public static AgentHardwareComponents? MergeComponents(
        JsonElement? incomingComponents,
        string? inventoryRaw,
        AgentHardwareComponents? existing,
        Guid agentId,
        DateTime collectedAt)
    {
        var incOpt = incomingComponents;
        var hasIncoming = incOpt.HasValue && incOpt.Value.ValueKind == JsonValueKind.Object;
        JsonElement inc = hasIncoming ? incOpt.GetValueOrDefault() : default;

        var hasRaw = false;
        JsonElement rawRoot = default;
        if (!string.IsNullOrWhiteSpace(inventoryRaw))
        {
            hasRaw = TryParseInventoryRoot(inventoryRaw, out rawRoot);
        }

        var result = new AgentHardwareComponents
        {
            Disks = Pick(inc, hasIncoming, "disks", rawRoot, hasRaw, existing?.Disks, ParseDisks, agentId, collectedAt),
            NetworkAdapters = Pick(inc, hasIncoming, "networkAdapters", rawRoot, hasRaw, existing?.NetworkAdapters, ParseNetworkAdapters, agentId, collectedAt),
            MemoryModules = Pick(inc, hasIncoming, "memoryModules", rawRoot, hasRaw, existing?.MemoryModules, ParseMemoryModules, agentId, collectedAt),
            Printers = Pick(inc, hasIncoming, "printers", rawRoot, hasRaw, existing?.Printers, ParsePrinters, agentId, collectedAt),
            ListeningPorts = Pick(inc, hasIncoming, "listeningPorts", rawRoot, hasRaw, existing?.ListeningPorts, ParseListeningPorts, agentId, collectedAt),
            OpenSockets = Pick(inc, hasIncoming, "openSockets", rawRoot, hasRaw, existing?.OpenSockets, ParseOpenSockets, agentId, collectedAt),
            StartupItems = Pick(inc, hasIncoming, "startupItems", rawRoot, hasRaw, existing?.StartupItems, ParseStartupItems, agentId, collectedAt),
            ScheduledTasks = Pick(inc, hasIncoming, "scheduledTasks", rawRoot, hasRaw, existing?.ScheduledTasks, ParseScheduledTasks, agentId, collectedAt),

            // Windows Server. Os dados são aninhados (osRoles é array na raiz,
            // mas raidTools/vms/clusters/storagePools/rdsSessions vivem dentro
            // de serverHardware/hyperV/cluster/storageRds), por isso cada um tem
            // a sua própria regra de merge.
            WindowsRoles = Pick(inc, hasIncoming, "osRoles", rawRoot, hasRaw, existing?.WindowsRoles, ParseWindowsRoles, agentId, collectedAt),
            RaidControllers = PickNested(inc, hasIncoming, "serverHardware", "raidTools", rawRoot, hasRaw, existing?.RaidControllers, ParseRaidControllers, agentId, collectedAt),
            VirtualMachines = PickNested(inc, hasIncoming, "hyperV", "vms", rawRoot, hasRaw, existing?.VirtualMachines, ParseVirtualMachines, agentId, collectedAt),
            Clusters = PickNested(inc, hasIncoming, "cluster", "clusters", rawRoot, hasRaw, existing?.Clusters, ParseClusters, agentId, collectedAt),
            StoragePools = PickNested(inc, hasIncoming, "storageRds", "storagePools", rawRoot, hasRaw, existing?.StoragePools, ParseStoragePools, agentId, collectedAt),
            RdsSessions = PickNested(inc, hasIncoming, "storageRds", "rdsSessions", rawRoot, hasRaw, existing?.RdsSessions, ParseRdsSessions, agentId, collectedAt)
        };

        // Estado escalar do host: preserva o valor já armazenado quando o sync
        // parcial não traz storageRds (agente antigo ou coleta só de portas).
        result.IsRdsHost = PickRdsHostFlag(inc, hasIncoming, rawRoot, hasRaw, existing?.IsRdsHost);
        result.ServerInventoryReason = PickServerReason(inc, hasIncoming, rawRoot, hasRaw, existing?.ServerInventoryReason);

        return result;
    }

    /// <summary>
    /// Merge de listas aninhadas em um objeto (<c>container.listProperty</c>),
    /// como <c>serverHardware.raidTools</c> ou <c>hyperV.vms</c>.
    ///
    /// Mesma regra do <see cref="Pick"/>: payload do agent → InventoryRaw →
    /// lista já armazenada. Existe separado porque <c>TryGetArrayProperty</c>
    /// procura a propriedade na raiz e não desce um nível.
    /// </summary>
    private static List<T> PickNested<T>(
        JsonElement incoming,
        bool hasIncoming,
        string containerProperty,
        string listProperty,
        JsonElement rawRoot,
        bool hasRaw,
        List<T>? existing,
        Func<JsonElement, Guid, DateTime, List<T>> parse,
        Guid agentId,
        DateTime collectedAt)
    {
        // Descobre a listProperty dentro de um container e devolve a lista
        // reembrulhada como raiz, no formato que os Parse* esperam.
        static bool TryExtract(JsonElement source, string container, string list, out JsonElement wrapped)
        {
            wrapped = default;
            if (!ParseJson.TryGetObjectProperty(source, out var nested, container))
                return false;

            if (!ParseJson.TryGetArrayProperty(nested, out var array, list))
                return false;

            wrapped = WrapArrayAsRoot(array, list);
            return true;
        }

        if (hasIncoming && incoming.ValueKind == JsonValueKind.Object
            && TryExtract(incoming, containerProperty, listProperty, out var fromPayload))
        {
            return parse(fromPayload, agentId, collectedAt);
        }

        if (hasRaw && TryExtract(rawRoot, containerProperty, listProperty, out var fromRaw))
            return parse(fromRaw, agentId, collectedAt);

        return existing ?? [];
    }

    /// <summary>flag rdsHost (storageRds.rdsHost), preservando o valor anterior.</summary>
    private static bool? PickRdsHostFlag(JsonElement incoming, bool hasIncoming, JsonElement rawRoot, bool hasRaw, bool? existing)
    {
        if (hasIncoming && ParseJson.TryGetObjectProperty(incoming, out var fromPayload, "storageRds"))
        {
            var flag = ParseJson.GetNullableBool(fromPayload, "rdsHost");
            if (flag is not null)
                return flag;
        }

        if (hasRaw && ParseJson.TryGetObjectProperty(rawRoot, out var fromRaw, "storageRds"))
            return ParseJson.GetNullableBool(fromRaw, "rdsHost");

        return existing;
    }

    /// <summary>Motivo da coleta de servidor, preservando o valor anterior.</summary>
    private static string? PickServerReason(JsonElement incoming, bool hasIncoming, JsonElement rawRoot, bool hasRaw, string? existing)
    {
        if (hasIncoming && incoming.ValueKind == JsonValueKind.Object)
        {
            var reason = ParseJson.GetString(incoming, "serverInventoryReason");
            if (!string.IsNullOrWhiteSpace(reason))
                return Truncate(reason, 1000);
        }

        if (hasRaw)
        {
            var reason = ParseJson.GetString(rawRoot, "serverInventoryReason");
            if (!string.IsNullOrWhiteSpace(reason))
                return Truncate(reason, 1000);
        }

        return existing;
    }


    // ─────────────────────────────────────────────────────────────────────
    // Windows Server: roles, RAID/BMC, Hyper-V, cluster, Storage/RDS.
    //
    // Estes dados só chegam quando a máquina é Windows Server (o agente faz
    // gate por IsServer). Em estações e agentes antigos as chaves estão
    // ausentes — todos os parseadores abaixo devolvem lista vazia sem lançar.
    // ─────────────────────────────────────────────────────────────────────

    private static List<AgentWindowsRole> ParseWindowsRoles(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentWindowsRole>();
        if (!ParseJson.TryGetArrayProperty(root, out var roles, "osRoles"))
            return result;

        foreach (var item in roles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new AgentWindowsRole
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = Truncate(name, 200),
                DisplayName = Truncate(ParseJson.GetString(item, "displayName"), 300),
                Installed = ParseJson.GetBool(item, "installed"),
                InstallState = Truncate(ParseJson.GetString(item, "installState"), 50),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    /// <summary>
    /// RAID/BMC: lê <c>serverHardware.raidTools[]</c>. Cada ferramenta vira uma
    /// linha, inclusive quando falhou (Ok=false + Error) — o site precisa
    /// distinguir "sem RAID" de "não conseguiu ler o RAID".
    /// </summary>
    private static List<AgentRaidController> ParseRaidControllers(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentRaidController>();
        if (!ParseJson.TryGetObjectProperty(root, out var serverHardware, "serverHardware"))
            return result;

        if (!ParseJson.TryGetArrayProperty(serverHardware, out var tools, "raidTools"))
            return result;

        foreach (var item in tools.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new AgentRaidController
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = Truncate(name, 200),
                Vendor = Truncate(ParseJson.GetString(item, "vendor"), 200),
                Path = Truncate(ParseJson.GetString(item, "path"), 500),
                Ok = ParseJson.GetBool(item, "ok"),
                Error = Truncate(ParseJson.GetString(item, "error"), 1000),
                Controllers = ParseJson.GetNullableInt(item, "controllers"),
                VirtualDisks = ParseJson.GetNullableInt(item, "virtualDisks"),
                PhysicalDisks = ParseJson.GetNullableInt(item, "physicalDisks"),
                DegradedJson = ParseJson.SerializeArrayProperty(item, "degraded"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<AgentVirtualMachine> ParseVirtualMachines(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentVirtualMachine>();
        if (!ParseJson.TryGetObjectProperty(root, out var hyperV, "hyperV"))
            return result;

        if (!ParseJson.TryGetArrayProperty(hyperV, out var vms, "vms"))
            return result;

        foreach (var item in vms.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new AgentVirtualMachine
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = Truncate(name, 200),
                State = Truncate(ParseJson.GetString(item, "state"), 50),
                StateCode = ParseJson.GetNullableInt(item, "stateCode"),
                MemoryAssignedMb = ParseJson.GetNullableLong(item, "memoryAssignedMb"),
                UptimeSeconds = ParseJson.GetNullableLong(item, "uptimeSeconds"),
                Generation = ParseJson.GetNullableInt(item, "generation"),
                IntegrationServicesVersion = Truncate(ParseJson.GetString(item, "integrationServicesVersion"), 100),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    /// <summary>
    /// Clusters de failover. Nós e recursos ficam em colunas JSON (listas
    /// aninhadas, sem consulta relacional própria).
    /// </summary>
    private static List<AgentCluster> ParseClusters(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentCluster>();
        if (!ParseJson.TryGetObjectProperty(root, out var cluster, "cluster"))
            return result;

        if (!ParseJson.TryGetArrayProperty(cluster, out var clusters, "clusters"))
            return result;

        foreach (var item in clusters.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new AgentCluster
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = Truncate(name, 200),
                NodesJson = ParseJson.SerializeArrayProperty(item, "nodes"),
                ResourcesJson = ParseJson.SerializeArrayProperty(item, "resources"),
                QuorumType = Truncate(ParseJson.GetString(item, "quorumType"), 100),
                QuorumResource = Truncate(ParseJson.GetString(item, "quorumResource"), 300),
                DegradedResources = ParseJson.GetNullableInt(item, "degradedResources"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<AgentStoragePool> ParseStoragePools(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentStoragePool>();
        if (!ParseJson.TryGetObjectProperty(root, out var storageRds, "storageRds"))
            return result;

        if (!ParseJson.TryGetArrayProperty(storageRds, out var pools, "storagePools"))
            return result;

        foreach (var item in pools.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ParseJson.GetString(item, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new AgentStoragePool
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                Name = Truncate(name, 200),
                HealthStatus = Truncate(ParseJson.GetString(item, "healthStatus"), 50),
                OperationalStatus = Truncate(ParseJson.GetString(item, "operationalStatus"), 100),
                SizeGb = ParseJson.GetNullableLong(item, "sizeGb"),
                AllocatedGb = ParseJson.GetNullableLong(item, "allocatedGb"),
                DiskCount = ParseJson.GetNullableInt(item, "diskCount"),
                IsPrimordial = ParseJson.GetBool(item, "isPrimordial"),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    private static List<AgentRdsSession> ParseRdsSessions(JsonElement root, Guid agentId, DateTime collectedAt)
    {
        var result = new List<AgentRdsSession>();
        if (!ParseJson.TryGetObjectProperty(root, out var storageRds, "storageRds"))
            return result;

        if (!ParseJson.TryGetArrayProperty(storageRds, out var sessions, "rdsSessions"))
            return result;

        foreach (var item in sessions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var sessionId = ParseJson.GetNullableInt(item, "sessionId");
            if (sessionId is null)
                continue;

            result.Add(new AgentRdsSession
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                SessionId = sessionId.Value,
                Username = Truncate(ParseJson.GetString(item, "username"), 200),
                State = Truncate(ParseJson.GetString(item, "state"), 50),
                SessionType = Truncate(ParseJson.GetString(item, "sessionType"), 50),
                CollectedAt = collectedAt
            });
        }

        return result;
    }

    /// <summary>
    /// Corta o valor no limite da coluna para não estourar o INSERT.
    ///
    /// Contrato de anulabilidade: devolve a própria entrada quando ela é nula/vazia
    /// (nunca inventa uma string), e por isso é anotado com NotNullIfNotNull — o que
    /// descreve a semântica real (ver HardwareInventoryTruncateProbeTests) e permite
    /// atribuir o resultado tanto a propriedades <c>string?</c> (ex.: DisplayName,
    /// Vendor, Username de AgentRdsSession, cujo JSON pode realmente vir sem valor)
    /// quanto a propriedades <c>string</c> não-anuláveis (Name das entidades de
    /// hardware), que só são preenchidas depois do guard IsNullOrWhiteSpace(name).
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static List<T> Pick<T>(
        JsonElement incoming,
        bool hasIncoming,
        string incomingProperty,
        JsonElement rawRoot,
        bool hasRaw,
        List<T>? existing,
        Func<JsonElement, Guid, DateTime, List<T>> parse,
        Guid agentId,
        DateTime collectedAt)
    {
        if (hasIncoming && incoming.ValueKind == JsonValueKind.Object && incoming.TryGetProperty(incomingProperty, out var incomingElement))
        {
            if (incomingElement.ValueKind == JsonValueKind.Array)
                return parse(WrapArrayAsRoot(incomingElement, incomingProperty), agentId, collectedAt);
        }

        if (hasRaw && ParseJson.TryGetArrayProperty(rawRoot, out var rawElement, incomingProperty))
            return parse(WrapArrayAsRoot(rawElement, incomingProperty), agentId, collectedAt);

        return existing ?? [];
    }

    /// <summary>
    /// Os parseadores consultam propriedades pelo nome no objeto raiz
    /// (TryGetProperty), então um array isolado precisa voltar a ser um objeto:
    /// { "<propriedade>": [ ... ] }. O propertyName é sempre o primeiro alias
    /// canônico aceito pelo Parse* correspondente.
    /// </summary>
    private static JsonElement WrapArrayAsRoot(JsonElement array, string propertyName)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return array;

        // Serializa { "prop": [ ... ] } para string e reparseia — o volume por
        // lista é limitado (MaxOpenSockets/MaxListeningPorts) e esse caminho é
        // raro (apenas merge).
        var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
        {
            [propertyName] = array.Clone()
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}