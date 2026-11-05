using System.Globalization;
using System.Text;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.ValueObjects;

namespace Discovery.Core.Helpers;

/// <summary>
/// Valida as referencias de campo e o encadeamento de joins de um layout
/// multi-source contra o catalogo real de datasets.
///
/// O <see cref="ReportLayoutValidator"/> cobre apenas a forma (limites,
/// formatos, alias duplicado). Ele nao sabe que "hw.agentId" existe e que
/// "hw.agentIde" nao — era por isso que um template com campo inexistente ou
/// com join incompleto era gravado e so falhava (ou pior, saia vazio) na
/// geracao do relatorio.
///
/// Convencao de referencia: "{alias}.{field}" (FieldReferenceMode do schema).
/// Em layout de fonte unica o nome puro do campo tambem e aceito, para nao
/// quebrar os templates antigos gravados antes do multi-source.
/// </summary>
public static class ReportLayoutFieldResolver
{
    /// <summary>
    /// Devolve a lista de erros encontrados (vazia quando o layout esta ok).
    /// As mensagens seguem o formato "dataSources[1].join.sourceKey: ...".
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        ReportLayoutDefinition layout,
        IReadOnlyList<ReportDatasetCatalogItemDto> catalog)
    {
        var errors = new List<string>();
        if (layout is null || catalog is null || catalog.Count == 0)
            return errors;

        var dataSources = layout.DataSources;
        var isMultiSource = dataSources is { Count: > 1 };

        var aliasesByDatasetType = new Dictionary<int, List<string>>();
        var sourceByAlias = new Dictionary<string, SourceInfo>(StringComparer.OrdinalIgnoreCase);

        if (dataSources is { Count: > 0 })
        {
            for (var index = 0; index < dataSources.Count; index++)
            {
                var source = dataSources[index];
                if (!source.DatasetType.HasValue || string.IsNullOrWhiteSpace(source.Alias))
                    continue;

                var item = FindByDatasetType(catalog, (int)source.DatasetType.Value);
                if (item is null)
                {
                    errors.Add($"dataSources[{index}].datasetType: o dataset {(int)source.DatasetType.Value} nao existe no catalogo.");
                    continue;
                }

                var info = new SourceInfo(index, source.Alias!, item);
                sourceByAlias[info.Alias] = info;

                if (!aliasesByDatasetType.TryGetValue(item.DatasetType, out var list))
                {
                    list = [];
                    aliasesByDatasetType[item.DatasetType] = list;
                }
                list.Add(info.Alias);
            }

            ValidateBaseMark(dataSources, errors);
            ValidateJoins(dataSources, sourceByAlias, errors);
        }

        var knownReferences = BuildKnownReferences(catalog, aliasesByDatasetType, isMultiSource);

        if (layout.Columns is { Count: > 0 })
            ValidateColumns(layout.Columns, "columns", knownReferences, errors);

        if (layout.GroupDetails is { Count: > 0 })
            ValidateColumns(layout.GroupDetails, "groupDetails", knownReferences, errors);

        if (layout.Sections is { Count: > 0 })
        {
            for (var index = 0; index < layout.Sections.Count; index++)
            {
                var columns = layout.Sections[index].Columns;
                if (columns is { Count: > 0 })
                    ValidateColumns(columns, $"sections[{index}].columns", knownReferences, errors);
            }
        }

        if (layout.Summaries is { Count: > 0 })
            ValidateSummaries(layout.Summaries, "summaries", knownReferences, errors);

        if (layout.GroupSummaries is { Count: > 0 })
            ValidateSummaries(layout.GroupSummaries, "groupSummaries", knownReferences, errors);

        if (layout.Charts is { Count: > 0 })
        {
            for (var index = 0; index < layout.Charts.Count; index++)
            {
                var chart = layout.Charts[index];
                ValidateField(chart.GroupField, $"charts[{index}].groupField", knownReferences, errors);
                ValidateField(chart.ValueField, $"charts[{index}].valueField", knownReferences, errors);
            }
        }

        if (layout.GroupLevels is { Count: > 0 })
        {
            for (var index = 0; index < layout.GroupLevels.Count; index++)
                ValidateField(layout.GroupLevels[index].Field, $"groupLevels[{index}].field", knownReferences, errors);
        }

        if (!string.IsNullOrWhiteSpace(layout.GroupBy))
            ValidateField(layout.GroupBy, "groupBy", knownReferences, errors);

        if (layout.ComputedFields is { Count: > 0 })
        {
            // O "name" e um alias novo criado pelo layout: registra antes de
            // validar as expressoes, pois uma pode usar a outra.
            foreach (var computed in layout.ComputedFields)
            {
                if (!string.IsNullOrWhiteSpace(computed.Name))
                    knownReferences.Add(computed.Name!);
            }

            for (var index = 0; index < layout.ComputedFields.Count; index++)
            {
                foreach (var reference in ExtractReferences(layout.ComputedFields[index].Expression))
                {
                    if (!knownReferences.Contains(reference))
                        errors.Add($"computedFields[{index}].expression: o campo '{reference}' nao existe em nenhuma fonte do layout.");
                }
            }
        }

        return errors;
    }

    // ── Base ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// D3: no maximo uma fonte marcada como base. A checagem de grafo conectado
    /// fica no ReportLayoutValidator (validacao de forma, roda sempre); aqui
    /// reportamos o mesmo defeito no caminho que conhece o catalogo.
    /// </summary>
    /// <summary>
    /// Alias da fonte principal: a MARCADA com base=true ou, sem marca, a
    /// primeira do array (comportamento historico).
    /// </summary>
    private static string? ResolveBaseAlias(IReadOnlyList<ReportLayoutDataSourceDefinition> dataSources)
    {
        var marked = dataSources.FirstOrDefault(source => source.Base == true);
        return marked?.Alias ?? dataSources.FirstOrDefault()?.Alias;
    }

    private static void ValidateBaseMark(
        IReadOnlyList<ReportLayoutDataSourceDefinition> dataSources,
        ICollection<string> errors)
    {
        var marked = dataSources
            .Select((source, index) => new { source, index })
            .Where(item => item.source.Base == true)
            .ToList();

        if (marked.Count <= 1)
            return;

        errors.Add(
            "dataSources: apenas uma fonte pode ser marcada como base (base=true). " +
            $"Marcadas: {string.Join(", ", marked.Select(m => m.source.Alias ?? $"#{m.index}"))}.");
    }

    // ── Joins ────────────────────────────────────────────────────────────────
    private static void ValidateJoins(
        IReadOnlyList<ReportLayoutDataSourceDefinition> dataSources,
        IReadOnlyDictionary<string, SourceInfo> sourceByAlias,
        ICollection<string> errors)
    {
        // A base nao tem join — mas "base" e a fonte MARCADA, nao necessariamente
        // dataSources[0]: com base:true a ordem do array deixa de definir quem e
        // a principal.
        var baseAlias = ResolveBaseAlias(dataSources);

        for (var index = 0; index < dataSources.Count; index++)
        {
            var source = dataSources[index];

            if (source.Alias is not null && baseAlias is not null &&
                string.Equals(source.Alias, baseAlias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.Join is null)
                continue; // ReportLayoutValidator ja reporta o join ausente.

            var join = source.Join;
            var info = source.Alias is not null && sourceByAlias.TryGetValue(source.Alias, out var found)
                ? found
                : (SourceInfo?)null;

            if (string.IsNullOrWhiteSpace(join.SourceKey))
            {
                // Antes o validator aceitava sourceKey vazio e o relatorio saia
                // sem join nenhum; aqui isso e erro explicito.
                errors.Add($"dataSources[{index}].join.sourceKey: a chave de origem e obrigatoria a partir da segunda fonte.");
            }
            else if (info is not null && !HasField(info.Value.Item, join.SourceKey!))
            {
                errors.Add($"dataSources[{index}].join.sourceKey: o campo '{join.SourceKey}' nao existe no dataset '{info.Value.Item.Key}'.");
            }

            if (string.IsNullOrWhiteSpace(join.JoinToAlias))
            {
                errors.Add($"dataSources[{index}].join.joinToAlias: informe com qual fonte esta fonte se liga.");
                continue;
            }

            if (!sourceByAlias.TryGetValue(join.JoinToAlias!, out var target))
            {
                errors.Add($"dataSources[{index}].join.joinToAlias: a fonte '{join.JoinToAlias}' nao foi declarada em dataSources.");
                continue;
            }

            // Com base explicita a ordem do array nao manda mais: o que importa e
            // que o alvo nao seja a propria fonte (autojoin) nem crie ciclo.
            var isSelfJoin = source.Alias is not null &&
                string.Equals(source.Alias, join.JoinToAlias, StringComparison.OrdinalIgnoreCase);

            if (isSelfJoin)
                errors.Add($"dataSources[{index}].join.joinToAlias: a fonte '{join.JoinToAlias}' nao pode se ligar a si mesma.");

            var targetKey = string.IsNullOrWhiteSpace(join.TargetKey) ? join.SourceKey : join.TargetKey;
            if (string.IsNullOrWhiteSpace(targetKey))
                continue;

            if (!HasField(target.Item, targetKey!))
                errors.Add($"dataSources[{index}].join.targetKey: o campo '{targetKey}' nao existe no dataset '{target.Item.Key}' (fonte '{join.JoinToAlias}').");
        }
    }

    // ── Referencias conhecidas ───────────────────────────────────────────────
    private static HashSet<string> BuildKnownReferences(
        IReadOnlyList<ReportDatasetCatalogItemDto> catalog,
        IReadOnlyDictionary<int, List<string>> aliasesByDatasetType,
        bool isMultiSource)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in catalog)
        {
            var aliases = aliasesByDatasetType.TryGetValue(item.DatasetType, out var declared)
                ? declared
                : [];

            // Em layout multi-source so valem as fontes DECLARADAS: usar o
            // alias de um dataset que nao esta em dataSources e erro. Sem isso
            // "wu.kbId" passaria num layout que so declara hw+sns.
            if (isMultiSource && aliases.Count == 0)
                continue;

            if (aliases.Count == 0)
            {
                var defaultAlias = DefaultAliasOf(item);
                if (!string.IsNullOrWhiteSpace(defaultAlias))
                    aliases = [defaultAlias!];
            }

            foreach (var field in item.Fields)
            {
                // Em fonte unica o nome puro e aceito (compatibilidade com
                // layouts antigos gravados antes do multi-source).
                if (!isMultiSource)
                    references.Add(field);

                foreach (var alias in aliases)
                    references.Add($"{alias}.{field}");
            }
        }

        return references;
    }

    private static string? DefaultAliasOf(ReportDatasetCatalogItemDto item)
    {
        var candidate = item.FieldMetadata
            .Select(metadata => metadata.DefaultAlias)
            .FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias));

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var dot = candidate!.IndexOf('.');
        return dot > 0 ? candidate[..dot] : candidate;
    }

    private static ReportDatasetCatalogItemDto? FindByDatasetType(
        IReadOnlyList<ReportDatasetCatalogItemDto> catalog, int datasetType)
        => catalog.FirstOrDefault(item => item.DatasetType == datasetType);

    private static bool HasField(ReportDatasetCatalogItemDto item, string field)
        => item.Fields.Contains(field, StringComparer.OrdinalIgnoreCase);

    // ── Campos ───────────────────────────────────────────────────────────────
    private static void ValidateColumns(
        IReadOnlyList<ReportLayoutColumnDefinition> columns,
        string path,
        HashSet<string> knownReferences,
        ICollection<string> errors)
    {
        for (var index = 0; index < columns.Count; index++)
            ValidateField(columns[index].Field, $"{path}[{index}].field", knownReferences, errors);
    }

    private static void ValidateSummaries(
        IReadOnlyList<ReportLayoutSummaryDefinition> summaries,
        string path,
        HashSet<string> knownReferences,
        ICollection<string> errors)
    {
        for (var index = 0; index < summaries.Count; index++)
        {
            var summary = summaries[index];
            if (string.Equals(summary.Aggregate, "count", StringComparison.OrdinalIgnoreCase))
                continue; // "count" nao precisa de campo.

            ValidateField(summary.Field, $"{path}[{index}].field", knownReferences, errors);
        }
    }

    private static void ValidateField(
        string? field,
        string path,
        HashSet<string> knownReferences,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(field))
            return;

        if (!knownReferences.Contains(field))
            errors.Add($"{path}: o campo '{field}' nao existe em nenhuma fonte do layout.");
    }

    /// <summary>
    /// Extrai as referencias de campo de uma expressao de campo calculado
    /// (ex: "hw.totalMemoryGB / 1024"). Ignora numeros e literais entre aspas.
    /// </summary>
    private static IEnumerable<string> ExtractReferences(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return [];

        var found = new List<string>();
        var token = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        void Flush()
        {
            if (token.Length == 0)
                return;

            var value = token.ToString();
            token.Clear();

            if (!IsLiteral(value))
                found.Add(value);
        }

        foreach (var ch in expression!)
        {
            if (inQuotes)
            {
                if (ch == quoteChar) inQuotes = false;
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuotes = true;
                quoteChar = ch;
                Flush();
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
            {
                token.Append(ch);
                continue;
            }

            Flush();
        }

        Flush();
        return found;
    }

    private static bool IsLiteral(string value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    private readonly record struct SourceInfo(int Index, string Alias, ReportDatasetCatalogItemDto Item);
}
