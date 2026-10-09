using System.Text.Json;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Valida as mensagens A2UI extraídas ANTES de virarem chunks para o agent.
///
/// Por que existe: o renderer (catálogo basic v0.9, schemas ESTRITOS) rejeita
/// referências inexistentes com "Component not found" — o que chegava ao usuário
/// como "Não foi possível exibir a interface interativa gerada.". O bug real: o
/// modelo emitia <c>Button.child</c> com o TEXTO do rótulo ("Instalar"), porque o
/// system prompt ensinava errado. Corrigido o prompt, esta validação é a rede de
/// segurança: uma interface inválida é DESCARTADA por inteiro (o usuário segue
/// vendo o texto) em vez de renderizar e falhar.
/// </summary>
public static class AiChatA2uiValidator
{
    // Lista EXATA dos nomes de componente do basic_catalog v0.9 embutido no
    // renderer (a2ui-bundle.js). Qualquer outro nome ("StatusBar", "Dropdown",
    // "Table"...) derruba a interface inteira no processamento.
    private static readonly HashSet<string> KnownComponents = new(StringComparer.Ordinal)
    {
        "Text", "Button", "TextField", "Row", "Column", "List", "Image", "Icon",
        "Video", "AudioPlayer", "Card", "Divider", "CheckBox", "Slider",
        "DateTimeInput", "ChoicePicker", "Tabs", "Modal",
        // Select = dropdown próprio do Discovery (components/Select.js no bundle
        // A2UI). Não existe no basic v0.9; sem ele aqui o validador descartaria
        // a surface inteira.
        "Select"
    };

    /// <summary>
    /// Nomes conhecidos pelo validador, expostos para o teste de paridade com o
    /// catálogo embarcado no renderer (a2ui-bundle.js). Sem esse teste, um
    /// componente novo no bundle e ausente aqui derruba a surface INTEIRA.
    /// </summary>
    public static IReadOnlyCollection<string> KnownComponentNames => KnownComponents;

    // Propriedades que referenciam OUTRO componente por id.
    private static readonly string[] SingleReferenceProps = { "child", "trigger", "content" };

    // Props SEM as quais o componente nem chega a ser criado (o schema zod do
    // renderer é estrito): o componente simplesmente DESAPARECE do card, sem
    // aviso. O validador não valida o schema inteiro — cobre só esses casos.
    private static readonly Dictionary<string, string[]> RequiredProps = new(StringComparer.Ordinal)
    {
        ["Text"] = new[] { "text" },
        ["ChoicePicker"] = new[] { "options" },
        ["Select"] = new[] { "options" },
    };

    /// <summary>
    /// Descarta surfaces inválidas. Devolve as mensagens válidas (mesma ordem) e
    /// os motivos dos descartes (para log).
    /// </summary>
    public static (List<string> Valid, List<string> Errors) Validate(IReadOnlyList<string>? messages)
    {
        var errors = new List<string>();
        if (messages == null || messages.Count == 0)
            return (new List<string>(), errors);

        var parsed = new List<ParsedMessage>(messages.Count);
        foreach (var message in messages)
            parsed.Add(Parse(message));

        // Ids definidos por surface NESTA resposta (permite update incremental
        // que referencia componente definido em mensagem anterior da resposta).
        var idsBySurface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            if (p.ComponentIds.Count == 0) continue;
            if (!idsBySurface.TryGetValue(p.SurfaceId, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                idsBySurface[p.SurfaceId] = ids;
            }
            foreach (var id in p.ComponentIds) ids.Add(id);
        }

        var invalidSurfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            if (invalidSurfaces.Contains(p.SurfaceId)) continue;

            if (p.Errors.Count > 0)
            {
                invalidSurfaces.Add(p.SurfaceId);
                foreach (var e in p.Errors)
                    errors.Add($"surface '{p.SurfaceId}': {e}");
            }
        }

        // Surface CRIADA nesta resposta precisa definir o componente raiz: sem
        // 'root' o renderer não monta a árvore e a bolha fica em "loading".
        // Updates incrementais (surface de resposta anterior) não passam por aqui.
        var createdHere = new HashSet<string>(StringComparer.Ordinal);
        var surfacesWithRoot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            if (p.CreatesSurface) createdHere.Add(p.SurfaceId);
            if (p.FullDefinition) surfacesWithRoot.Add(p.SurfaceId);
        }
        var definesComponentsHere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parsed)
        {
            if (p.ComponentIds.Count > 0) definesComponentsHere.Add(p.SurfaceId);
        }
        foreach (var surfaceId in createdHere)
        {
            // Sem nenhuma mensagem de componentes nesta resposta, a definição
            // pode vir depois (outro turno) — não é erro. Com componentes e sem
            // root, o renderer deixaria a bolha em "loading" para sempre.
            if (!definesComponentsHere.Contains(surfaceId)) continue;
            if (surfacesWithRoot.Contains(surfaceId)) continue;
            invalidSurfaces.Add(surfaceId);
            errors.Add($"surface '{surfaceId}': definição sem o componente raiz 'root' (a superfície ficaria em loading)");
        }

        foreach (var p in parsed)
        {
            if (invalidSurfaces.Contains(p.SurfaceId)) continue;

            // Referências só são exigidas na mensagem de DEFINIÇÃO COMPLETA (a
            // que contém o root): updates incrementais podem referenciar
            // componentes já existentes na surface.
            if (!p.FullDefinition || p.References.Count == 0) continue;

            idsBySurface.TryGetValue(p.SurfaceId, out var known);
            foreach (var reference in p.References)
            {
                if (known != null && known.Contains(reference)) continue;
                invalidSurfaces.Add(p.SurfaceId);
                errors.Add($"surface '{p.SurfaceId}': referência '{reference}' não existe na definição (child/children são IDs de componentes, nunca texto)");
                break;
            }
        }

        var valid = new List<string>(messages.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            if (!invalidSurfaces.Contains(parsed[i].SurfaceId))
                valid.Add(parsed[i].Json);
        }
        return (valid, errors);
    }

    private static ParsedMessage Parse(string json)
    {
        var p = new ParsedMessage { Json = json };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                p.Errors.Add("mensagem não é um objeto JSON");
                return p;
            }

            if (root.TryGetProperty("createSurface", out var create))
            {
                p.SurfaceId = ReadSurfaceId(create, "createSurface", p);
                p.CreatesSurface = true;
                // O renderer IGNORA components dentro do createSurface; quando o
                // modelo insiste nesse formato, a superfície nasce vazia (em
                // "loading"). Melhor descartar a interface e manter o texto.
                if (create.TryGetProperty("components", out var inlineComponents)
                    && inlineComponents.ValueKind == JsonValueKind.Array
                    && inlineComponents.GetArrayLength() > 0)
                {
                    p.Errors.Add("createSurface não deve conter 'components' (use uma mensagem updateComponents)");
                }
            }
            else if (root.TryGetProperty("updateComponents", out var update))
            {
                p.SurfaceId = ReadSurfaceId(update, "updateComponents", p);
                if (!update.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
                {
                    p.Errors.Add("updateComponents sem array 'components'");
                    return p;
                }
                if (components.GetArrayLength() == 0)
                {
                    // Superfície sem nenhum componente: sem o root o renderer
                    // fica em "loading" para sempre (bolha vazia).
                    p.Errors.Add("updateComponents sem componentes");
                    return p;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var component in components.EnumerateArray())
                    ReadComponent(component, p, seen);
            }
            else if (root.TryGetProperty("updateDataModel", out var data))
            {
                p.SurfaceId = ReadSurfaceId(data, "updateDataModel", p);
            }
            else if (root.TryGetProperty("deleteSurface", out var delete))
            {
                p.SurfaceId = ReadSurfaceId(delete, "deleteSurface", p);
            }
            else
            {
                p.Errors.Add("verbo A2UI desconhecido");
            }
        }
        catch (JsonException ex)
        {
            p.Errors.Add("json inválido: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(p.SurfaceId))
            p.Errors.Add("surfaceId ausente");
        return p;
    }

    private static string ReadSurfaceId(JsonElement element, string verb, ParsedMessage p)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("surfaceId", out var sid) || sid.ValueKind != JsonValueKind.String)
        {
            p.Errors.Add($"{verb} sem 'surfaceId' string");
            return "";
        }
        var id = sid.GetString()?.Trim() ?? "";
        if (id.Length == 0) p.Errors.Add($"{verb} com 'surfaceId' vazio");
        return id;
    }

    /// <summary>
    /// Armadilha do renderer (verificada empiricamente em 2026-10-08): um
    /// `action.event.context` com a chave `path` no NÍVEL DE CIMA é
    /// interpretado como um DataBinding — o objeto INTEIRO é resolvido para o
    /// valor de /x e, quando /x não existe, a chave some do context entregue
    /// (o clique chega com `context: {}`). Melhor descartar a surface e mostrar
    /// o fallback do que entregar um botão morto.
    ///
    /// O binding ANINHADO é intencional e permitido (o prompt ensina
    /// `context:{"etapa":{"path":"/etapa"}}` para enviar o valor atual ao
    /// agente). Ele é resolvido na hora do clique: se o caminho não tiver sido
    /// populado por um `updateDataModel` do mesmo turno, a chave não aparece no
    /// context — por isso o prompt exige ligar os campos ao data model.
    /// </summary>
    private static void CheckActionContextPathTrap(JsonElement component, string id, ParsedMessage p)
    {
        if (!component.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object)
            return;
        if (!action.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return;
        if (!evt.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Object)
            return;
        if (ctx.TryGetProperty("path", out _))
            p.Errors.Add($"'{id}'.action.event.context usa a chave 'path' (o renderer trata o contexto como binding e a ação NÃO dispara — use 'target', 'campo' etc.)");
    }

    private static void ReadComponent(JsonElement component, ParsedMessage p, HashSet<string> seen)
    {
        if (component.ValueKind != JsonValueKind.Object)
        {
            p.Errors.Add("componente não é um objeto");
            return;
        }

        var id = component.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()?.Trim() ?? ""
            : "";
        if (id.Length == 0)
        {
            p.Errors.Add("componente sem 'id'");
        }
        else
        {
            if (HasWhitespace(id)) p.Errors.Add($"id '{id}' contém espaços");
            if (!seen.Add(id)) p.Errors.Add($"id '{id}' duplicado na mensagem");
            p.ComponentIds.Add(id);
            if (id == "root") p.FullDefinition = true;
        }

        var type = component.TryGetProperty("component", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()?.Trim() ?? ""
            : "";
        if (type.Length == 0)
        {
            p.Errors.Add($"componente '{id}' sem 'component'");
            return;
        }
        if (!KnownComponents.Contains(type))
            p.Errors.Add($"componente '{id}' com tipo desconhecido '{type}'");
        else if (RequiredProps.TryGetValue(type, out var required))
        {
            foreach (var prop in required)
            {
                if (!component.TryGetProperty(prop, out _))
                    p.Errors.Add($"'{id}' ({type}) sem a propriedade obrigatória '{prop}' (o componente não seria renderizado)");
            }
        }

        foreach (var prop in SingleReferenceProps)
        {
            if (component.TryGetProperty(prop, out var refEl))
                AddReference(refEl, prop, id, p);
        }

        CheckActionContextPathTrap(component, id, p);

        if (component.TryGetProperty("children", out var childrenEl))
        {
            switch (childrenEl.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var child in childrenEl.EnumerateArray())
                    {
                        if (child.ValueKind != JsonValueKind.String)
                        {
                            p.Errors.Add($"'{id}'.children com item não textual");
                            continue;
                        }
                        var refId = child.GetString()?.Trim() ?? "";
                        if (refId.Length == 0)
                        {
                            p.Errors.Add($"'{id}'.children com id vazio");
                            continue;
                        }
                        if (HasWhitespace(refId))
                        {
                            p.Errors.Add($"'{id}'.children com '{refId}' (parece texto, não ID)");
                            continue;
                        }
                        p.References.Add(refId);
                    }
                    break;
                case JsonValueKind.Object:
                    // Template dinâmico: { componentId, path }
                    if (childrenEl.TryGetProperty("componentId", out var templateId) && templateId.ValueKind == JsonValueKind.String)
                        p.References.Add(templateId.GetString()?.Trim() ?? "");
                    else
                        p.Errors.Add($"'{id}'.children template sem 'componentId'");
                    break;
                default:
                    p.Errors.Add($"'{id}'.children com tipo inválido");
                    break;
            }
        }

        if (component.TryGetProperty("tabs", out var tabsEl) && tabsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tab in tabsEl.EnumerateArray())
            {
                if (tab.ValueKind != JsonValueKind.Object)
                {
                    p.Errors.Add($"'{id}'.tabs com item não objeto");
                    continue;
                }
                if (!tab.TryGetProperty("title", out _)) p.Errors.Add($"'{id}'.tabs sem 'title'");
                if (tab.TryGetProperty("child", out var tabChild)) AddReference(tabChild, "tabs.child", id, p);
                else p.Errors.Add($"'{id}'.tabs sem 'child'");
            }
        }
    }

    private static void AddReference(JsonElement element, string prop, string ownerId, ParsedMessage p)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            p.Errors.Add($"'{ownerId}'.{prop} não é string");
            return;
        }
        var refId = element.GetString()?.Trim() ?? "";
        if (refId.Length == 0)
        {
            p.Errors.Add($"'{ownerId}'.{prop} vazio");
            return;
        }
        if (HasWhitespace(refId))
        {
            p.Errors.Add($"'{ownerId}'.{prop} = '{refId}' não é um ID de componente (o rótulo deve ir em um componente Text)");
            return;
        }
        p.References.Add(refId);
    }

    private static bool HasWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    private sealed class ParsedMessage
    {
        public string Json { get; init; } = "";
        public string SurfaceId { get; set; } = "";
        public bool CreatesSurface { get; set; }
        public bool FullDefinition { get; set; }
        public List<string> ComponentIds { get; } = new();
        public List<string> References { get; } = new();
        public List<string> Errors { get; } = new();
    }
}
