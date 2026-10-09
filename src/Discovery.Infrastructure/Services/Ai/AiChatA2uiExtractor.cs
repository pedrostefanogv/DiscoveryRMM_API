using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Extrai mensagens A2UI (Agent-to-User Interface) do conteúdo gerado pelo LLM.
///
/// O LLM é instruído (via system prompt) a emitir interfaces A2UI dentro de um
/// fenced code block com linguagem a2ui:
///
///   { "version":"v0.9","createSurface":{...} }
///   { "version":"v0.9","updateComponents":{...} }
///
/// Cada linha do bloco é uma mensagem A2UI (JSONL). O helper:
///   - Detecta e extrai essas mensagens;
///   - Remove o bloco do texto visível ao usuário (o texto restante segue o
///     fluxo markdown normal);
///   - Valida minimamente que cada linha é um JSON com "version" e um dos
///     verbos A2UI (createSurface/updateComponents/updateDataModel/deleteSurface);
///   - Repara o defeito mais comum (fechamentos ausentes) e aplica o teto
///     PRESERVANDO o essencial da interface (ver ApplyCap).
///
/// Isso mantém o A2UI "secure by design": o renderer só processa mensagens
/// declarativas do catálogo aprovado, nunca código executável.
/// </summary>
public static class AiChatA2uiExtractor
{
    // C8: abertura de bloco tolerante (espacos e maiusculas variando).
    private static readonly Regex A2uiOpenRegex = new("^```\\s*a2ui\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] A2uiVerbs =
    {
        "createSurface", "updateComponents", "updateDataModel", "deleteSurface"
    };

    /// <summary>
    /// Teto de mensagens A2UI emitidas por resposta. O agent/renderer também
    /// limita por turno (maxA2uiMessagesPerTurn, no agent); o teto no servidor
    /// evita que um LLM "empolgado" inunde o SSE e o cliente.
    ///
    /// O teto NÃO é mais aplicado cegamente por ordem: ver ApplyCap — o
    /// createSurface e a definição com "root" são sempre preservados.
    /// </summary>
    public const int MaxA2uiMessagesPerResponse = 6;

    /// <summary>
    /// Tamanho máximo de uma linha de mensagem A2UI. Sem isso, uma linha
    /// gigante (componentes/estilo colados) trafegava inteira no chunk "a2ui"
    /// e no processamento do renderer.
    /// </summary>
    public const int MaxA2uiMessageBytes = 32 * 1024;

    /// <summary>
    /// Tenta extrair mensagens A2UI do conteúdo. Retorna o conteúdo "limpo"
    /// (sem os blocos a2ui) e a lista de mensagens A2UI válidas.
    /// </summary>
    /// <param name="content">Conteúdo bruto do LLM (tokens concatenados).</param>
    /// <param name="onInvalidLine">
    /// Diagnóstico opcional de linhas descartadas/reparadas e do teto atingido.
    /// Antes o descarte era SILENCIOSO: o LLM emitia o createSurface, a linha
    /// updateComponents vinha com JSON inválido, o servidor a jogava fora sem
    /// log e o usuário só via o card preso em "Loading surface..." sem nenhuma
    /// pista (caso real de 2026-10-08).
    /// </param>
    public static (string CleanContent, List<string> A2uiMessages) Extract(
        string content, Action<string>? onInvalidLine = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (content ?? string.Empty, new List<string>());

        var clean = new StringBuilder(content.Length);
        var diagnostics = new DiagnosticLimiter(onInvalidLine);
        var candidates = new List<A2uiLineInfo>();
        var surfaceEmitted = false; // C2: no máximo 1 surface por resposta

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var inA2uiBlock = false;
        var blockBuffer = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (!inA2uiBlock)
            {
                // Abre bloco: cerca de código com a linguagem a2ui.
                if (A2uiOpenRegex.IsMatch(line))
                {
                    inA2uiBlock = true;
                    blockBuffer.Clear();
                    continue;
                }
                clean.Append(rawLine).Append('\n');
                continue;
            }

            // Fecha bloco: cerca de código genérica.
            if (line.StartsWith("```"))
            {
                inA2uiBlock = false;
                CollectBlock(blockBuffer.ToString(), candidates, ref surfaceEmitted, diagnostics);
                continue;
            }
            blockBuffer.Append(rawLine).Append('\n');
        }

        // Bloco não fechado: processa o que sobrou.
        if (inA2uiBlock)
            CollectBlock(blockBuffer.ToString(), candidates, ref surfaceEmitted, diagnostics);

        var messages = ApplyCap(candidates, MaxA2uiMessagesPerResponse, diagnostics.Report);
        return (clean.ToString().TrimEnd('\n'), messages);
    }

    /// <summary>
    /// Coleta as mensagens de UM bloco. Aplica a regra C2 (no máximo 1
    /// createSurface/surface por resposta) e delega o teto ao ApplyCap, que roda
    /// depois de TODOS os blocos — antes o corte acontecia por bloco e podia
    /// cortar justamente a definição da interface.
    /// </summary>
    private static void CollectBlock(
        string block, List<A2uiLineInfo> candidates, ref bool surfaceEmitted, DiagnosticLimiter diagnostics)
    {
        foreach (var info in ParseBlock(block, diagnostics.Report))
        {
            // C2: no máximo 1 createSurface por resposta - updateComponents/
            // updateDataModel/deleteSurface da MESMA surface são permitidos.
            if (info.CreatesSurface && surfaceEmitted)
            {
                diagnostics.Report("segunda surface na resposta foi descartada (máx. 1 createSurface por resposta)");
                continue;
            }
            if (info.CreatesSurface) surfaceEmitted = true;
            candidates.Add(info);
        }
    }

    /// <summary>
    /// Aplica o teto PRESERVANDO o essencial da interface: o createSurface e a
    /// ÚLTIMA definição completa (a mensagem updateComponents que contém o
    /// componente "root") de cada surface. O restante do orçamento é preenchido
    /// com as mensagens MAIS RECENTES, porque o estado final é o que importa.
    ///
    /// Antes o corte era cego por ordem: um wizard que emitia um updateDataModel
    /// por etapa estourava o teto ANTES do updateComponents, a interface era
    /// emitida sem componentes e a bolha ficava em "Loading surface..." mesmo
    /// com todo o JSON válido (bug identificado na revisão de 2026-10-08).
    /// </summary>
    private static List<string> ApplyCap(List<A2uiLineInfo> all, int max, Action<string> report)
    {
        if (all.Count <= max)
            return all.Select(m => m.Json).ToList();

        var keep = new HashSet<int>();
        var createKept = false;
        var lastRootBySurface = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].CreatesSurface && !createKept)
            {
                keep.Add(i);
                createKept = true;
            }
            if (all[i].DefinesRoot)
                lastRootBySurface[all[i].SurfaceId] = i;
        }
        foreach (var index in lastRootBySurface.Values)
            keep.Add(index);

        for (var i = all.Count - 1; i >= 0 && keep.Count < max; i--)
            keep.Add(i);

        var ordered = keep.OrderBy(i => i).Take(max).ToList();
        var dropped = all.Count - ordered.Count;
        if (dropped > 0)
            report($"teto de {max} mensagens A2UI por resposta atingido: {dropped} mensagem(ns) descartada(s) — createSurface e a definição com 'root' foram preservadas");

        return ordered.Select(i => all[i].Json).ToList();
    }

    /// <summary>
    /// SurfaceIds criadas nesta resposta mas SEM definição completa (nenhuma
    /// mensagem updateComponents com "root"). O renderer fica em
    /// "Loading surface..." sem ela — o chamador usa isto para diagnosticar e,
    /// se quiser, pedir a interface de novo ao LLM.
    /// </summary>
    public static List<string> SurfacesMissingDefinition(IReadOnlyList<string>? messages)
    {
        var missing = new List<string>();
        if (messages == null || messages.Count == 0) return missing;

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var info = Classify(message);
            if (!info.DefinesRoot || info.SurfaceId.Length == 0) continue;
            defined.Add(info.SurfaceId);
        }
        foreach (var message in messages)
        {
            var info = Classify(message);
            if (!info.CreatesSurface || info.SurfaceId.Length == 0) continue;
            if (!defined.Contains(info.SurfaceId) && !missing.Contains(info.SurfaceId))
                missing.Add(info.SurfaceId);
        }
        return missing;
    }

    /// <summary>
    /// Texto do diagnóstico de interface incompleta (chunk "a2ui_incomplete") ou
    /// null quando toda surface criada tem definição. Centraliza a decisão para
    /// os TRÊS caminhos de entrega (streaming round 1, multi-round e sync).
    /// </summary>
    public static string? BuildIncompleteDiagnostic(IReadOnlyList<string>? messages)
    {
        var missing = SurfacesMissingDefinition(messages);
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private static IEnumerable<A2uiLineInfo> ParseBlock(string block, Action<string> report)
    {
        foreach (var rawLine in block.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            // Linha gigante não é uma mensagem declarativa plausível; descartar
            // protege o SSE e o renderer de payload anômalo.
            if (Encoding.UTF8.GetByteCount(line) > MaxA2uiMessageBytes)
            {
                report($"linha a2ui acima do teto de {MaxA2uiMessageBytes} bytes foi descartada");
                continue;
            }
            if (IsValidA2uiMessage(line))
            {
                yield return Classify(line);
                continue;
            }
            // Defeito mais recorrente do LLM: fecha-chaves faltando dentro do
            // updateComponents. Reparar devolve os componentes; descartar
            // deixava a surface eternamente em "Loading surface...".
            if (TryRepairMissingClosers(line, out var repaired) && IsValidA2uiMessage(repaired))
            {
                report("linha a2ui com JSON inválido foi reparada (fechamentos ausentes): componentes recuperados");
                yield return Classify(repaired);
                continue;
            }
            report($"linha a2ui descartada (JSON inválido ou verbo A2UI ausente): {ShortenForLog(line, 160)}");
        }
    }

    /// <summary>
    /// Classifica a mensagem (já validada) para o teto: surfaceId, se cria a
    /// surface e se é a definição completa (contém o componente "root").
    /// </summary>
    private static A2uiLineInfo Classify(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("createSurface", out var create) && create.ValueKind == JsonValueKind.Object)
                return new A2uiLineInfo(line, ReadSurfaceId(create), CreatesSurface: true, DefinesRoot: false);

            if (root.TryGetProperty("updateComponents", out var update) && update.ValueKind == JsonValueKind.Object)
                return new A2uiLineInfo(line, ReadSurfaceId(update), CreatesSurface: false, DefinesRoot: HasRootComponent(update));

            foreach (var verb in new[] { "updateDataModel", "deleteSurface" })
            {
                if (root.TryGetProperty(verb, out var other) && other.ValueKind == JsonValueKind.Object)
                    return new A2uiLineInfo(line, ReadSurfaceId(other), CreatesSurface: false, DefinesRoot: false);
            }
        }
        catch (JsonException)
        {
            // Mensagem já validada antes; cai no classificador vazio.
        }
        return new A2uiLineInfo(line, "", CreatesSurface: false, DefinesRoot: false);
    }

    private static string ReadSurfaceId(JsonElement element) =>
        element.TryGetProperty("surfaceId", out var id) && id.ValueKind == JsonValueKind.String
            ? (id.GetString() ?? "").Trim()
            : "";

    private static bool HasRootComponent(JsonElement update)
    {
        if (!update.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object) continue;
            if (component.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                string.Equals(id.GetString()?.Trim(), "root", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Repara o defeito mais recorrente dos payloads A2UI gerados pelo LLM: JSON
    /// com fecha-chaves/colchetes FALTANDO. O modelo fecha context/event/action
    /// mas esquece de fechar o objeto do componente (ou fecha o último a menos),
    /// o que invalida a linha INTEIRA de updateComponents. Sem o reparo a surface
    /// nasce vazia: o createSurface e o updateDataModel passam, os componentes
    /// não, e o usuário só vê "Loading surface..." (caso real de 2026-10-08,
    /// surface paper_jam_wizard6 com 3 botões de navegação).
    ///
    /// Conservador de propósito: só atua em linhas que contenham "components";
    /// insere os fechamentos que faltam imediatamente antes de um novo
    /// componente irmão e, no fim, fecha o que sobrou na ordem inversa da pilha.
    /// Qualquer anomalia que não seja um objeto aberto à espera de "}" devolve
    /// false e a linha segue o caminho de descarte. O resultado ainda passa por
    /// IsValidA2uiMessage e, depois, pelo AiChatA2uiValidator.
    /// </summary>
    internal static bool TryRepairMissingClosers(string line, out string repaired)
    {
        repaired = string.Empty;
        if (line.Length == 0 || line[0] != '{') return false;

        var componentsKey = line.IndexOf("\"components\"", StringComparison.Ordinal);
        if (componentsKey < 0) return false;
        var arrayStart = line.IndexOf('[', componentsKey);
        if (arrayStart < 0) return false;

        var sb = new StringBuilder(line.Length + 8);
        var stack = new List<char>(16);
        var inString = false;
        var escaped = false;
        var changed = false;
        var arrayDepth = -1;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    sb.Append(c);
                    continue;
                case '{':
                case '[':
                    stack.Add(c);
                    if (i == arrayStart) arrayDepth = stack.Count;
                    sb.Append(c);
                    continue;
                case '}':
                case ']':
                    // Fechamento cruzado: o objeto anterior ficou aberto (ex.:
                    // fechamento do action sem fechar o componente, seguido de
                    // outro componente). Insere o "}" ausente antes de aplicar
                    // o fechamento atual.
                    while (stack.Count > 0 && !CloserMatches(stack[^1], c))
                    {
                        if (stack[^1] != '{') return false;
                        stack.RemoveAt(stack.Count - 1);
                        sb.Append('}');
                        changed = true;
                    }
                    if (stack.Count == 0) return false;
                    stack.RemoveAt(stack.Count - 1);
                    sb.Append(c);
                    continue;
                case ',':
                    if (arrayDepth > 0 && stack.Count > arrayDepth && StartsComponent(line, i + 1))
                    {
                        while (stack.Count > arrayDepth)
                        {
                            var open = stack[^1];
                            stack.RemoveAt(stack.Count - 1);
                            sb.Append(open == '{' ? '}' : ']');
                            changed = true;
                        }
                    }
                    sb.Append(c);
                    continue;
                default:
                    sb.Append(c);
                    continue;
            }
        }

        if (!changed) return false;
        while (stack.Count > 0)
        {
            var open = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            sb.Append(open == '{' ? '}' : ']');
        }
        repaired = sb.ToString();
        return true;
    }

    private static bool CloserMatches(char open, char close) =>
        (open == '{' && close == '}') || (open == '[' && close == ']');

    private static bool StartsComponent(string line, int index)
    {
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
        return line.AsSpan(index).StartsWith("{\"id\"", StringComparison.Ordinal);
    }

    private static string ShortenForLog(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static bool IsValidA2uiMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("version", out var versionProp))
                return false;
            var version = versionProp.GetString();
            if (string.IsNullOrWhiteSpace(version)) return false;

            foreach (var verb in A2uiVerbs)
            {
                if (doc.RootElement.TryGetProperty(verb, out _))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Mensagem A2UI classificada para o teto de entrega.</summary>
    private readonly record struct A2uiLineInfo(string Json, string SurfaceId, bool CreatesSurface, bool DefinesRoot);

    /// <summary>
    /// Limita quantos diagnósticos sobem por resposta: um bloco corrompido com
    /// dezenas de linhas gerava dezenas de LogWarning por turno (revisão de
    /// 2026-10-08).
    /// </summary>
    private sealed class DiagnosticLimiter
    {
        private const int MaxReports = 3;
        private readonly Action<string>? _sink;
        private int _sent;

        public DiagnosticLimiter(Action<string>? sink) => _sink = sink;

        public void Report(string reason)
        {
            if (_sink == null) return;
            if (_sent < MaxReports)
            {
                _sent++;
                _sink(reason);
                return;
            }
            if (_sent == MaxReports)
            {
                _sent++;
                _sink($"demais diagnósticos A2UI desta resposta foram omitidos (limite de {MaxReports})");
            }
        }
    }
}
