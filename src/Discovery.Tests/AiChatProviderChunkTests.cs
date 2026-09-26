using System.Text;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// Regressão B16: o parser de chunks do provider usava GetProperty (que lança
/// KeyNotFoundException em variações de shape — objeto de erro do OpenRouter,
/// chunk sem delta, etc.) e só tratava stop/tool_calls. O stream morria em
/// silêncio e o cliente recebia 200 sem tokens. Agora o parser é defensivo,
/// propaga o erro do provider e trata qualquer finish_reason.
/// </summary>
public class AiChatProviderChunkTests
{
    private static Dictionary<int, (string Id, string Name, StringBuilder Args)> Pending() => new();

    [Test]
    public void ContentDelta_ReturnsToken()
    {
        var evt = OpenAiProvider.ParseStreamChunk("{\"choices\":[{\"delta\":{\"content\":\"olá\"}}]}", Pending());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Type, Is.EqualTo("token"));
        Assert.That(evt.Content, Is.EqualTo("olá"));
    }

    [Test]
    public void ErrorObject_ReturnsStructuredErrorEvent()
    {
        var evt = OpenAiProvider.ParseStreamChunk("{\"error\":{\"message\":\"insufficient credits\"}}", Pending());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Type, Is.EqualTo("error"));
        Assert.That(evt.Content, Does.Contain("insufficient credits"));
    }

    [Test]
    public void MissingChoices_ReturnsNull()
    {
        Assert.That(OpenAiProvider.ParseStreamChunk("{\"id\":\"x\"}", Pending()), Is.Null);
    }

    [Test]
    public void LengthFinishReason_ReturnsDone()
    {
        var evt = OpenAiProvider.ParseStreamChunk("{\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}", Pending());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Type, Is.EqualTo("done"));
    }

    [Test]
    public void MissingDelta_DoesNotThrow()
    {
        var evt = OpenAiProvider.ParseStreamChunk("{\"choices\":[{\"finish_reason\":\"stop\"}]}", Pending());
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.Type, Is.EqualTo("done"));
    }

    [Test]
    public void ToolCallDeltas_AssembleAndEmit()
    {
        var pending = Pending();
        var first = OpenAiProvider.ParseStreamChunk(
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"get_pending_updates\",\"arguments\":\"{\"}}]}}]}",
            pending);
        Assert.That(first, Is.Null);
        var second = OpenAiProvider.ParseStreamChunk(
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
            pending);
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Type, Is.EqualTo("tool_calls"));
        Assert.That(second.ToolCalls![0].Name, Is.EqualTo("get_pending_updates"));
        Assert.That(second.ToolCalls[0].ArgumentsJson, Is.EqualTo("{}"));
    }
}
