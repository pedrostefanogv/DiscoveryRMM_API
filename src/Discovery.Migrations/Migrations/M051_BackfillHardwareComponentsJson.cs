using FluentMigrator;
using System.Data;
using System.Text.Json;

namespace Discovery.Migrations.Migrations;

[Migration(20260311_051)]
public class M051_BackfillHardwareComponentsJson : Migration
{
    public override void Up()
    {
        Execute.WithConnection((connection, transaction) =>
        {
            var disksByAgent = LoadDisksByAgent(connection, transaction);
            var adaptersByAgent = LoadNetworkAdaptersByAgent(connection, transaction);
            var modulesByAgent = LoadMemoryModulesByAgent(connection, transaction);

            var agentsFromLegacy = disksByAgent.Keys
                .Concat(adaptersByAgent.Keys)
                .Concat(modulesByAgent.Keys)
                .Distinct()
                .ToList();

            var existingAgents = LoadExistingHardwareAgents(connection, transaction);

            // Garante linha base em agent_hardware_info para agents que só tinham dados legados.
            foreach (var agentId in agentsFromLegacy.Where(id => !existingAgents.Contains(id)))
            {
                Insert.IntoTable("agent_hardware_info").Row(new
                {
                    id = agentId,
                    agent_id = agentId,
                    collected_at = DateTime.UtcNow,
                    updated_at = DateTime.UtcNow
                });
            }

            var agentsToBackfill = LoadAgentsWithNullComponents(connection, transaction);

            foreach (var agentId in agentsToBackfill)
            {
                var disks = disksByAgent.TryGetValue(agentId, out var diskRows) ? diskRows : [];
                var adapters = adaptersByAgent.TryGetValue(agentId, out var adapterRows) ? adapterRows : [];
                var modules = modulesByAgent.TryGetValue(agentId, out var moduleRows) ? moduleRows : [];

                var components = new
                {
                    disks,
                    networkAdapters = adapters,
                    memoryModules = modules
                };

                var componentsJson = JsonSerializer.Serialize(components);

                Update.Table("agent_hardware_info")
                    .Set(new
                    {
                        hardware_components_json = componentsJson,
                        total_disks_count = disks.Count,
                        updated_at = DateTime.UtcNow
                    })
                    .Where(new { agent_id = agentId });
            }
        });
    }

    public override void Down()
    {
        Update.Table("agent_hardware_info")
            .Set(new
            {
                hardware_components_json = (string?)null,
                total_disks_count = (int?)null,
                updated_at = DateTime.UtcNow
            })
            .AllRows();
    }

    private static HashSet<Guid> LoadExistingHardwareAgents(IDbConnection connection, IDbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT agent_id FROM agent_hardware_info";

        var result = new HashSet<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static HashSet<Guid> LoadAgentsWithNullComponents(IDbConnection connection, IDbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT agent_id FROM agent_hardware_info WHERE hardware_components_json IS NULL";

        var result = new HashSet<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private static Dictionary<Guid, List<object>> LoadDisksByAgent(IDbConnection connection, IDbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT id, agent_id, drive_letter, label, file_system, total_size_bytes, free_space_bytes, media_type, collected_at
FROM disk_info";

        var result = new Dictionary<Guid, List<object>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var agentId = reader.GetGuid(1);
            if (!result.TryGetValue(agentId, out var rows))
            {
                rows = [];
                result[agentId] = rows;
            }

            rows.Add(new
            {
                id = reader.GetGuid(0),
                agentId,
                driveLetter = reader.GetString(2),
                label = reader.IsDBNull(3) ? null : reader.GetString(3),
                fileSystem = reader.IsDBNull(4) ? null : reader.GetString(4),
                totalSizeBytes = reader.GetInt64(5),
                freeSpaceBytes = reader.GetInt64(6),
                mediaType = reader.IsDBNull(7) ? null : reader.GetString(7),
                collectedAt = reader.GetDateTime(8)
            });
        }

        return result;
    }

    private static Dictionary<Guid, List<object>> LoadNetworkAdaptersByAgent(IDbConnection connection, IDbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT id, agent_id, name, mac_address, ip_address, subnet_mask, gateway, dns_servers, is_dhcp_enabled, adapter_type, speed, collected_at
FROM network_adapter_info";

        var result = new Dictionary<Guid, List<object>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var agentId = reader.GetGuid(1);
            if (!result.TryGetValue(agentId, out var rows))
            {
                rows = [];
                result[agentId] = rows;
            }

            rows.Add(new
            {
                id = reader.GetGuid(0),
                agentId,
                name = reader.GetString(2),
                macAddress = reader.IsDBNull(3) ? null : reader.GetString(3),
                ipAddress = reader.IsDBNull(4) ? null : reader.GetString(4),
                subnetMask = reader.IsDBNull(5) ? null : reader.GetString(5),
                gateway = reader.IsDBNull(6) ? null : reader.GetString(6),
                dnsServers = reader.IsDBNull(7) ? null : reader.GetString(7),
                isDhcpEnabled = reader.GetBoolean(8),
                adapterType = reader.IsDBNull(9) ? null : reader.GetString(9),
                speed = reader.IsDBNull(10) ? null : reader.GetString(10),
                collectedAt = reader.GetDateTime(11)
            });
        }

        return result;
    }

    private static Dictionary<Guid, List<object>> LoadMemoryModulesByAgent(IDbConnection connection, IDbTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT id, agent_id, slot, capacity_bytes, speed_mhz, memory_type, manufacturer, part_number, serial_number, collected_at
FROM memory_module_info";

        var result = new Dictionary<Guid, List<object>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var agentId = reader.GetGuid(1);
            if (!result.TryGetValue(agentId, out var rows))
            {
                rows = [];
                result[agentId] = rows;
            }

            rows.Add(new
            {
                id = reader.GetGuid(0),
                agentId,
                slot = reader.IsDBNull(2) ? null : reader.GetString(2),
                capacityBytes = reader.GetInt64(3),
                speedMhz = reader.IsDBNull(4) ? null : (int?)reader.GetInt32(4),
                memoryType = reader.IsDBNull(5) ? null : reader.GetString(5),
                manufacturer = reader.IsDBNull(6) ? null : reader.GetString(6),
                partNumber = reader.IsDBNull(7) ? null : reader.GetString(7),
                serialNumber = reader.IsDBNull(8) ? null : reader.GetString(8),
                collectedAt = reader.GetDateTime(9)
            });
        }

        return result;
    }
}
