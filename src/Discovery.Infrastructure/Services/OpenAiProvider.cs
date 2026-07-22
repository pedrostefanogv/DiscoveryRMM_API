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

        if (isOpenRouterModel)
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

    /// <summary>Aplica headers OpenRouter se o provider for openrouter</summary>
    private static void ApplyOpenRouterHeaders(HttpRequestMessage request, LlmOptions options)
    {
        if (!string.Equals(options.Provider, AIIntegrationSettings.ProviderOpenRouter, StringComparison.OrdinalIgnoreCase))
            return;

        // Sempre enviar HTTP-Referer e X-Title para identificar o app nos logs do OpenRouter
        // Fallback para defaults se não configurados (mantém consistência com embeddings)
        request.Headers.TryAddWithoutValidation("HTTP-Referer",
            !string.IsNullOrWhiteSpace(options.OpenRouterReferer)
                ? options.OpenRouterReferer
                : "https://discovery-rmm.local");

        request.Headers.TryAddWithoutValidation("X-Title",
            !string.IsNullOrWhiteSpace(options.OpenRouterTitle)
                ? options.OpenRouterTitle
                : "Discovery RMM");

        if (!string.IsNullOrWhiteSpace(options.OpenRouterCategories))
            request.Headers.TryAddWithoutValidation("X-Categories", options.OpenRouterCategories);

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
                ["temperature"] = options.Temperature
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

            var content = new StringContent(
                JsonSerializer.Serialize(payloadDict, new JsonSerializerOptions 
                { 
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull 
                }),
                Encoding.UTF8,
                "application/json");

            var requestUri = new Uri(new Uri(baseUrl), "chat/completions");
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            // Usa effectiveProvider para decidir headers OpenRouter
            var openRouterOpts = options with { Provider = effectiveProvider };
            ApplyOpenRouterHeaders(request, openRouterOpts);

            var httpClient = _httpClientFactory.CreateClient("AiChat");
            var response = await httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenAI API error: {StatusCode} - {Error}", 
                    response.StatusCode, errorBody);
                throw new HttpRequestException($"OpenAI API returned {response.StatusCode}");
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
                result.Usage.TotalTokens,
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

        // Montar mensagens
        var openAiMessages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var msg in messages)
        {
            openAiMessages.Add(new { role = msg.Role, content = msg.Content });
        }

        var payloadDict = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = openAiMessages,
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = options.Temperature,
            ["stream"] = true
        };

        AddSessionIdToPayload(payloadDict, options);

        var requestBody = new StringContent(
            JsonSerializer.Serialize(payloadDict),
            Encoding.UTF8,
            "application/json");

        var requestUri = new Uri(new Uri(baseUrl), "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = requestBody
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Usa effectiveProvider para decidir headers OpenRouter
        var openRouterOpts = options with { Provider = effectiveProvider };
        ApplyOpenRouterHeaders(request, openRouterOpts);

        var httpClient = _httpClientFactory.CreateClient("AiChat");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI stream error: {StatusCode} - {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenAI API returned {response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(line))
                continue;

            // Cada linha SSE começa com "data: "
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];

            if (data == "[DONE]")
                yield break;

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;

                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var contentProp) &&
                    contentProp.ValueKind == JsonValueKind.String)
                {
                    token = contentProp.GetString();
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

        var requestBody = new StringContent(
            JsonSerializer.Serialize(payloadDict, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8,
            "application/json");

        var requestUri = new Uri(new Uri(baseUrl), "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = requestBody };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Usa effectiveProvider para decidir headers OpenRouter
        var openRouterOpts = options with { Provider = effectiveProvider };
        ApplyOpenRouterHeaders(request, openRouterOpts);

        var httpClient = _httpClientFactory.CreateClient("AiChat");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenAI stream error: {StatusCode} - {Error}", response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenAI API returned {response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Acumuladores de tool calls (delta.tool_calls chega em chunks incrementais)
        var pendingToolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
            {
                yield return new LlmStreamEvent(Type: "done");
                yield break;
            }

            // ── Parse chunk (fora de try-catch para permitir yield) ──
            LlmStreamEvent? parsed = null;
            try
            {
                parsed = ParseStreamChunk(data, pendingToolCalls);
            }
            catch (JsonException)
            {
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
    private static LlmStreamEvent? ParseStreamChunk(string data, Dictionary<int, (string Id, string Name, StringBuilder Args)> pendingToolCalls)
    {
        using var doc = JsonDocument.Parse(data);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return null;

        var choice = choices[0];
        var delta = choice.GetProperty("delta");
        string? finishReason = null;
        if (choice.TryGetProperty("finish_reason", out var frProp) && frProp.ValueKind == JsonValueKind.String)
            finishReason = frProp.GetString();

        // 1. Delta content (texto)
        if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
        {
            var token = contentProp.GetString();
            if (!string.IsNullOrEmpty(token))
                return new LlmStreamEvent(Type: "token", Content: token);
        }

        // 2. Delta tool_calls (incremental)
        if (delta.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcProp.EnumerateArray())
            {
                var index = tc.GetProperty("index").GetInt32();

                if (!pendingToolCalls.ContainsKey(index))
                {
                    var id = tc.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()! : string.Empty;
                    var fn = tc.GetProperty("function");
                    var name = fn.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String
                        ? nProp.GetString()! : string.Empty;
                    pendingToolCalls[index] = (id, name, new StringBuilder());
                }

                var existing = pendingToolCalls[index];
                var fnDelta = tc.GetProperty("function");
                if (fnDelta.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.String)
                    existing.Args.Append(argsProp.GetString());
            }
        }

        // 3. Finish reason = tool_calls → emitir tool calls acumuladas
        if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase) && pendingToolCalls.Count > 0)
        {
            var parsedToolCalls = pendingToolCalls.Values.Select(tc => new LlmToolCall(
                tc.Id, tc.Name, tc.Args.ToString())).ToList();

            int? tokensUsed = null;
            if (doc.RootElement.TryGetProperty("usage", out var usageProp))
            {
                if (usageProp.TryGetProperty("total_tokens", out var ttProp))
                    tokensUsed = ttProp.GetInt32();
            }

            return new LlmStreamEvent(Type: "tool_calls", ToolCalls: parsedToolCalls, TokensUsed: tokensUsed);
        }

        // 4. Finish reason = stop
        if (string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase))
        {
            int? tokensUsed = null;
            if (doc.RootElement.TryGetProperty("usage", out var usageProp))
            {
                if (usageProp.TryGetProperty("total_tokens", out var ttProp))
                    tokensUsed = ttProp.GetInt32();
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
        [property: JsonPropertyName("usage")] OpenAiUsage Usage
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
}
