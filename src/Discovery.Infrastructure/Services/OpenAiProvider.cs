using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discovery.Infrastructure.Services;

public class OpenAiProvider : ILlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiProvider> _logger;

    public OpenAiProvider(IHttpClientFactory httpClientFactory, ILogger<OpenAiProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Cria um HttpClient para chamadas LLM. Quando TimeoutMs é especificado no
    /// LlmOptions, aplica esse timeout por request; caso contrário usa o client
    /// nomeado "AiChat" (default 60s). Isso torna o timeout configurável via
    /// AIIntegrationSettings.TimeoutMs, que antes era ignorado.
    /// </summary>
    private HttpClient BuildHttpClient(LlmOptions options)
    {
        // A6: NÃO mutar client.Timeout em HttpClient da IHttpClientFactory
        // (race/InvalidOperationException em concorrência). O timeout é
        // aplicado por request via CancelAfter nos chamadores.
        return _httpClientFactory.CreateClient("AiChat");
    }

    /// <summary>Trunca um texto para log sem depender de helpers externos.</summary>
    private static string Shorten(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");

    /// <summary>
    /// Cria um CTS com timeout por request, encadeado ao token do chamador.
    /// Substitui o cap fixo de 60s do HttpClient "AiChat" por LlmOptions.TimeoutMs.
    /// </summary>
    private static CancellationTokenSource CreateTimeoutCts(CancellationToken ct, int timeoutMs)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs > 0 ? timeoutMs : 180_000);
        return cts;
    }

    /// <summary>
    /// Resolve a BaseUrl padrão ou da opção. Suporta Ollama como provider explícito.
    /// </summary>
    internal static string ResolveDefaultBaseUrl(string? provider)
    {
        if (string.Equals(provider, AIIntegrationSettings.ProviderOpenRouter, StringComparison.OrdinalIgnoreCase))
            return AIIntegrationSettings.OpenRouterDefaultBaseUrl;
        if (string.Equals(provider, AIIntegrationSettings.ProviderOllama, StringComparison.OrdinalIgnoreCase))
            return AIIntegrationSettings.OllamaDefaultBaseUrl;
        return AIIntegrationSettings.OpenAiDefaultBaseUrl;
    }

    /// <summary>
    /// Auto-detecta se o modelo pertence ao OpenRouter (modelos com "/" no nome: org/model)
    /// e corrige provider/baseUrl automaticamente para evitar erros 404 quando o provider
    /// está como "openai" mas o modelo é do OpenRouter.
    /// </summary>
    internal static (string Provider, string BaseUrl) AutoCorrectProviderAndBaseUrl(
        string? provider, string? baseUrl, string model)
    {
        // Modelos OpenAI nativos NÃO têm "/" no nome (gpt-4o-mini, gpt-4, o1, o3-mini, etc.)
        // Modelos OpenRouter SEMPRE têm "/" (meta-llama/llama-3.2-1b, nex-agi/nex-n2-mini, etc.)
        var isOpenRouterModel = model.Contains('/')
            && !model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            && !model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

        // Credencial por escopo pode ter BaseUrl = openrouter.ai com Provider
        // diferente de "openrouter" (default "openai" do AiProviderCredential) e
        // modelo sem "/" (ex: gpt-oss-120b sem prefixo). O alvo real da
        // requisição é a baseUrl — ela define o provider efetivo.
        var isOpenRouterBaseUrl = !string.IsNullOrWhiteSpace(baseUrl)
            && baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

        if (isOpenRouterModel || isOpenRouterBaseUrl)
        {
            var effectiveProvider = AIIntegrationSettings.ProviderOpenRouter;
            var effectiveBaseUrl = !string.IsNullOrWhiteSpace(baseUrl)
                ? baseUrl
                : AIIntegrationSettings.OpenRouterDefaultBaseUrl;
            return (effectiveProvider, effectiveBaseUrl);
        }

        var finalBaseUrl = !string.IsNullOrWhiteSpace(baseUrl)
            ? baseUrl
            : ResolveDefaultBaseUrl(provider);

        return (provider ?? AIIntegrationSettings.ProviderOpenAi, finalBaseUrl!);
    }

    /// <summary>
    /// Aplica headers OpenRouter se o provider for openrouter OU se a baseUrl
    /// resolver para openrouter.ai. Credenciais por escopo (AiProviderCredential)
    /// podem ter BaseUrl apontando pro OpenRouter com Provider diferente
    /// (default "openai") — nesse caso o OpenRouter registrava o app como
    /// "Unknown". Espelha a lógica do OpenAiEmbeddingProvider (check por baseUrl).
    /// </summary>
    internal static void ApplyOpenRouterHeaders(HttpRequestMessage request, LlmOptions options, string baseUrl)
    {
        var isOpenRouter = string.Equals(options.Provider, AIIntegrationSettings.ProviderOpenRouter, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(baseUrl)
                && baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase));

        if (!isOpenRouter)
            return;

        // Sempre enviar HTTP-Referer e X-Title para identificar o app nos logs do OpenRouter
        // Fallback para defaults se não configurados (mantém consistência com embeddings)
        request.Headers.TryAddWithoutValidation("HTTP-Referer",
            !string.IsNullOrWhiteSpace(options.OpenRouterReferer)
                ? options.OpenRouterReferer
                : "https://discovery-rmm.local");

        // Headers canônicos atuais (docs: app-attribution). X-OpenRouter-Title é
        // exigido para URLs não rastreáveis publicamente (ex: .local); X-Title
        // mantido por compatibilidade.
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title",
            !string.IsNullOrWhiteSpace(options.OpenRouterTitle)
                ? options.OpenRouterTitle
                : "Discovery RMM");
        request.Headers.TryAddWithoutValidation("X-Title",
            !string.IsNullOrWhiteSpace(options.OpenRouterTitle)
                ? options.OpenRouterTitle
                : "Discovery RMM");

        if (!string.IsNullOrWhiteSpace(options.OpenRouterCategories))
        {
            request.Headers.TryAddWithoutValidation("X-OpenRouter-Categories", options.OpenRouterCategories);
            request.Headers.TryAddWithoutValidation("X-Categories", options.OpenRouterCategories);
        }

        // Sticky session routing: garante mesmo provider em todos os turnos da conversa
        // e habilita prompt caching para reduzir latência e custo
        if (!string.IsNullOrWhiteSpace(options.SessionId))
            request.Headers.TryAddWithoutValidation("x-session-id", options.SessionId);
    }

    /// <summary>
    /// Adiciona session_id ao payload JSON para sticky sessions no OpenRouter.
    /// Funciona para qualquer provider compatível com OpenAI/OpenRouter.
    /// </summary>
    private static void AddSessionIdToPayload(Dictionary<string, object?> payloadDict, LlmOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SessionId))
            payloadDict["session_id"] = options.SessionId;
    }

    /// <summary>
    /// B16: envia o controle de reasoning ao OpenRouter (config que era morta em
    /// AIIntegrationSettings). Só para OpenRouter para não causar 400 nos demais.
    /// </summary>
    private static void AddReasoningToPayload(Dictionary<string, object?> payloadDict, LlmOptions options)
    {
        var (provider, baseUrl) = AutoCorrectProviderAndBaseUrl(options.Provider, options.BaseUrl, options.Model ?? string.Empty);
        var isOpenRouter = string.Equals(provider, AIIntegrationSettings.ProviderOpenRouter, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase));
        if (!isOpenRouter) return;

        if (options.ReasoningEnabled)
            payloadDict["reasoning"] = new { effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? "medium" : options.ReasoningEffort };
        else
            payloadDict["reasoning"] = new { enabled = false };
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var model = options.Model;
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Modelo de IA não definido no banco para o escopo atual.");

            var apiKey = options.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("API key de IA não definida no banco para o escopo atual.");

            // Auto-corrigir provider/baseUrl com base no nome do modelo
            var (effectiveProvider, baseUrl) = AutoCorrectProviderAndBaseUrl(
                options.Provider, options.BaseUrl, model);

            // IMPORTANTE: NUNCA logar _apiKey
            _logger.LogInformation(
                "Calling LLM provider={Provider} with {MessageCount} messages, maxTokens={MaxTokens}, model={Model}",
                effectiveProvider, messages.Count, options.MaxTokens, model);

            // Preparar mensagens no formato OpenAI
            var openAiMessages = new List<object>
            {
                new { role = "system", content = systemPrompt }
            };

            foreach (var msg in messages)
            {
                if (msg.Role == "tool")
                {
                    openAiMessages.Add(new
                    {
                        role = "tool",
                        tool_call_id = msg.ToolCallId,
                        content = msg.Content
                    });
                }
                else if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
                {
                    // Assistant com tool_calls: serializa corretamente para que o LLM
                    // saiba qual tool foi chamada com quais argumentos.
                    openAiMessages.Add(new
                    {
                        role = "assistant",
                        content = string.IsNullOrEmpty(msg.Content) ? null : msg.Content,
                        tool_calls = msg.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new
                            {
                                name = tc.Name,
                                arguments = tc.ArgumentsJson
                            }
                        }).ToList()
                    });
                }
                else
                {
                    openAiMessages.Add(new
                    {
                        role = msg.Role,
                        content = msg.Content
                    });
                }
            }

            // Montar payload como dicionário para suportar campos dinâmicos (session_id)
            var payloadDict = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = openAiMessages,
                ["max_tokens"] = options.MaxTokens,
                ["temperature"] = options.Temperature,
                ["top_p"] = options.TopP,
                ["frequency_penalty"] = options.FrequencyPenalty,
                ["presence_penalty"] = options.PresencePenalty,
                ["seed"] = options.Seed,
                ["response_format"] = options.ResponseFormat,
            };

            if (options.EnableTools && options.Tools != null)
            {
                payloadDict["tools"] = options.Tools.Select(t => new
                {
                    type = "function",
                    function = new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = t.Schema
                    }
                }).ToList();
            }

            AddSessionIdToPayload(payloadDict, options);
        AddReasoningToPayload(payloadDict, options);

            var content = new StringContent(
                JsonSerializer.Serialize(payloadDict, SJsonOpts),
                Encoding.UTF8,
                "application/json");

            var requestUri = BuildRequestUri(baseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            // Usa effectiveProvider para decidir headers OpenRouter
            var openRouterOpts = options with { Provider = effectiveProvider };
            ApplyOpenRouterHeaders(request, openRouterOpts, baseUrl);

            var httpClient = BuildHttpClient(options);
            var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenAI API error: {StatusCode} - {Error}",
                    response.StatusCode, errorBody);
                throw new HttpRequestException(BuildProviderErrorMessage(response.StatusCode, errorBody));
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody)
                ?? throw new InvalidOperationException("Failed to deserialize OpenAI response");

            var choice = result.Choices.FirstOrDefault()
                ?? throw new InvalidOperationException("No choices in OpenAI response");

            // Verificar se há tool_calls
            List<LlmToolCall>? toolCalls = null;
            if (choice.Message.ToolCalls != null && choice.Message.ToolCalls.Any())
            {
                toolCalls = choice.Message.ToolCalls.Select(tc => new LlmToolCall(
                    tc.Id,
                    tc.Function.Name,
                    tc.Function.Arguments
                )).ToList();
            }

            return new LlmResponse(
                choice.Message.Content ?? string.Empty,
                result.Usage?.TotalTokens ?? 0,
                result.Model,
                toolCalls
            );
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("OpenAI request timeout");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API");
            throw;
        }
    }

    /// <summary>
    /// Chama a OpenAI com stream=true e yield os tokens incrementalmente via IAsyncEnumerable.
    /// Não suporta tool calls — a resposta é apenas texto.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CreateTimeoutCts(cancellationToken, options.TimeoutMs);
        var requestToken = timeoutCts.Token;

        var model = options.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Modelo de IA não definido no banco para o escopo atual.");

        var apiKey = options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key de IA não definida no banco para o escopo atual.");

        // Auto-corrigir provider/baseUrl com base no nome do modelo
        var (effectiveProvider, baseUrl) = AutoCorrectProviderAndBaseUrl(
            options.Provider, options.BaseUrl, model);

        _logger.LogInformation(
            "StreamAsync LLM provider={Provider}: {MessageCount} messages, model={Model}",
            effectiveProvider, messages.Count, model);

        // A8: serialização comum — antes o StreamAsync descartava histórico
        // com tool calls (role=tool / assistant.tool_calls), gerando 400 no
        // turno seguinte em qualquer provider.
        var openAiMessages = BuildOpenAiMessages(systemPrompt, messages);

        var payloadDict = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = openAiMessages,
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = options.Temperature,
            // A7: parametros opcionais (null = omitido no payload)
            ["top_p"] = options.TopP,
            ["frequency_penalty"] = options.FrequencyPenalty,
            ["presence_penalty"] = options.PresencePenalty,
            ["seed"] = options.Seed,
            ["response_format"] = options.ResponseFormat,
            ["stream"] = true
        };

        AddSessionIdToPayload(payloadDict, options);
        AddReasoningToPayload(payloadDict, options);

        var requestBody = new StringContent(
            JsonSerializer.Serialize(payloadDict, SJsonOpts),
            Encoding.UTF8,
            "application/json");

        var requestUri = BuildRequestUri(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = requestBody
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Usa effectiveProvider para decidir headers OpenRouter
        var openRouterOpts = options with { Provider = effectiveProvider };
        ApplyOpenRouterHeaders(request, openRouterOpts, baseUrl);

        var httpClient = BuildHttpClient(options);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            requestToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(requestToken);
            _logger.LogError("OpenAI stream error: {StatusCode} - {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException(BuildProviderErrorMessage(response.StatusCode, errorBody));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(requestToken)) != null)
        {
            requestToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line))
                continue;

            // Cada linha SSE começa com "data: "
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line.StartsWith("data: ", StringComparison.Ordinal)
                ? line["data: ".Length..]
                : line["data:".Length..];

            if (data == "[DONE]")
                yield break;

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var errProp))
                {
                    var msg = errProp.ValueKind == JsonValueKind.String
                        ? errProp.GetString()
                        : errProp.TryGetProperty("message", out var m) ? m.GetString() : errProp.GetRawText();
                    throw new InvalidOperationException($"Provider stream error: {Shorten(msg, 400)}");
                }
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                        && delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                    {
                        token = contentProp.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // linha malformada — ignorar
                continue;
            }

            if (!string.IsNullOrEmpty(token))
                yield return token;
        }
    }

    /// <summary>
    /// Streaming SSE com suporte a tool calls. Emite LlmStreamEvent (token, tool_calls, done)
    /// em vez de strings. Detecta finish_reason=tool_calls e emite as tool calls acumuladas.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamEvent> StreamWithToolsAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CreateTimeoutCts(cancellationToken, options.TimeoutMs);
        var requestToken = timeoutCts.Token;

        var model = options.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Modelo de IA não definido no banco para o escopo atual.");

        var apiKey = options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key de IA não definida no banco para o escopo atual.");

        // Auto-corrigir provider/baseUrl com base no nome do modelo
        var (effectiveProvider, baseUrl) = AutoCorrectProviderAndBaseUrl(
            options.Provider, options.BaseUrl, model);

        _logger.LogInformation(
            "StreamWithToolsAsync LLM provider={Provider}: {MessageCount} messages, model={Model}, tools={Tools}",
            effectiveProvider, messages.Count, model, options.EnableTools && options.Tools != null);

        var openAiMessages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var msg in messages)
        {
            if (msg.Role == "tool")
            {
                openAiMessages.Add(new { role = "tool", tool_call_id = msg.ToolCallId, content = msg.Content });
            }
            else if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                // Assistant com tool_calls: serializa corretamente para que o LLM
                // saiba qual tool foi chamada com quais argumentos.
                openAiMessages.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrEmpty(msg.Content) ? null : msg.Content,
                    tool_calls = msg.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = tc.ArgumentsJson
                        }
                    }).ToList()
                });
            }
            else
            {
                openAiMessages.Add(new { role = msg.Role, content = msg.Content });
            }
        }

        var payloadDict = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = openAiMessages,
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = options.Temperature,
            ["stream"] = true
        };

        if (options.EnableTools && options.Tools != null)
        {
            payloadDict["tools"] = options.Tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.Schema }
            }).ToList();
        }

        AddSessionIdToPayload(payloadDict, options);
        AddReasoningToPayload(payloadDict, options);

        var requestBody = new StringContent(
            JsonSerializer.Serialize(payloadDict, SJsonOpts),
            Encoding.UTF8,
            "application/json");

        var requestUri = BuildRequestUri(baseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = requestBody };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Usa effectiveProvider para decidir headers OpenRouter
        var openRouterOpts = options with { Provider = effectiveProvider };
        ApplyOpenRouterHeaders(request, openRouterOpts, baseUrl);

        var httpClient = BuildHttpClient(options);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(requestToken);
            _logger.LogError("OpenAI stream error: {StatusCode} - {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException(BuildProviderErrorMessage(response.StatusCode, errorBody));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Acumuladores de tool calls (delta.tool_calls chega em chunks incrementais)
        var pendingToolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        string? line;
        while ((line = await reader.ReadLineAsync(requestToken)) != null)
        {
            requestToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line.StartsWith("data: ", StringComparison.Ordinal)
                ? line["data: ".Length..]
                : line["data:".Length..]; // A10: gateways que enviam "data:{json}" sem espaço
            if (data == "[DONE]")
            {
                // A2: alguns providers terminam o stream com [DONE] SEM
                // finish_reason=tool_calls — sem isso as tool calls acumuladas
                // eram descartadas silenciosamente e o loop parava.
                if (pendingToolCalls.Count > 0)
                {
                    yield return new LlmStreamEvent(Type: "tool_calls",
                        ToolCalls: pendingToolCalls.Values.Select(tc => new LlmToolCall(
                            tc.Id, tc.Name, tc.Args.ToString())).ToList());
                }
                yield return new LlmStreamEvent(Type: "done");
                yield break;
            }

            // ── Parse chunk (fora de try-catch para permitir yield) ──
            LlmStreamEvent? parsed = null;
            try
            {
                parsed = ParseStreamChunk(data, pendingToolCalls);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                if (ex is InvalidOperationException) throw;
                _logger.LogWarning("Chunk SSE invalido do provider (ignorado): {Data}", Shorten(data, 300));
                continue;
            }

            if (parsed is not null)
            {
                yield return parsed;
                if (parsed.Type is "tool_calls" or "done")
                    yield break;
            }
        }
    }

    /// <summary>
    /// Parse um chunk SSE e retorna o LlmStreamEvent correspondente.
    /// Extraído para método separado para evitar yield dentro de try-catch.
    /// </summary>
    internal static LlmStreamEvent? ParseStreamChunk(string data, Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingToolCalls)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        // OpenRouter (e outros gateways) podem enviar um objeto de erro no meio
        // do stream, sem "choices". Antes isso lançava KeyNotFoundException e o
        // enumerador morria em silêncio (200 sem tokens). Agora é erro explícito.
        if (root.TryGetProperty("error", out var errProp))
        {
            var msg = errProp.ValueKind == JsonValueKind.String
                ? errProp.GetString()
                : errProp.TryGetProperty("message", out var m) ? m.GetString() : errProp.GetRawText();
            throw new InvalidOperationException($"Provider stream error: {Shorten(msg, 400)}");
        }

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return null;

        var choice = choices[0];
        var hasDelta = choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object;

        string? finishReason = null;
        if (choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind == JsonValueKind.String)
            finishReason = frProp.GetString();
        // OpenRouter: native_finish_reason reflete o motivo real do upstream.
        if (string.IsNullOrEmpty(finishReason)
            && choice.TryGetProperty("native_finish_reason", out var nfr) && nfr.ValueKind == JsonValueKind.String)
            finishReason = nfr.GetString();

        // 1. Delta content (texto). reasoning/reasoning_content NÃO é exibido.
        if (hasDelta && delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
        {
            var token = contentProp.GetString();
            if (!string.IsNullOrEmpty(token))
                return new LlmStreamEvent(Type: "token", Content: token);
        }

        // 2. Delta tool_calls (incremental)
        if (hasDelta && delta.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcProp.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object) continue;
                if (!tc.TryGetProperty("index", out var idxProp) || idxProp.ValueKind != JsonValueKind.Number) continue;
                var index = idxProp.GetInt32();

                if (!pendingToolCalls.ContainsKey(index))
                {
                    var id = tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()! : string.Empty;
                    var name = tc.TryGetProperty("function", out var fn0)
                        && fn0.ValueKind == JsonValueKind.Object
                        && fn0.TryGetProperty("name", out var n0) && n0.ValueKind == JsonValueKind.String
                        ? n0.GetString()! : string.Empty;
                    pendingToolCalls[index] = (id, name, new StringBuilder());
                }

                var existing = pendingToolCalls[index];
                // A1: alguns providers (OpenRouter/Anthropic/Gemini) enviam id/name
                // em chunks SUBSEQUENTES ao de criação — merge abaixo.
                if (tc.TryGetProperty("id", out var idDelta) && idDelta.ValueKind == JsonValueKind.String)
                {
                    var idVal = idDelta.GetString();
                    if (!string.IsNullOrEmpty(idVal)) existing.Id = idVal;
                }
                if (tc.TryGetProperty("function", out var fnDelta) && fnDelta.ValueKind == JsonValueKind.Object)
                {
                    if (fnDelta.TryGetProperty("name", out var nameDelta) && nameDelta.ValueKind == JsonValueKind.String)
                    {
                        var nameVal = nameDelta.GetString();
                        if (!string.IsNullOrEmpty(nameVal)) existing.Name = nameVal;
                    }
                    if (fnDelta.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
                        existing.Args.Append(argsProp.GetString());
                }
            }
        }

        int? tokensUsed = null;
        if (root.TryGetProperty("usage", out var usageProp) && usageProp.ValueKind == JsonValueKind.Object
            && usageProp.TryGetProperty("total_tokens", out var ttProp) && ttProp.ValueKind == JsonValueKind.Number)
            tokensUsed = ttProp.GetInt32();

        // 3. Qualquer finish_reason terminal encerra o round. tool_calls emite as
        // tool calls acumuladas; stop/length/content_filter/... emitem done.
        if (!string.IsNullOrEmpty(finishReason))
        {
            if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase) && pendingToolCalls.Count > 0)
            {
                var parsedToolCalls = pendingToolCalls.Values.Select(tc => new LlmToolCall(
                    tc.Id, tc.Name, tc.Args.ToString())).ToList();
                return new LlmStreamEvent(Type: "tool_calls", ToolCalls: parsedToolCalls, TokensUsed: tokensUsed);
            }
            return new LlmStreamEvent(Type: "done", TokensUsed: tokensUsed);
        }

        return null;
    }

    // DTOs internos para deserialização da resposta OpenAI
    private record OpenAiChatResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("choices")] List<OpenAiChoice> Choices,
        [property: JsonPropertyName("usage")] OpenAiUsage? Usage
    );

    private record OpenAiChoice(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("message")] OpenAiMessage Message,
        [property: JsonPropertyName("finish_reason")] string FinishReason
    );

    private record OpenAiMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls")] List<OpenAiToolCall>? ToolCalls
    );

    private record OpenAiToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] OpenAiFunction Function
    );

    private record OpenAiFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments
    );

    private record OpenAiUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens
    );

    // A9: options JSON estáticas — antes cada chamada instanciava uma nova.
    private static readonly JsonSerializerOptions SJsonOpts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // A4: Uri relativa via new Uri(new Uri(baseUrl), ...) perde o último
    // segmento quando baseUrl não termina em "/" (ex.: ".../v1" some).
    private static Uri BuildRequestUri(string baseUrl)
        => new(baseUrl.TrimEnd('/') + "/chat/completions");

    // A8: serialização comum de mensagens (role=tool e assistant.tool_calls) —
    // compartilhada por CompleteAsync, StreamAsync e StreamWithToolsAsync.
    private static List<object> BuildOpenAiMessages(string systemPrompt, List<LlmMessage> messages)
    {
        var openAiMessages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var msg in messages)
        {
            if (msg.Role == "tool")
            {
                openAiMessages.Add(new { role = "tool", tool_call_id = msg.ToolCallId, content = msg.Content });
            }
            else if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                openAiMessages.Add(new
                {
                    role = "assistant",
                    content = string.IsNullOrEmpty(msg.Content) ? null : msg.Content,
                    tool_calls = msg.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new
                        {
                            name = tc.Name,
                            arguments = tc.ArgumentsJson
                        }
                    }).ToList()
                });
            }
            else
            {
                openAiMessages.Add(new { role = msg.Role, content = msg.Content });
            }
        }
        return openAiMessages;
    }

    // A3: mensagem de erro rica (status + corpo truncado) com mapeamento
    // específico de erros comuns do OpenRouter (402 créditos, 429 rate limit).
    internal static string BuildProviderErrorMessage(System.Net.HttpStatusCode statusCode, string errorBody)
    {
        var body = errorBody;
        if (body.Length > 500) body = body[..500] + "...";
        return statusCode switch
        {
            System.Net.HttpStatusCode.PaymentRequired =>
                $"Provedor LLM retornou 402 (créditos insuficientes — verifique saldo OpenRouter). Detalhe: {body}",
            System.Net.HttpStatusCode.TooManyRequests =>
                $"Provedor LLM retornou 429 (limite de requisições). Tente novamente em instantes. Detalhe: {body}",
            System.Net.HttpStatusCode.Unauthorized =>
                $"Provedor LLM retornou 401 (API key inválida). Detalhe: {body}",
            _ => $"Provedor LLM retornou {(int)statusCode}. Detalhe: {body}"
        };
    }
}
