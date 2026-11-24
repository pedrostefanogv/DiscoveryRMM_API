using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Core.Entities;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
    private async Task<ReportQueryResult> QueryMultiSourceAsync(IReadOnlyList<ReportLayoutDataSourceDefinition> dataSources, string? runtimeFiltersJson, CancellationToken cancellationToken)
    {
        // Antes as fontes invalidas eram REMOVIDAS sem aviso. Se a remocao
        // deixasse uma fonte so, o relatorio voltava normalmente e o usuario
        // achava que o multi-source funcionou; se zerava tudo, vinha um
        // "sucesso" com uma linha de texto. Template antigo ou editado direto no
        // banco chega aqui sem passar pelo ReportLayoutValidator, entao a guarda
        // precisa existir tambem no caminho de execucao.
        var invalidSources = dataSources
            .Select((source, index) => new { source, index })
            .Where(item => !item.source.DatasetType.HasValue || string.IsNullOrWhiteSpace(item.source.Alias))
            .ToList();

        if (invalidSources.Count > 0)
        {
            var details = string.Join("; ", invalidSources.Select(item =>
            {
                var alias = string.IsNullOrWhiteSpace(item.source.Alias)
                    ? "(alias vazio)"
                    : $"alias '{item.source.Alias}'";

                var type = item.source.DatasetType.HasValue
                    ? $"datasetType {(int)item.source.DatasetType.Value}"
                    : "datasetType ausente";

                return $"dataSources[{item.index}]: {alias}, {type}";
            }));

            throw new InvalidOperationException(
                "O layout multi-source tem fonte(s) invalida(s) e o relatorio nao pode ser gerado: " +
                details + ". Cada fonte precisa de datasetType e alias.");
        }

        var normalizedSources = dataSources.ToList();

        var globalFilters = ParseJsonObject(runtimeFiltersJson);
        var sourceResults = new List<MultiSourceResult>(normalizedSources.Count);

        foreach (var source in normalizedSources)
        {
            // Scope keys (clientId/siteId) from globalFilters are authoritative and
            // must never be overridden or cleared by per-source LayoutJson filters.
            var mergedFilters = MergeJsonObjects(ParseJsonObject(source.Filters), globalFilters);
            var mergedFiltersJson = mergedFilters?.ToJsonString();
            var mergedFiltersElement = ParseFilters(mergedFiltersJson);
            var clientId = GetGuid(mergedFiltersElement, "clientId");

            // Chave com o JSON completo: GetHashCode() pode colidir (devolvendo o
            // resultado de outro conjunto de filtros) e muda a cada processo.
            var cacheKey = $"report_query:{source.DatasetType}:{clientId}:{mergedFiltersJson}";
            if (!_cache.TryGetValue(cacheKey, out ReportQueryResult? cachedResult))
            {
                cachedResult = await QuerySingleDatasetAsync(source.DatasetType!.Value, clientId, mergedFiltersElement, cancellationToken);

                // Guardar o resultado INTEIRO no cache era um vazamento: ate
                // 10.000 linhas por dataset, por filtro, sem SizeLimit. So
                // entra no cache resultado pequeno, com Size declarado.
                if (cachedResult is not null && cachedResult.Rows.Count <= QueryCacheMaxRows)
                {
                    _cache.Set(cacheKey, cachedResult, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = QueryCacheTtl,
                        Size = 1
                    });
                }
            }

            sourceResults.Add(new MultiSourceResult(source, cachedResult!));
        }

        if (sourceResults.Count == 1)
            return sourceResults[0].Result;

        // D1: a base e a fonte MARCADA com base=true. Sem a marca nada muda: a
        // base continua sendo dataSources[0] (compatibilidade total com o que
        // ja esta gravado). Promover aqui deixa o laco de join — que percorre a
        // partir do indice 1 — funcionando sem nenhuma outra alteracao.
        sourceResults = PromoteBaseSource(sourceResults);

        var baseSource = sourceResults[0];

        // B1: TODAS as fontes recebem o nome puro do campo alem de "{alias}.{field}".
        // Antes so a primeira (a base) passava por BuildBaseRow; as demais ficavam
        // apenas com o nome qualificado, entao um join por "siteId" puro nunca
        // encontrava a chave na segunda fonte.
        var mergedRows = baseSource.Result.Rows
            .Select(row => BuildBaseRow(baseSource.Source.Alias!, row))
            .ToList();

        // B2: indice de join pre-computado UMA vez por fonte (nao por linha) e
        // valores normalizados com InvariantCulture (ToString() de DateTime/Guid
        // depende da cultura do processo e derrubava o match).

        // Process joins in order — each source joins with whichever alias it specifies (chained)
        for (var index = 1; index < sourceResults.Count; index++)
        {
            var current = sourceResults[index];
            var alias = current.Source.Alias!;

            // B1 (cont.): as linhas da fonte corrente tambem ganham o nome puro.
            var currentRows = current.Result.Rows
                .Select(row => BuildBaseRow(alias, row))
                .ToList();
            var join = current.Source.Join;
            if (join is null || string.IsNullOrWhiteSpace(join.SourceKey))
                continue;

            var sourceKey = join.SourceKey!;
            var targetKey = string.IsNullOrWhiteSpace(join.TargetKey) ? sourceKey : join.TargetKey!;
            var joinAlias = string.IsNullOrWhiteSpace(join.JoinToAlias)
                ? baseSource.Source.Alias!
                : join.JoinToAlias!;
            var joinType = string.Equals(join.JoinType, "inner", StringComparison.OrdinalIgnoreCase) ? "inner" : "left";

            // Indice construido uma unica vez para a fonte inteira.
            var lookup = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in currentRows)
            {
                var key = GetJoinKey(row, alias, sourceKey);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!lookup.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    lookup[key] = bucket;
                }

                bucket.Add(row);
            }

            var leftCount = mergedRows.Count;
            var matchedLeftCount = 0;
            var nextRows = new List<Dictionary<string, object?>>();

            foreach (var left in mergedRows)
            {
                var leftKey = GetJoinKey(left, joinAlias, targetKey);
                if (string.IsNullOrWhiteSpace(leftKey) || !lookup.TryGetValue(leftKey, out var matches) || matches.Count == 0)
                {
                    // left preserva a linha da base; inner descarta. Em left, as
                    // colunas da fonte entram como null para existirem no
                    // dicionario — sem isso o renderer quebrava com KeyNotFound.
                    if (joinType == "left")
                    {
                        var semMatch = new Dictionary<string, object?>(left, StringComparer.OrdinalIgnoreCase);
                        foreach (var column in current.Result.Columns)
                            semMatch[$"{alias}.{column}"] = null;

                        nextRows.Add(semMatch);
                    }
                    continue;
                }

                matchedLeftCount++;

                foreach (var match in matches)
                {
                    var merged = new Dictionary<string, object?>(left, StringComparer.OrdinalIgnoreCase);
                    foreach (var (field, value) in match)
                    {
                        // Preserva os campos nao-qualificados da fonte base; copia
                        // os qualificados ("{alias}.{campo}") sempre e os puros apenas
                        // quando ainda nao existem na linha combinada.
                        if (field.Contains('.', StringComparison.Ordinal) || !merged.ContainsKey(field))
                            merged[field] = value;
                    }

                    // Atalho para join com alias explicito no target (ex.: hw.agentId)
                    merged[$"{joinAlias}.{targetKey}"] = left.TryGetValue($"{joinAlias}.{targetKey}", out var targetValue)
                        ? targetValue
                        : left.TryGetValue(targetKey, out var plainValue) ? plainValue : null;

                    nextRows.Add(merged);
                }
            }

            // B3: teto do produto cartesiano com ERRO explicito, nunca truncar em silencio.
            if (nextRows.Count > HardLimit)
            {
                throw new InvalidOperationException(
                    $"O join '{joinAlias}.{targetKey}' = '{alias}.{sourceKey}' produziu {nextRows.Count} linhas " +
                    $"(limite {HardLimit}). Refine os filtros dos datasets '{joinAlias}' e '{alias}' para reduzir o cruzamento.");
            }

            // B4: nenhum match entre a base e a fonte com linhas => falha acionavel
            // (chave errada). Em LEFT JOIN com a fonte direita vazia (0 linhas),
            // preserva as linhas da base com colunas da direita nulas; em INNER JOIN
            // falha porque o resultado seria silenciosamente zerado.
            var shouldFailNoMatch = matchedLeftCount == 0 && leftCount > 0 &&
                (currentRows.Count > 0 || string.Equals(joinType, "inner", StringComparison.OrdinalIgnoreCase));

            if (shouldFailNoMatch)
            {
                var reason = currentRows.Count == 0
                    ? $"O join por '{targetKey}' nao casou nenhuma linha entre '{joinAlias}' e '{alias}': " +
                      $"o dataset '{alias}' nao retornou linhas para os filtros informados."
                    : $"O join por '{targetKey}' nao casou nenhuma linha entre '{joinAlias}' e '{alias}': " +
                      $"{leftCount} linha(s) na base e {currentRows.Count} na fonte, sem chave em comum.";
                _logger?.LogWarning(
                    "Report multi-source join sem correspondencia: {Reason} (sourceKey={SourceKey}, targetKey={TargetKey})",
                    reason, sourceKey, targetKey);

                throw new InvalidOperationException(reason);
            }

            mergedRows = nextRows;
        }

        // Collect columns from all sources
        var columns = new List<string>();
        foreach (var column in baseSource.Result.Columns)
        {
            if (!columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                columns.Add(column);

            var aliased = $"{baseSource.Source.Alias}.{column}";
            if (!columns.Contains(aliased, StringComparer.OrdinalIgnoreCase))
                columns.Add(aliased);
        }

        for (var index = 1; index < sourceResults.Count; index++)
        {
            var source = sourceResults[index];
            foreach (var column in source.Result.Columns)
            {
                var aliased = $"{source.Source.Alias}.{column}";
                if (!columns.Contains(aliased, StringComparer.OrdinalIgnoreCase))
                    columns.Add(aliased);
            }
        }

        return new ReportQueryResult
        {
            Columns = columns,
            Rows = mergedRows.Cast<IReadOnlyDictionary<string, object?>>().ToList()
        };
    }

    /// <summary>
    /// D1: coloca a fonte principal na posicao 0.
    ///
    /// A base e a fonte marcada com <c>base=true</c>; sem marca, mantem
    /// <c>dataSources[0]</c> — nenhum layout antigo muda de comportamento.
    /// As demais fontes sao reordenadas para que cada uma venha DEPOIS da fonte
    /// em que se liga (joinToAlias), senao o laco de join processaria uma fonte
    /// antes da sua origem. Fontes que nao alcancam a base ficam no fim, na ordem
    /// original: o validador ja reprova esse layout antes de chegar aqui.
    /// </summary>
    private static List<MultiSourceResult> PromoteBaseSource(List<MultiSourceResult> sourceResults)
    {
        var marked = sourceResults.FirstOrDefault(result => result.Source.Base == true);
        if (marked is null)
            return sourceResults;

        var ordered = new List<MultiSourceResult> { marked };
        var pending = sourceResults.Where(result => !ReferenceEquals(result, marked)).ToList();

        // Insere cada fonte assim que a sua origem (joinToAlias) ja estiver posta.
        while (pending.Count > 0)
        {
            var placedIndex = pending.FindIndex(candidate =>
            {
                var joinToAlias = candidate.Source.Join?.JoinToAlias;
                if (string.IsNullOrWhiteSpace(joinToAlias))
                    return false;

                return ordered.Any(placed =>
                    string.Equals(placed.Source.Alias, joinToAlias, StringComparison.OrdinalIgnoreCase));
            });

            if (placedIndex < 0)
                break; // Sem progresso: resto fica na ordem original (layout invalido).

            ordered.Add(pending[placedIndex]);
            pending.RemoveAt(placedIndex);
        }

        ordered.AddRange(pending);
        return ordered;
    }

    /// <summary>
    /// Duplica cada campo da fonte em duas chaves: o nome puro e "{alias}.{campo}".
    /// Aplicado a TODAS as fontes (antes so a base passava por aqui, entao a
    /// segunda fonte nao tinha o nome puro e o join por campo simples falhava).
    /// O valor e o MESMO objeto: nenhuma conversao de tipo e introduzida.
    /// </summary>
    private static Dictionary<string, object?> BuildBaseRow(string alias, IReadOnlyDictionary<string, object?> row)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in row)
        {
            result[field] = value;
            result[$"{alias}.{field}"] = value;
        }

        return result;
    }

    /// <summary>
    /// Resolve a chave de join de uma linha, preferindo "{alias}.{key}" e caindo
    /// para o nome puro. A versao antiga procurava por <c>EndsWith(".{key}")</c>
    /// em TODAS as chaves, o que casava a chave errada quando dois campos
    /// terminavam igual (ex: "target" e "sensorTarget").
    /// </summary>
    private static string? GetJoinKey(IReadOnlyDictionary<string, object?> row, string alias, string key)
    {
        if (!string.IsNullOrWhiteSpace(alias) &&
            row.TryGetValue($"{alias}.{key}", out var aliased) && aliased is not null)
        {
            return NormalizeJoinKey(aliased);
        }

        if (row.TryGetValue(key, out var value) && value is not null)
            return NormalizeJoinKey(value);

        return null;
    }

    /// <summary>
    /// Normaliza o valor da chave para comparacao. ToString() de DateTime/Guid
    /// segue a cultura do processo: um relatorio gerado em pt-BR podia nao casar
    /// com o mesmo dado lido em outra cultura.
    /// </summary>
    private static string? NormalizeJoinKey(object? value) => value switch
    {
        null => null,
        string text => text.Trim(),
        Guid guid => guid.ToString("D"),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static JsonObject? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ParseJsonObject(JsonElement? jsonElement)
    {
        if (!jsonElement.HasValue || jsonElement.Value.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return JsonNode.Parse(jsonElement.Value.GetRawText()) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? MergeJsonObjects(JsonObject? baseObject, JsonObject? overrides)
    {
        if (baseObject is null && overrides is null)
            return null;

        var result = new JsonObject();
        if (baseObject is not null)
        {
            foreach (var property in baseObject)
                result[property.Key] = property.Value?.DeepClone();
        }

        if (overrides is not null)
        {
            foreach (var property in overrides)
                result[property.Key] = property.Value?.DeepClone();
        }

        return result;
    }

    private sealed record MultiSourceResult(ReportLayoutDataSourceDefinition Source, ReportQueryResult Result);
}
