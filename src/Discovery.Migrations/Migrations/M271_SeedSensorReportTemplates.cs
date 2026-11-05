using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Templates de relatório embutidos para os sensores (dataset 29 — estado
/// corrente de cada sensor por agente/alvo).
///
///   a0000004-...-01 "Sensores em Falha por Agente" — fonte única, agrupada
///                     por hostname, com as falhas severidade alta.
///   a0000004-...-02 "Hardware + Sensores por Agente" — MULTI-SOURCE, prova
///                     viva de que o join hw -> sns funciona. O join usa
///                     agentId, a única chave realmente comum aos dois
///                     datasets (agentHardware NÃO expõe clientId/siteId,
///                     apenas clientName/siteName; logo clientId e siteId
///                     não podem ser usados aqui — o resolver rejeitaria).
///
/// O layoutJson abaixo é validado em CI por
/// <c>SeedSensorReportTemplatesTests</c>, que roda o
/// <c>ReportLayoutFieldResolver</c> de verdade contra o catálogo real. Isso
/// importa porque o backend agora rejeita (400 em layoutJson) layouts com
/// campo inexistente, join sem sourceKey ou joinToAlias que não seja fonte
/// anterior.
///
/// IDs determinísticos a0000004-0000-0000-0000-00000000000N e Down idempotente,
/// seguindo M232_SeedPatchReportTemplates.
/// </summary>
[Migration(20270106_271)]
public class M271_SeedSensorReportTemplates : Migration
{
    private const int AgentSensorsVal = 29;
    private const int AgentHardwareVal = 4;

    private const string DefaultStyle = @",""style"":{""primaryColor"":""#16324F"",""headerBackgroundColor"":""#16324F"",""headerTextColor"":""#FFFFFF"",""alternateRowColor"":""#EEF4F7"",""fontFamily"":""Segoe UI, sans-serif""}";

    public override void Up()
    {
        var now = DateTime.UtcNow.ToString("O");

        Seed("a0000004-0000-0000-0000-000000000001",
            "Sensores em Falha por Agente",
            "Estado corrente dos sensores por agente: sensores com falha, severidade, valor do canal primário, limites e última coleta.",
            AgentSensorsVal,
            SensorFailuresLayoutJson,
            SensorFailuresSchema, now);

        Seed("a0000004-0000-0000-0000-000000000002",
            "Hardware + Sensores por Agente",
            "Uma linha por agente com o hardware inventariado e os sensores medidos no mesmo host (join por agentId).",
            AgentHardwareVal,
            HardwareSensorsLayoutJson,
            HardwareSensorsSchema, now);
    }

    /// <summary>Layout JSON do template "Sensores em Falha por Agente".
    /// Exposto para o teste validar exatamente a string que o seed insere.</summary>
    public static string SensorFailuresLayoutJson =>
        @"{""title"":""Sensores em Falha por Agente"",""orientation"":""landscape"",""groupBy"":""agentHostname"",""groupTitleTemplate"":""{{value}} ({{count}} sensores)"",""hideGroupColumn"":true,""columns"":[{""field"":""agentHostname"",""header"":""Agente""},{""field"":""sensorName"",""header"":""Sensor""},{""field"":""sensorType"",""header"":""Tipo""},{""field"":""target"",""header"":""Alvo""},{""field"":""status"",""header"":""Status""},{""field"":""severity"",""header"":""Severidade""},{""field"":""primaryValue"",""header"":""Valor"",""format"":""number""},{""field"":""unit"",""header"":""Unidade""},{""field"":""message"",""header"":""Mensagem""},{""field"":""consecutiveFailures"",""header"":""Falhas consecutivas"",""format"":""number""},{""field"":""lastRunAt"",""header"":""Última coleta"",""format"":""datetime""}],""groupDetails"":[{""field"":""clientName"",""header"":""Cliente""},{""field"":""siteName"",""header"":""Site""}],""groupSummaries"":[{""label"":""Sensores"",""aggregate"":""count""}]" + DefaultStyle + "}";

    /// <summary>Layout JSON do template "Hardware + Sensores por Agente".
    /// Exposto para o teste validar exatamente a string que o seed insere.</summary>
    public static string HardwareSensorsLayoutJson =>
        @"{""title"":""Hardware + Sensores por Agente"",""orientation"":""landscape"",""groupBy"":""hw.agentHostname"",""groupTitleTemplate"":""{{value}}"",""hideGroupColumn"":true,""dataSources"":[{""datasetType"":4,""alias"":""hw""},{""datasetType"":29,""alias"":""sns"",""join"":{""joinToAlias"":""hw"",""sourceKey"":""agentId"",""targetKey"":""agentId"",""joinType"":""left""}}],""columns"":[{""field"":""hw.agentHostname"",""header"":""Agente""},{""field"":""hw.osName"",""header"":""SO""},{""field"":""hw.processor"",""header"":""Processador""},{""field"":""hw.totalMemoryGB"",""header"":""RAM (GB)"",""format"":""number""},{""field"":""hw.totalDisksCount"",""header"":""Discos"",""format"":""number""},{""field"":""hw.collectedAt"",""header"":""Coletado em"",""format"":""datetime""}],""sections"":[{""title"":""Sensores do agente"",""source"":""sns"",""columns"":[{""field"":""sns.sensorName"",""header"":""Sensor""},{""field"":""sns.sensorType"",""header"":""Tipo""},{""field"":""sns.target"",""header"":""Alvo""},{""field"":""sns.status"",""header"":""Status""},{""field"":""sns.severity"",""header"":""Severidade""},{""field"":""sns.primaryValue"",""header"":""Valor"",""format"":""number""},{""field"":""sns.unit"",""header"":""Unidade""},{""field"":""sns.message"",""header"":""Mensagem""},{""field"":""sns.lastRunAt"",""header"":""Última coleta"",""format"":""datetime""}]}],""summaries"":[{""label"":""Agentes"",""aggregate"":""count""}]" + DefaultStyle + "}";

    public override void Down()
    {
        for (var i = 1; i <= 2; i++)
        {
            Delete.FromTable("report_templates")
                .Row(new { id = Guid.Parse($"a0000004-0000-0000-0000-00000000000{i:D1}") });
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

    private static string SensorFailuresSchema => @"
{
  ""scopeType"": 3,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""agentHostname"", ""sensorName"", ""sensorType"", ""status"", ""severity"", ""lastRunAt""],
  ""defaultSortField"": ""agentHostname"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""asc"",
  ""filters"": [
    " + ScopeFilters + @"
    { ""name"": ""status"", ""label"": ""Status"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Status corrente do sensor."", ""uiComponent"": 2, ""allowedValues"": [""Ok"", ""Warning"", ""Critical"", ""Failed"", ""Unknown""] },
    { ""name"": ""severity"", ""label"": ""Severidade"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Severidade calculada para o sensor."", ""uiComponent"": 2, ""allowedValues"": [""Critical"", ""Warning"", ""Info""] },
    { ""name"": ""sensorType"", ""label"": ""Tipo de sensor"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pelo tipo do sensor (ex: disk, cpu, service)."", ""uiComponent"": 1, ""maxLength"": 60, ""isPartialMatch"": true },
    { ""name"": ""from"", ""label"": ""Coletado a partir de"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Filtro pela data da ultima coleta."", ""uiComponent"": 7 },
    { ""name"": ""to"", ""label"": ""Coletado até"", ""type"": 7, ""required"": false, ""group"": ""Periodo"", ""description"": ""Limite superior da data da ultima coleta."", ""uiComponent"": 7 },
    " + OutputFilters("agentHostname", ["agentHostname", "sensorName", "sensorType", "status", "severity", "lastRunAt"]) + @"
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Falhas (todas)"", ""description"": ""Sensores em falha de todos os agentes, por severidade"", ""filtersJson"": ""{ ""status"": ""Failed"", ""orderBy"": ""severity"", ""orderDirection"": ""asc"" }"" },
    { ""name"": ""Somente críticos"", ""description"": ""Sensores com severidade Critical"", ""filtersJson"": ""{ ""severity"": ""Critical"" }"" },
    { ""name"": ""Discos com falha"", ""description"": ""Sensores de disco em estado de falha"", ""filtersJson"": ""{ ""sensorType"": ""disk"", ""status"": ""Failed"" }"" }
  ]
}";

    private static string HardwareSensorsSchema => @"
{
  ""scopeType"": 3,
  ""dateMode"": 1,
  ""allowedOrientations"": [""landscape"", ""portrait""],
  ""defaultOrientation"": ""landscape"",
  ""allowedSortFields"": [""hw.agentHostname"", ""hw.osName"", ""hw.totalMemoryGB"", ""hw.collectedAt""],
  ""defaultSortField"": ""hw.agentHostname"",
  ""allowedSortDirections"": [""asc"", ""desc""],
  ""defaultSortDirection"": ""asc"",
  ""filters"": [
    " + ScopeFilters + @"
    { ""name"": ""osName"", ""label"": ""SO"", ""type"": 0, ""required"": false, ""group"": ""Filtros"", ""description"": ""Busca parcial pelo nome do sistema operacional."", ""uiComponent"": 1, ""maxLength"": 120, ""isPartialMatch"": true },
    { ""name"": ""minMemoryGB"", ""label"": ""RAM mínima (GB)"", ""type"": 4, ""required"": false, ""group"": ""Filtros"", ""description"": ""Somente agentes com memoria total igual ou acima do valor."", ""uiComponent"": 5, ""min"": 0, ""max"": 4096 },
    { ""name"": ""sensorStatus"", ""label"": ""Status do sensor"", ""type"": 2, ""required"": false, ""group"": ""Filtros"", ""description"": ""Filtra agentes que tenham sensor nesse status."", ""uiComponent"": 2, ""allowedValues"": [""Ok"", ""Warning"", ""Critical"", ""Failed"", ""Unknown""] },
    " + OutputFilters("hw.agentHostname", ["hw.agentHostname", "hw.osName", "hw.totalMemoryGB", "hw.collectedAt"]) + @"
  ],
  ""sampleFilterPresets"": [
    { ""name"": ""Agentes com sensores em falha"", ""description"": ""Hardware dos agentes que possuem ao menos um sensor em falha"", ""filtersJson"": ""{ ""sensorStatus"": ""Failed"", ""orderBy"": ""hw.agentHostname"", ""orderDirection"": ""asc"" }"" },
    { ""name"": ""Máquinas com pouca RAM"", ""description"": ""Agentes com menos de 8 GB de memoria"", ""filtersJson"": ""{ ""minMemoryGB"": 8, ""orderBy"": ""hw.totalMemoryGB"", ""orderDirection"": ""asc"" }"" },
    { ""name"": ""Somente Windows 11"", ""description"": ""Agentes cujo SO reportado contem 'Windows 11'"", ""filtersJson"": ""{ ""osName"": ""Windows 11"" }"" }
  ]
}";
}
