using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Templates de relatório embutidos para o gerenciador de patches do Windows
/// (datasets 24–27) e para o inventário/estado dos agentes (28).
///
/// Cada template traz layout + execution_schema_json (filtros/presets) para que
/// o formulário de execução do console já ofereça escopo/período/ordenação e
/// presets prontos — sem isso o usuário precisaria montar o JSON de filtros.
///
/// IDs determinísticos a0000003-0000-0000-0000-00000000000N (deep-link estável
/// pelo mapa de slug no console) e Down idempotente.
/// </summary>
[Migration(20261220_232)]
public class M232_SeedPatchReportTemplates : Migration
{
    private const int WindowsUpdatesVal = 24;
    private const int PatchComplianceVal = 25;
    private const int PatchApprovalsVal = 26;
    private const int PatchApprovalAuditVal = 27;
    private const int AgentsVal = 28;

    private const string DefaultStyle = @",""style"":{""primaryColor"":""#16324F"",""headerBackgroundColor"":""#16324F"",""headerTextColor"":""#FFFFFF"",""alternateRowColor"":""#EEF4F7"",""fontFamily"":""Segoe UI, sans-serif""}";

    public override void Up()
    {
        var now = DateTime.UtcNow.ToString("O");

        Seed("a0000003-0000-0000-0000-000000000001",
            "Patches Pendentes por Agente",
            "Atualizações do Windows pendentes por agente, com severidade, classificação, aprovação e reinício.",
            WindowsUpdatesVal,
            @"{""title"":""Patches Pendentes por Agente"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}} ({{count}} atualizações)"",""hideGroupColumn"":true,""columns"":[{""field"":""kbIds"",""header"":""KB""},{""field"":""title"",""header"":""Atualização""},{""field"":""classification"",""header"":""Classificação""},{""field"":""severity"",""header"":""Severidade""},{""field"":""approvalState"",""header"":""Aprovação""},{""field"":""requiresReboot"",""header"":""Reinício""},{""field"":""publishedAt"",""header"":""Publicada"",""format"":""datetime""},{""field"":""reportedAt"",""header"":""Reportada"",""format"":""datetime""}],""groupDetails"":[{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""}],""groupSummaries"":[{""label"":""Pendentes"",""aggregate"":""count""}]" + DefaultStyle + "}",
            WindowsUpdatesSchema, now);

        Seed("a0000003-0000-0000-0000-000000000002",
            "Conformidade de Patches",
            "Uma linha por agente: pendentes acionáveis, críticas, recusadas, aprovadas, instaladas e reinício pendente.",
            PatchComplianceVal,
            @"{""title"":""Conformidade de Patches"",""orientation"":""landscape"",""columns"":[{""field"":""agentHostname"",""header"":""Agente""},{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""},{""field"":""osName"",""header"":""SO""},{""field"":""pendingCount"",""header"":""Pendentes"",""format"":""number""},{""field"":""criticalCount"",""header"":""Críticas"",""format"":""number""},{""field"":""deniedCount"",""header"":""Recusadas"",""format"":""number""},{""field"":""approvedCount"",""header"":""Aprovadas"",""format"":""number""},{""field"":""installedCount"",""header"":""Instaladas"",""format"":""number""},{""field"":""pendingReboot"",""header"":""Reinício pendente""},{""field"":""scanStatus"",""header"":""Scan""},{""field"":""lastScanAt"",""header"":""Último scan"",""format"":""datetime""}],""summaries"":[{""label"":""Agentes"",""aggregate"":""count""},{""label"":""Pendentes (total)"",""field"":""pendingCount"",""aggregate"":""sum""},{""label"":""Críticas (total)"",""field"":""criticalCount"",""aggregate"":""sum""}]" + DefaultStyle + "}",
            PatchComplianceSchema, now);

        Seed("a0000003-0000-0000-0000-000000000003",
            "Aprovações de Patches",
            "Regras de aprovação/recusa de atualizações por escopo, com motivo e responsável.",
            PatchApprovalsVal,
            @"{""title"":""Aprovações de Patches"",""orientation"":""landscape"",""columns"":[{""field"":""scopeType"",""header"":""Escopo""},{""field"":""updateKey"",""header"":""Atualização""},{""field"":""kbId"",""header"":""KB""},{""field"":""action"",""header"":""Ação""},{""field"":""reason"",""header"":""Motivo""},{""field"":""updatedBy"",""header"":""Alterado por""},{""field"":""updatedAt"",""header"":""Atualizada em"",""format"":""datetime""}],""summaries"":[{""label"":""Regras"",""aggregate"":""count""}]" + DefaultStyle + "}",
            PatchApprovalsSchema, now);

        Seed("a0000003-0000-0000-0000-000000000004",
            "Auditoria de Aprovações de Patches",
            "Histórico de criação/alteração/remoção de aprovações (usuário, IP, motivo).",
            PatchApprovalAuditVal,
            @"{""title"":""Auditoria de Aprovações de Patches"",""orientation"":""landscape"",""columns"":[{""field"":""changedAt"",""header"":""Quando"",""format"":""datetime""},{""field"":""changeType"",""header"":""Mudança""},{""field"":""scopeType"",""header"":""Escopo""},{""field"":""updateKey"",""header"":""Atualização""},{""field"":""oldAction"",""header"":""Ação anterior""},{""field"":""newAction"",""header"":""Nova ação""},{""field"":""reason"",""header"":""Motivo""},{""field"":""changedBy"",""header"":""Por""},{""field"":""ipAddress"",""header"":""IP""}],""summaries"":[{""label"":""Eventos"",""aggregate"":""count""}]" + DefaultStyle + "}",
            PatchApprovalAuditSchema, now);

        Seed("a0000003-0000-0000-0000-000000000005",
            "Inventário de Agentes",
            "Frota de agentes com status, SO, versão do agente, usuário logado e reinício pendente.",
            AgentsVal,
            @"{""title"":""Inventário de Agentes"",""orientation"":""landscape"",""columns"":[{""field"":""hostname"",""header"":""Hostname""},{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""},{""field"":""status"",""header"":""Status""},{""field"":""osName"",""header"":""SO""},{""field"":""osVersion"",""header"":""Versão SO""},{""field"":""agentVersion"",""header"":""Agente""},{""field"":""loggedUser"",""header"":""Usuário""},{""field"":""lastSeenAt"",""header"":""Último contato"",""format"":""datetime""},{""field"":""pendingReboot"",""header"":""Reinício pendente""}],""summaries"":[{""label"":""Agentes"",""aggregate"":""count""}]" + DefaultStyle + "}",
            AgentsSchema, now);
    }

    public override void Down()
    {
        for (var i = 1; i <= 5; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000003-0000-0000-0000-00000000000{i:D1}") });
        }
    }

    private void Seed(string id, string name, string description, int datasetType, string layoutJson, string executionSchemaJson, string now)
    {
        Insert.IntoTable("report_templates").Row(new
        {
            id = Guid.Parse(id),
            name,
            description,
            dataset_type = datasetType,
            default_format = 0,
            layout_json = layoutJson,
            filters_json = (string?)null,
            execution_schema_json = executionSchemaJson,
            is_active = true,
            is_built_in = true,
            version = 1,
            created_at = now,
            updated_at = now
        });
    }

    private static string ScopeFilters => @"
    { ""name"": ""clientId"", ""label"": ""Cliente"", ""type"": 3, ""required"": false, ""group"": ""Escopo"", ""description"": ""Escopo opcional por cliente."", ""uiComponent"": 4, ""placeholder"": ""GUID do cliente"" },
    { ""name"": ""siteId"", ""label"": ""Site"", ""type"": 3, ""required"": false, ""group"": ""Escopo"", ""description"": ""Escopo opcional por site."", ""uiComponent"": 4, ""dependsOn"": ""clientId"", ""placeholder"": ""GUID do site"" },
    { ""name"": ""agentId"", ""label"": ""Agente"", ""type"": 3, ""required"": false, ""group"": ""Escopo"", ""description"": ""Escopo opcional por agente."", ""uiComponent"": 4, ""dependsOn"": ""siteId"", ""placeholder"": ""GUID do agente"" },";

    private static string OutputFilters(string defaultOrder, string[] allowedOrders) => @"
    { ""name"": ""orderBy"", ""label"": ""Ordenar por"", ""type"": 2, ""required"": false, ""group"": ""Ordenacao"", ""description"": ""Campo para ordenacao dos resultados."", ""uiComponent"": 2, ""allowedValues"": [" + string.Join(", ", allowedOrders.Select(item => "\"" + item + "\"")) + @"], ""defaultValue"": """ + defaultOrder + @""" },
    { ""name"": ""orderDirection"", ""label"": ""Direcao"", ""type"": 2, ""required"": false, ""group"": ""Ordenacao"", ""description"": ""Direcao da ordenacao."", ""uiComponent"": 2, ""allowedValues"": [""asc"", ""desc""], ""defaultValue"": ""asc"" },
    { ""name"": ""orientation"", ""label"": ""Orientacao"", ""type"": 2, ""required"": false, ""group"": ""Formatacao"", ""description"": ""Orientacao da pagina do relatorio."", ""uiComponent"": 2, ""allowedValues"": [""landscape"", ""portrait""], ""defaultValue"": ""landscape"" },
    { ""name"": ""limit"", ""label"": ""Limite de linhas"", ""type"": 4, ""required"": false, ""group"": ""Saida"", ""description"": ""Maximo recomendado: 10000."", ""uiComponent"": 5, ""defaultValue"": ""1000"", ""min"": 1, ""max"": 10000 }";

    private static string WindowsUpdatesSchema => @"
{
  ""scopeType"": 3,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""agentHostname"", ""severity"", ""title"", ""reportedAt"", ""publishedAt"", ""siteName""],
  ""defaultSortField"": ""agentHostname"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""asc"",
  ""filters"": [
    " + ScopeFilters + @"
    { ""name"": ""onlyPending"", ""label"": ""Somente pendentes"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Exibe apenas atualizações ainda pendentes."", ""uiComponent"": 8, ""defaultValue"": ""true"" },
    { ""name"": ""severity"", ""label"": ""Severidade"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Severidade reportada pelo WUA."", ""uiComponent"": 2, ""allowedValues"": [""Critical"", ""Important"", ""Moderate"", ""Low""] },
    { ""name"": ""approvalState"", ""label"": ""Aprovação"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Estado efetivo de aprovação."", ""uiComponent"": 2, ""allowedValues"": [""Unknown"", ""Approved"", ""Denied"", ""AutoApproved""] },
    { ""name"": ""status"", ""label"": ""Status"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Status da linha de inventário."", ""uiComponent"": 2, ""allowedValues"": [""Pending"", ""Approved"", ""Denied"", ""Installing"", ""Installed"", ""Failed"", ""Superseded""] },
    { ""name"": ""updateKey"", ""label"": ""Chave da atualização"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial por updateKey (guid:... ou kb:KBxxxx)."", ""uiComponent"": 1, ""maxLength"": 120, ""isPartialMatch"": true },
    { ""name"": ""kb"", ""label"": ""KB"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pelo número do KB."", ""uiComponent"": 1, ""maxLength"": 20, ""isPartialMatch"": true },
    { ""name"": ""from"", ""label"": ""Reportada a partir de"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Filtro pela data de reporte no agente."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Reportada até"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Limite superior da data de reporte."", ""uiComponent"": 7 },
    " + OutputFilters("agentHostname", ["agentHostname","severity","title","reportedAt","publishedAt","siteName"]) + @"
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Pendências (todas)"", ""description"": ""Patches pendentes de todos os agentes, por severidade"", ""filtersJson"": ""{ \""onlyPending\"": true, \""orderBy\"": \""severity\"", \""orderDirection\"": \""asc\"" }"" },
    { ""name"": ""Somente críticas"", ""description"": ""Patches pendentes com severidade Critical"", ""filtersJson"": ""{ \""onlyPending\"": true, \""severity\"": \""Critical\"" }"" },
    { ""name"": ""Recusadas"", ""description"": ""Atualizações recusadas pelos operadores"", ""filtersJson"": ""{ \""approvalState\"": \""Denied\"" }"" }
  ]
}";

    private static string PatchComplianceSchema => @"
{
  ""scopeType"": 3,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""agentHostname"", ""siteName"", ""pendingCount"", ""criticalCount"", ""lastScanAt""],
  ""defaultSortField"": ""agentHostname"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""asc"",
  ""filters"": [
    " + ScopeFilters + @"
    { ""name"": ""onlyWithPending"", ""label"": ""Somente com pendências"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Exibe apenas agentes com pendências acionáveis."", ""uiComponent"": 8 },
    { ""name"": ""pendingReboot"", ""label"": ""Reinício pendente"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Somente agentes com reinício pendente."", ""uiComponent"": 8 },
    { ""name"": ""scanStatus"", ""label"": ""Status do scan"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Status da última varredura de patches."", ""uiComponent"": 2, ""allowedValues"": [""ok"", ""partial"", ""error"", ""timeout"", ""unsupported""] },
    { ""name"": ""from"", ""label"": ""Último scan a partir de"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Filtro pelo último scan."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Último scan até"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Limite superior do último scan."", ""uiComponent"": 7 },
    " + OutputFilters("agentHostname", ["agentHostname","siteName","pendingCount","criticalCount","lastScanAt"]) + @"
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Com pendências"", ""description"": ""Apenas agentes com pendências acionáveis"", ""filtersJson"": ""{ \""onlyWithPending\"": true, \""orderBy\"": \""pendingCount\"", \""orderDirection\"": \""desc\"" }"" },
    { ""name"": ""Reinício pendente"", ""description"": ""Agentes aguardando reinício"", ""filtersJson"": ""{ \""pendingReboot\"": true }"" }
  ]
}";

    private static string PatchApprovalsSchema => @"
{
  ""scopeType"": 0,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""updatedAt""],
  ""defaultSortField"": ""updatedAt"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""desc"",
  ""filters"": [
    { ""name"": ""scopeType"", ""label"": ""Escopo"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Escopo da regra."", ""uiComponent"": 2, ""allowedValues"": [""Global"", ""Client"", ""Site"", ""Agent""] },
    { ""name"": ""action"", ""label"": ""Ação"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Aprovar ou recusar."", ""uiComponent"": 2, ""allowedValues"": [""Approve"", ""Deny""] },
    { ""name"": ""updateKey"", ""label"": ""Atualização"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pela chave da atualização."", ""uiComponent"": 1, ""maxLength"": 120, ""isPartialMatch"": true },
    { ""name"": ""kb"", ""label"": ""KB"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pelo KB."", ""uiComponent"": 1, ""maxLength"": 20, ""isPartialMatch"": true },
    { ""name"": ""changedBy"", ""label"": ""Alterado por"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Usuário que criou/alterou a regra."", ""uiComponent"": 1, ""maxLength"": 200, ""isPartialMatch"": true },
    { ""name"": ""from"", ""label"": ""Atualizada a partir de"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Filtro pela data de atualização."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Atualizada até"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Limite superior da data de atualização."", ""uiComponent"": 7 },
    { ""name"": ""orientation"", ""label"": ""Orientacao"", ""type"": 2, ""required"": false, ""group"": ""Formatacao"", ""description"": ""Orientacao da pagina do relatorio."", ""uiComponent"": 2, ""allowedValues"": [""landscape"", ""portrait""], ""defaultValue"": ""landscape"" },
    { ""name"": ""limit"", ""label"": ""Limite de linhas"", ""type"": 4, ""required"": false, ""group"": ""Saida"", ""description"": ""Maximo recomendado: 10000."", ""uiComponent"": 5, ""defaultValue"": ""1000"", ""min"": 1, ""max"": 10000 }
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Todas as regras"", ""description"": ""Regras ativas em todos os escopos"", ""filtersJson"": ""{ \""limit\"": 5000 }"" },
    { ""name"": ""Somente recusadas"", ""description"": ""Regras de recusa"", ""filtersJson"": ""{ \""action\"": \""Deny\"" }"" }
  ]
}";

    private static string PatchApprovalAuditSchema => @"
{
  ""scopeType"": 0,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""changedAt""],
  ""defaultSortField"": ""changedAt"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""desc"",
  ""filters"": [
    { ""name"": ""from"", ""label"": ""Data inicial"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Inicio do intervalo (opcional)."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Data final"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Fim do intervalo (opcional)."", ""uiComponent"": 7 },
    { ""name"": ""changeType"", ""label"": ""Tipo de mudança"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Criação, alteração ou remoção."", ""uiComponent"": 2, ""allowedValues"": [""Created"", ""Updated"", ""Deleted""] },
    { ""name"": ""scopeType"", ""label"": ""Escopo"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Escopo da regra."", ""uiComponent"": 2, ""allowedValues"": [""Global"", ""Client"", ""Site"", ""Agent""] },
    { ""name"": ""action"", ""label"": ""Nova ação"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Aprovar ou recusar."", ""uiComponent"": 2, ""allowedValues"": [""Approve"", ""Deny""] },
    { ""name"": ""updateKey"", ""label"": ""Atualização"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pela chave da atualização."", ""uiComponent"": 1, ""maxLength"": 120, ""isPartialMatch"": true },
    { ""name"": ""changedBy"", ""label"": ""Alterado por"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Usuário responsável."", ""uiComponent"": 1, ""maxLength"": 200, ""isPartialMatch"": true },
    { ""name"": ""limit"", ""label"": ""Limite de linhas"", ""type"": 4, ""required"": false, ""group"": ""Saida"", ""description"": ""Maximo recomendado: 10000."", ""uiComponent"": 5, ""defaultValue"": ""1000"", ""min"": 1, ""max"": 10000 }
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Somente recusas"", ""description"": ""Eventos de recusa de atualizações"", ""filtersJson"": ""{ \""action\"": \""Deny\"" }"" },
    { ""name"": ""Somente criação de regras"", ""description"": ""Eventos de criação"", ""filtersJson"": ""{ \""changeType\"": \""Created\"" }"" }
  ]
}";

    private static string AgentsSchema => @"
{
  ""scopeType"": 3,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""hostname"", ""siteName"", ""status"", ""lastSeenAt"", ""osName""],
  ""defaultSortField"": ""hostname"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""asc"",
  ""filters"": [
    " + ScopeFilters + @"
    { ""name"": ""status"", ""label"": ""Status"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Status efetivo do agente."", ""uiComponent"": 2, ""allowedValues"": [""online"", ""offline"", ""maintenance"", ""error""] },
    { ""name"": ""pendingReboot"", ""label"": ""Reinício pendente"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Somente agentes com reinício pendente."", ""uiComponent"": 8 },
    { ""name"": ""zeroTouchPending"", ""label"": ""Aguardando aprovação"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Somente agentes zero-touch pendentes."", ""uiComponent"": 8 },
    { ""name"": ""maintenance"", ""label"": ""Em manutenção"", ""type"": 8, ""required"": false, ""group"": ""Filtros"", ""description"": ""Somente agentes em manutenção."", ""uiComponent"": 8 },
    { ""name"": ""osName"", ""label"": ""SO"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pelo nome do SO."", ""uiComponent"": 1, ""maxLength"": 120, ""isPartialMatch"": true },
    { ""name"": ""from"", ""label"": ""Último contato a partir de"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Filtro pelo último contato."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Último contato até"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Limite superior do último contato."", ""uiComponent"": 7 },
    " + OutputFilters("hostname", ["hostname","siteName","status","lastSeenAt","osName"]) + @"
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Somente online"", ""description"": ""Agentes online"", ""filtersJson"": ""{ \""status\"": \""online\"" }"" },
    { ""name"": ""Reinício pendente"", ""description"": ""Agentes aguardando reinício"", ""filtersJson"": ""{ \""pendingReboot\"": true }"" }
  ]
}";
}
