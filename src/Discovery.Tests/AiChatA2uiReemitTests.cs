using System.Runtime.CompilerServices;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// M2: reemissão da definição A2UI quando o card nasce sem updateComponents com
/// "root". Aqui o helper é testado isoladamente (contrato de contexto, usage e
/// tratamento de erro); o pipeline é coberto pelos testes de extrator/validador.
/// </summary>
public class AiChatA2uiReemitTests
{
    private const string ValidDefinition =
        "{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"paper_jam\",\"components\":[{\"id\":\"root\",\"component\":\"Column\"}]}}";

    [Test]
    public async Task Reemit_ReturnsContentAndTokens_AndSendsPreviousOutputPlusNote()
    {
        var provider = new FakeLlmProvider(ValidDefinition);
        var orchestrator = BuildOrchestrator(provider);
        var messages = new List<LlmMessage> { new("user", "gere o card") };

        var (content, tokens) = await orchestrator.TryReemitA2uiDefinitionAsync(
            "system", messages, new AIIntegrationSettings(), 1000, Guid.NewGuid(),
            "texto anterior com o bloco a2ui", new[] { "paper_jam" }, CancellationToken.None);

        Assert.That(content, Does.Contain("updateComponents"));
        Assert.That(tokens, Is.EqualTo(42), "o usage do provider precisa voltar para a telemetria do turno");
        Assert.That(provider.LastMessages, Is.Not.Null);
        Assert.That(provider.LastMessages!.Any(m => m.Role == "assistant" && m.Content.Contains("texto anterior")), Is.True,
            "a saída anterior precisa entrar no contexto (o assistant do stream não fica em llmMessages)");
        Assert.That(provider.LastMessages!.Any(m => m.Role == "system" && m.Content.Contains("paper_jam")), Is.True,
            "a nota precisa citar o surfaceId sem definição");
    }

    [Test]
    public async Task Reemit_WhenProviderReturnsError_ReturnsEmptyWithoutThrowing()
    {
        var orchestrator = BuildOrchestrator(new FakeLlmProvider(error: true));

        var (content, tokens) = await orchestrator.TryReemitA2uiDefinitionAsync(
            "system", new List<LlmMessage>(), new AIIntegrationSettings(), 1000, Guid.NewGuid(),
            "", new[] { "s" }, CancellationToken.None);

        Assert.That(content, Is.Empty);
        Assert.That(tokens, Is.Zero);
    }

    [Test]
    public async Task Reemit_WhenProviderThrows_ReturnsEmptyWithoutThrowing()
    {
        var orchestrator = BuildOrchestrator(new FakeLlmProvider(throwOnStream: true));

        var (content, tokens) = await orchestrator.TryReemitA2uiDefinitionAsync(
            "system", new List<LlmMessage>(), new AIIntegrationSettings(), 1000, Guid.NewGuid(),
            "", new[] { "s" }, CancellationToken.None);

        Assert.That(content, Is.Empty);
        Assert.That(tokens, Is.Zero);
    }

    private static AiChatStreamingOrchestrator BuildOrchestrator(ILlmProvider provider) => new(
        null!, null!, null!, null!, provider, null!, null!, NullLogger<AiChatService>.Instance,
        null!, null!, null!, null!, null!, null!);

    private sealed class FakeLlmProvider : ILlmProvider
    {
        private readonly string _content;
        private readonly bool _error;
        private readonly bool _throw;

        public FakeLlmProvider(string content = "", bool error = false, bool throwOnStream = false)
        {
            _content = content;
            _error = error;
            _throw = throwOnStream;
        }

        public List<LlmMessage>? LastMessages { get; private set; }

        public Task<LlmResponse> CompleteAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(_content, 0, "fake"));

        public async IAsyncEnumerable<string> StreamAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return _content;
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamWithToolsAsync(
            string systemPrompt, List<LlmMessage> messages, LlmOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            if (_throw) throw new InvalidOperationException("provider fora do ar");
            await Task.CompletedTask;
            if (_error)
            {
                yield return LlmStreamEvent.Error("erro do provider");
                yield break;
            }
            yield return new LlmStreamEvent("token", _content);
            yield return new LlmStreamEvent("done", TokensUsed: 42);
        }
    }
}
