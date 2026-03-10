using Meduza.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meduza.Infrastructure.Services;

public class OpenAiProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<OpenAiProvider> _logger;

    public OpenAiProvider(IConfiguration config, ILogger<OpenAiProvider> logger)
    {
        _apiKey = config["OpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured in environment");
        _model = config["OpenAI:Model"] ?? "gpt-4-turbo";
        
        var timeoutSeconds = int.TryParse(config["OpenAI:TimeoutSeconds"], out var timeout) ? timeout : 30;
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // IMPORTANTE: NUNCA logar _apiKey
            _logger.LogInformation(
                "Calling OpenAI with {MessageCount} messages, maxTokens={MaxTokens}, model={Model}", 
                messages.Count, options.MaxTokens, _model);

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
                else
                {
                    openAiMessages.Add(new
                    {
                        role = msg.Role,
                        content = msg.Content
                    });
                }
            }

            // Montar payload
            var payload = new
            {
                model = _model,
                messages = openAiMessages,
                max_tokens = options.MaxTokens,
                temperature = options.Temperature,
                tools = options.EnableTools && options.Tools != null 
                    ? options.Tools.Select(t => new
                    {
                        type = "function",
                        function = new
                        {
                            name = t.Name,
                            description = t.Description,
                            parameters = t.Schema
                        }
                    }).ToList()
                    : null
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload, new JsonSerializerOptions 
                { 
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull 
                }),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("chat/completions", content, cancellationToken);
            
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
