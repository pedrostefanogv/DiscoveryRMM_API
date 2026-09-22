using System.Text.Json;
using Discovery.Core.Entities;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Shared JSON parsing helpers extracted from AgentAuthController.
/// </summary>
internal static class HardwareInventoryParser
{
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
            ScheduledTasks = ParseScheduledTasks(root, agentId, collectedAt)
        };

        return result.Disks.Count == 0
            && result.NetworkAdapters.Count == 0
            && result.MemoryModules.Count == 0
            && result.Printers.Count == 0
            && result.ListeningPorts.Count == 0
            && result.OpenSockets.Count == 0
            && result.StartupItems.Count == 0
            && result.ScheduledTasks.Count == 0
            ? null
            : result;
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

            if (result.Count >= 200)
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
                CollectedAt = collectedAt
            });

            if (result.Count >= 500)
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
            ScheduledTasks = Pick(inc, hasIncoming, "scheduledTasks", rawRoot, hasRaw, existing?.ScheduledTasks, ParseScheduledTasks, agentId, collectedAt)
        };

        return result;
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
        // lista é pequeno (<=500/1000 itens) e esse caminho é raro (apenas merge).
        var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement>
        {
            [propertyName] = array.Clone()
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}