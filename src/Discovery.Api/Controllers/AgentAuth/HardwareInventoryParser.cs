using System.Text.Json;
using Discovery.Core.Entities;

namespace Discovery.Api.Controllers;

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
            OpenSockets = ParseOpenSockets(root, agentId, collectedAt)
        };

        return result.Disks.Count == 0
            && result.NetworkAdapters.Count == 0
            && result.MemoryModules.Count == 0
            && result.Printers.Count == 0
            && result.ListeningPorts.Count == 0
            && result.OpenSockets.Count == 0
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
}
