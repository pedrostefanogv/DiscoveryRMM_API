using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307_034, "Seed predefined report templates")]
public class M034_SeedReportTemplates : Migration
{
  private bool HasLegacySeededTemplates()
  {
    var hasSeed = false;

    Execute.WithConnection((connection, transaction) =>
    {
      using var command = connection.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "SELECT 1 FROM report_templates WHERE created_by = 'migration' LIMIT 1";

      var result = command.ExecuteScalar();
      hasSeed = result is not null and not DBNull;
    });

    return hasSeed;
  }

  public override void Up()
  {
    // Compatibilidade: evita duplicar seeds em bases que já executaram a versão legada (34).
    if (HasLegacySeededTemplates())
    {
      return;
    }

    Insert.IntoTable("report_templates").Row(new
    {
      id = Guid.NewGuid(),
      client_id = DBNull.Value,
      name = "Software Inventory - All Clients",
      description = "Comprehensive list of installed software across all agents, including version and publisher information",
      dataset_type = 0, // SoftwareInventory
      default_format = 0, // Xlsx
      layout_json = @"{
  ""title"": ""Software Inventory Report"",
  ""orientation"": ""landscape"",
  ""pageSize"": 100,
  ""columns"": [
    {""field"": ""clientId"", ""width"": 20, ""header"": ""Client""},
    {""field"": ""siteId"", ""width"": 20, ""header"": ""Site""},
    {""field"": ""agentId"", ""width"": 20, ""header"": ""Agent""},
    {""field"": ""softwareName"", ""width"": 30, ""header"": ""Software""},
    {""field"": ""publisher"", ""width"": 25, ""header"": ""Publisher""},
    {""field"": ""version"", ""width"": 15, ""header"": ""Version""},
    {""field"": ""installedAt"", ""format"": ""datetime"", ""width"": 20, ""header"": ""Installed""}
  ]
}",
      filters_json = @"{
  ""limit"": 5000,
  ""orderBy"": ""installedAt"",
  ""orderDirection"": ""DESC""
}",
      is_active = true,
      version = 1,
      created_at = DateTime.UtcNow,
      updated_at = DateTime.UtcNow,
      created_by = "migration",
      updated_by = "migration"
    });

    Insert.IntoTable("report_templates").Row(new
    {
      id = Guid.NewGuid(),
      client_id = DBNull.Value,
      name = "System Logs - Last 7 Days",
      description = "Recent system logs filtered by error and warning levels, useful for troubleshooting and compliance audits",
      dataset_type = 1, // Logs
      default_format = 0, // Xlsx
      layout_json = @"{
  ""title"": ""System Logs Report"",
  ""pageSize"": 1000,
  ""columns"": [
    {""field"": ""clientId"", ""width"": 20, ""header"": ""Client""},
    {""field"": ""siteId"", ""width"": 20, ""header"": ""Site""},
    {""field"": ""agentId"", ""width"": 20, ""header"": ""Agent""},
    {""field"": ""type"", ""width"": 15, ""header"": ""Type""},
    {""field"": ""level"", ""width"": 12, ""header"": ""Level""},
    {""field"": ""source"", ""width"": 20, ""header"": ""Source""},
    {""field"": ""message"", ""width"": 50, ""header"": ""Message""},
    {""field"": ""timestamp"", ""format"": ""datetime"", ""width"": 20, ""header"": ""Timestamp""}
  ]
}",
      filters_json = @"{
  ""level"": [""Error"", ""Warning""],
  ""daysBack"": 7,
  ""limit"": 10000,
  ""orderBy"": ""timestamp"",
  ""orderDirection"": ""DESC""
}",
      is_active = true,
      version = 1,
      created_at = DateTime.UtcNow,
      updated_at = DateTime.UtcNow,
      created_by = "migration",
      updated_by = "migration"
    });

    Insert.IntoTable("report_templates").Row(new
    {
      id = Guid.NewGuid(),
      client_id = DBNull.Value,
      name = "Configuration Changes - Monthly",
      description = "Tracks all configuration modifications for compliance and change management purposes",
      dataset_type = 2, // ConfigurationAudit
      default_format = 0, // Xlsx
      layout_json = @"{
  ""title"": ""Configuration Audit Report"",
  ""pageSize"": 500,
  ""columns"": [
    {""field"": ""entityType"", ""width"": 20, ""header"": ""Entity Type""},
    {""field"": ""entityId"", ""width"": 20, ""header"": ""Entity ID""},
    {""field"": ""fieldName"", ""width"": 25, ""header"": ""Field Changed""},
    {""field"": ""oldValue"", ""width"": 25, ""header"": ""Old Value""},
    {""field"": ""newValue"", ""width"": 25, ""header"": ""New Value""},
    {""field"": ""changedBy"", ""width"": 20, ""header"": ""Changed By""},
    {""field"": ""changedAt"", ""format"": ""datetime"", ""width"": 20, ""header"": ""Changed At""},
    {""field"": ""reason"", ""width"": 30, ""header"": ""Reason""}
  ]
}",
      filters_json = @"{
  ""daysBack"": 30,
  ""limit"": 10000,
  ""orderBy"": ""changedAt"",
  ""orderDirection"": ""DESC""
}",
      is_active = true,
      version = 1,
      created_at = DateTime.UtcNow,
      updated_at = DateTime.UtcNow,
      created_by = "migration",
      updated_by = "migration"
    });

    Insert.IntoTable("report_templates").Row(new
    {
      id = Guid.NewGuid(),
      client_id = DBNull.Value,
      name = "Open Tickets - Priority View",
      description = "Overview of open tickets sorted by priority and SLA status, ideal for incident management dashboards",
      dataset_type = 3, // Tickets
      default_format = 0, // Xlsx
      layout_json = @"{
  ""title"": ""Tickets Report"",
  ""pageSize"": 500,
  ""columns"": [
    {""field"": ""clientId"", ""width"": 20, ""header"": ""Client""},
    {""field"": ""siteId"", ""width"": 20, ""header"": ""Site""},
    {""field"": ""agentId"", ""width"": 20, ""header"": ""Agent""},
    {""field"": ""workflowStateId"", ""width"": 15, ""header"": ""Status""},
    {""field"": ""priority"", ""width"": 12, ""header"": ""Priority""},
    {""field"": ""createdAt"", ""format"": ""datetime"", ""width"": 18, ""header"": ""Created""},
    {""field"": ""closedAt"", ""format"": ""datetime"", ""width"": 18, ""header"": ""Closed""},
    {""field"": ""slaBreached"", ""width"": 12, ""header"": ""SLA Breached""}
  ]
}",
      filters_json = @"{
  ""status"": [""Open"", ""InProgress""],
  ""limit"": 5000,
  ""orderBy"": ""priority"",
  ""orderDirection"": ""ASC""
}",
      is_active = true,
      version = 1,
      created_at = DateTime.UtcNow,
      updated_at = DateTime.UtcNow,
      created_by = "migration",
      updated_by = "migration"
    });

    Insert.IntoTable("report_templates").Row(new
    {
      id = Guid.NewGuid(),
      client_id = DBNull.Value,
      name = "Agent Hardware Inventory",
      description = "Detailed hardware specifications of all agents including OS, processor, memory, motherboard, and BIOS information",
      dataset_type = 4, // AgentHardware
      default_format = 0, // Xlsx
      layout_json = @"{
  ""title"": ""Inventário de Hardware dos Agentes"",
  ""subtitle"": ""Relatório Detalhado de Infraestrutura e Recursos"",
  ""pageSize"": 30,
  ""orientation"": ""landscape"",
  ""sections"": [
    {
      ""title"": ""Informações de Localização"",
      ""columnGroup"": ""location"",
      ""columns"": [
        {""field"": ""siteName"", ""width"": 18, ""header"": ""Site""},
        {""field"": ""agentHostname"", ""width"": 18, ""header"": ""Hostname""}
      ]
    },
    {
      ""title"": ""Sistema Operacional"",
      ""columnGroup"": ""os"",
      ""columns"": [
        {""field"": ""osName"", ""width"": 16, ""header"": ""SO""},
        {""field"": ""osVersion"", ""width"": 12, ""header"": ""Versão""},
        {""field"": ""osBuild"", ""width"": 10, ""header"": ""Build""},
        {""field"": ""osArchitecture"", ""width"": 10, ""header"": ""Arquitetura""}
      ]
    },
    {
      ""title"": ""Processador"",
      ""columnGroup"": ""processor"",
      ""columns"": [
        {""field"": ""processor"", ""width"": 20, ""header"": ""Modelo""},
        {""field"": ""processorCores"", ""width"": 8, ""header"": ""Cores"", ""format"": ""number""},
        {""field"": ""processorThreads"", ""width"": 8, ""header"": ""Threads"", ""format"": ""number""},
        {""field"": ""processorArchitecture"", ""width"": 10, ""header"": ""Arquitetura""}
      ]
    },
    {
      ""title"": ""Memória RAM"",
      ""columnGroup"": ""memory"",
      ""columns"": [
        {""field"": ""totalMemoryGB"", ""width"": 12, ""header"": ""Capacidade (GB)"", ""format"": ""number""}
      ]
    },
    {
      ""title"": ""Placa-mãe"",
      ""columnGroup"": ""motherboard"",
      ""columns"": [
        {""field"": ""motherboardManufacturer"", ""width"": 14, ""header"": ""Fabricante""},
        {""field"": ""motherboardModel"", ""width"": 16, ""header"": ""Modelo""}
      ]
    },
    {
      ""title"": ""BIOS/Firmware"",
      ""columnGroup"": ""bios"",
      ""columns"": [
        {""field"": ""biosManufacturer"", ""width"": 12, ""header"": ""Fabricante""},
        {""field"": ""biosVersion"", ""width"": 16, ""header"": ""Versão""}
      ]
    },
    {
      ""title"": ""Metadata"",
      ""columnGroup"": ""metadata"",
      ""columns"": [
        {""field"": ""collectedAt"", ""width"": 18, ""header"": ""Data da Coleta"", ""format"": ""datetime""}
      ]
    }
  ]
}",
      filters_json = @"{
  ""limit"": 5000,
  ""orderBy"": ""siteName"",
  ""orderDirection"": ""asc"",
  ""orientation"": ""landscape""
}",
      is_active = true,
      version = 1,
      created_at = DateTime.UtcNow,
      updated_at = DateTime.UtcNow,
      created_by = "migration",
      updated_by = "migration"
    });
  }

  public override void Down()
  {
    // Remove seeded templates by created_by field
    Delete.FromTable("report_templates").Row(new { created_by = "migration" });
  }
}
