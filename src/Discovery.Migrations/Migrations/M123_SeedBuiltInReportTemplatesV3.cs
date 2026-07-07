using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seeds 12 new built-in report templates covering new dataset types (V3).
/// Uses deterministic GUIDs: a0000002-0000-0000-0000-000000000001..12
/// </summary>
[Migration(20260706_123)]
public class M123_SeedBuiltInReportTemplatesV3 : Migration
{
    private const int AgentMonitoringEventsVal = 9;
    private const int AgentAlertsVal = 10;
    private const int P2pTelemetryVal = 11;
    private const int AgentDisksVal = 12;
    private const int NetworkAdaptersVal = 13;
    private const int ListeningPortsVal = 14;
    private const int PrintersVal = 15;
    private const int SoftwareCatalogVal = 16;
    private const int AutomationScriptsVal = 17;
    private const int AppPackagesVal = 18;
    private const int TicketActivityVal = 19;
    private const int KnowledgeBaseVal = 22;

    private const string DefaultStyle = @",""style"":{""primaryColor"":""#16324F"",""headerBackgroundColor"":""#16324F"",""headerTextColor"":""#FFFFFF"",""alternateRowColor"":""#EEF4F7"",""fontFamily"":""Segoe UI, sans-serif""}";

    public override void Up()
    {
        var now = DateTime.UtcNow.ToString("O");

        Seed("a0000002-0000-0000-0000-000000000001",
            "Alertas por Agente", "Alertas de monitoramento agrupados por severidade e agente.", AgentMonitoringEventsVal,
            @"{""title"":""Alertas por Agente"",""orientation"":""landscape"",""groupBy"":""severity"",""groupTitleTemplate"":""{{value}} ({{count}} alertas)"",""hideGroupColumn"":true,""columns"":[{""field"":""alertCode"",""header"":""Codigo""},{""field"":""title"",""header"":""Titulo""},{""field"":""message"",""header"":""Mensagem""},{""field"":""metricValue"",""header"":""Valor Metrica"",""format"":""number""},{""field"":""occurredAt"",""header"":""Ocorrencia"",""format"":""datetime""}],""summaries"":[{""label"":""Total Alertas"",""aggregate"":""count""},{""label"":""Codigos Distintos"",""field"":""alertCode"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000002",
            "Alertas Criticos (24h)", "Alertas de severidade critica das ultimas 24 horas.", AgentMonitoringEventsVal,
            @"{""title"":""Alertas Criticos — Ultimas 24h"",""orientation"":""landscape"",""columns"":[{""field"":""severity"",""header"":""Severidade""},{""field"":""alertCode"",""header"":""Codigo""},{""field"":""title"",""header"":""Titulo""},{""field"":""message"",""header"":""Mensagem""},{""field"":""metricValue"",""header"":""Valor"",""format"":""number""},{""field"":""occurredAt"",""header"":""Data/Hora"",""format"":""datetime""}],""summaries"":[{""label"":""Total"",""aggregate"":""count""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000003",
            "Telemetria P2P", "Metricas de performance da rede P2P.", P2pTelemetryVal,
            @"{""title"":""Telemetria P2P"",""orientation"":""landscape"",""columns"":[{""field"":""collectedAt"",""header"":""Coleta"",""format"":""datetime""},{""field"":""hostCpuPercent"",""header"":""CPU %"",""format"":""number""},{""field"":""hostMemoryPercent"",""header"":""Memoria %"",""format"":""number""},{""field"":""bytesServed"",""header"":""Bytes Servidos"",""format"":""bytes""},{""field"":""connectedPeers"",""header"":""Peers"",""format"":""number""},{""field"":""replicationsSucceeded"",""header"":""Replicacoes OK"",""format"":""number""},{""field"":""replicationsFailed"",""header"":""Falhas"",""format"":""number""}],""summaries"":[{""label"":""Snapshots"",""aggregate"":""count""},{""label"":""Bytes Totais"",""field"":""bytesServed"",""aggregate"":""sum""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000004",
            "Auditoria de Portas", "Portas TCP/UDP abertas por agente.", ListeningPortsVal,
            @"{""title"":""Auditoria de Portas"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}} ({{count}} portas)"",""hideGroupColumn"":true,""columns"":[{""field"":""port"",""header"":""Porta"",""format"":""number""},{""field"":""protocol"",""header"":""Protocolo""},{""field"":""processName"",""header"":""Processo""},{""field"":""state"",""header"":""Estado""}],""groupDetails"":[{""field"":""siteName"",""header"":""Site""}],""groupSummaries"":[{""label"":""Portas"",""aggregate"":""count""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000005",
            "Inventario de Rede", "Adaptadores de rede por agente.", NetworkAdaptersVal,
            @"{""title"":""Inventario de Rede"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}}"",""hideGroupColumn"":true,""columns"":[{""field"":""adapterName"",""header"":""Adaptador""},{""field"":""macAddress"",""header"":""MAC""},{""field"":""ipAddresses"",""header"":""IPs""},{""field"":""speedMbps"",""header"":""Velocidade (Mbps)"",""format"":""number""},{""field"":""isDefault"",""header"":""Padrao""}],""groupDetails"":[{""field"":""siteName"",""header"":""Site""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000006",
            "Inventario de Discos", "Discos por agente com capacidade e tipo.", AgentDisksVal,
            @"{""title"":""Inventario de Discos"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}}"",""hideGroupColumn"":true,""columns"":[{""field"":""diskName"",""header"":""Disco""},{""field"":""sizeBytes"",""header"":""Capacidade"",""format"":""bytes""},{""field"":""freeBytes"",""header"":""Livre"",""format"":""bytes""},{""field"":""fileSystem"",""header"":""Sistema Arquivos""},{""field"":""type"",""header"":""Tipo""},{""field"":""interface"",""header"":""Interface""},{""field"":""healthStatus"",""header"":""Saude""}],""groupDetails"":[{""field"":""siteName"",""header"":""Site""}],""groupSummaries"":[{""label"":""Discos"",""aggregate"":""count""},{""label"":""Capacidade Total"",""field"":""sizeBytes"",""aggregate"":""sum""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000007",
            "Inventario de Impressoras", "Impressoras instaladas por agente.", PrintersVal,
            @"{""title"":""Inventario de Impressoras"",""orientation"":""portrait"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}}"",""hideGroupColumn"":true,""columns"":[{""field"":""printerName"",""header"":""Impressora""},{""field"":""driverName"",""header"":""Driver""},{""field"":""portName"",""header"":""Porta""},{""field"":""isDefault"",""header"":""Padrao""},{""field"":""isShared"",""header"":""Compartilhada""}],""groupDetails"":[{""field"":""siteName"",""header"":""Site""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000008",
            "Catalogo de Software", "Catalogo com EOL, categoria e licenciamento.", SoftwareCatalogVal,
            @"{""title"":""Catalogo de Software"",""orientation"":""portrait"",""columns"":[{""field"":""name"",""header"":""Nome""},{""field"":""publisher"",""header"":""Fabricante""},{""field"":""category"",""header"":""Categoria""},{""field"":""latestVersion"",""header"":""Versao Atual""},{""field"":""isEol"",""header"":""EOL""},{""field"":""licenseType"",""header"":""Licenca""}],""summaries"":[{""label"":""Total Softwares"",""aggregate"":""count""},{""label"":""Categorias"",""field"":""category"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000009",
            "Scripts de Automacao", "Scripts de automacao com linguagem e status.", AutomationScriptsVal,
            @"{""title"":""Scripts de Automacao"",""orientation"":""portrait"",""columns"":[{""field"":""name"",""header"":""Nome""},{""field"":""language"",""header"":""Linguagem""},{""field"":""isActive"",""header"":""Ativo""},{""field"":""createdAt"",""header"":""Criado em"",""format"":""datetime""},{""field"":""updatedAt"",""header"":""Atualizado"",""format"":""datetime""}],""summaries"":[{""label"":""Total Scripts"",""aggregate"":""count""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000010",
            "Loja de Aplicativos", "Pacotes Chocolatey/Winget disponiveis.", AppPackagesVal,
            @"{""title"":""Loja de Aplicativos"",""orientation"":""portrait"",""columns"":[{""field"":""name"",""header"":""Nome""},{""field"":""publisher"",""header"":""Fabricante""},{""field"":""version"",""header"":""Versao""},{""field"":""source"",""header"":""Origem""},{""field"":""category"",""header"":""Categoria""},{""field"":""isActive"",""header"":""Ativo""}],""summaries"":[{""label"":""Total Pacotes"",""aggregate"":""count""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000011",
            "Auditoria de Chamados", "Log de atividades em chamados.", TicketActivityVal,
            @"{""title"":""Auditoria de Chamados"",""orientation"":""landscape"",""columns"":[{""field"":""action"",""header"":""Acao""},{""field"":""changedBy"",""header"":""Usuario""},{""field"":""oldValue"",""header"":""Valor Anterior""},{""field"":""newValue"",""header"":""Novo Valor""},{""field"":""createdAt"",""header"":""Data/Hora"",""format"":""datetime""}],""summaries"":[{""label"":""Total Atividades"",""aggregate"":""count""},{""label"":""Usuarios Distintos"",""field"":""changedBy"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}", now);

        Seed("a0000002-0000-0000-0000-000000000012",
            "Base de Conhecimento", "Artigos por status e categoria.", KnowledgeBaseVal,
            @"{""title"":""Base de Conhecimento"",""orientation"":""portrait"",""columns"":[{""field"":""title"",""header"":""Titulo""},{""field"":""category"",""header"":""Categoria""},{""field"":""status"",""header"":""Status""},{""field"":""author"",""header"":""Autor""},{""field"":""updatedAt"",""header"":""Atualizado"",""format"":""datetime""}],""summaries"":[{""label"":""Total Artigos"",""aggregate"":""count""},{""label"":""Categorias"",""field"":""category"",""aggregate"":""countDistinct""}]" + DefaultStyle + "}", now);
    }

    public override void Down()
    {
        for (var i = 1; i <= 12; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000002-0000-0000-0000-00000000000{i:D1}") });
        }
    }

    private void Seed(string id, string name, string description, int datasetType, string layoutJson, string now)
    {
        Insert.IntoTable("report_templates").Row(new
        {
            id = Guid.Parse(id),
            name,
            description,
            dataset_type = datasetType,
            default_format = 0,
            layout_json = layoutJson,
            is_active = true,
            is_built_in = true,
            version = 1,
            created_at = now,
            updated_at = now
        });
    }
}
