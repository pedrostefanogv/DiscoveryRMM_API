namespace Meduza.Core.Interfaces;

public interface ILlmProvider
{
    Task<LlmResponse> CompleteAsync(
        string systemPrompt,
        List<LlmMessage> messages,
        LlmOptions options,
        CancellationToken cancellationToken = default);
}

public record LlmMessage(
    string Role, 
    string Content, 
    string? ToolCallId = null, 
    string? ToolName = null);

public record LlmOptions(
    int MaxTokens = 1000,
    double Temperature = 0.7,
    bool EnableTools = false,
    List<LlmTool>? Tools = null);

public record LlmTool(
    string Name, 
    string Description, 
    object Schema);

public record LlmResponse(
    string Content,
    int TokensUsed,
    string ModelVersion,
    List<LlmToolCall>? ToolCalls = null);

public record LlmToolCall(
    string Id, 
    string Name, 
    string ArgumentsJson);
