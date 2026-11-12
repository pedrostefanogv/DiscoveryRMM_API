using Discovery.Api.Controllers;
using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// F5 (task-8): o catch de ChatStream trocava QUALQUER exceção pelo texto
/// constante "Erro interno ao processar o chat.", destruindo o erro real do
/// provedor (é por isso que o agent gravava status_code=0 e o suporte não tinha
/// como diferenciar 400 de payload malformado de 401 key / 402 créditos / 429
/// rate limit). Estes testes fixam o contrato de DescribeChatStreamFailure:
/// exceção de provedor é SURFACADA (texto + status + truncamento) e exceção
/// realmente inesperada continua genérica — B7 preservado.
/// </summary>
public class AgentAuthControllerChatStreamErrorTests
{
    [Test]
    public void HttpRequestException_DoProvedor_E_Surfaceada()
    {
        // Reproduz o texto real do OpenAiProvider:666 (BuildProviderErrorMessage).
        const string provider = "Provedor LLM retornou 400. Detalhe: {\"error\":{\"message\":\"No function call found for function call output with call_id 'call_01769fe00cf715c9_2'\"}}";

        var (status, detail) = AgentAuthController.DescribeChatStreamFailure(new HttpRequestException(provider));

        Assert.That(status, Is.EqualTo(502), "O status do SSE precisa ser reportado (hoje o agent grava 0).");
        Assert.That(detail, Is.Not.Null, "Erro de provedor NÃO pode cair na mensagem genérica.");
        Assert.That(detail, Does.Contain("call_01769fe00cf715c9_2"),
            "O call_id do provedor precisa chegar ao agent: é o dado que diagnostica o payload malformado.");
        Assert.That(detail, Does.Contain("400"), "O status upstream vem embutido no texto do provedor.");
    }

    [Test]
    public void ProviderStreamErrorException_E_Surfaceada()
    {
        var (status, detail) = AgentAuthController.DescribeChatStreamFailure(
            new ProviderStreamErrorException("Provider stream error: {\"error\":{\"message\":\"insufficient credits\"}}"));

        Assert.That(status, Is.EqualTo(502));
        Assert.That(detail, Does.Contain("insufficient credits"));
    }

    [Test]
    public void ExcecaoInesperada_ContinuaGenerica()
    {
        foreach (var ex in new Exception[]
                 {
                     new NullReferenceException("Object reference not set to an instance of an object."),
                     new InvalidOperationException("/var/lib/discovery/appsettings.json is missing"),
                     new ArgumentException("relation \"ai_chat_messages\" does not exist")
                 })
        {
            var (status, detail) = AgentAuthController.DescribeChatStreamFailure(ex);
            Assert.That(detail, Is.Null, "B7: exceção interna NÃO pode ser exposta ao client.");
            Assert.That(status, Is.EqualTo(500));
        }
    }

    [Test]
    public void HttpRequestException_ComMensagemPadraoDoFramework_E_SurfaceadaComoSempre()
    {
        // new HttpRequestException() sem mensagem usa o texto padrão do framework
        // ("Exception of type 'System.Net.Http.HttpRequestException' was thrown."),
        // que não carrega nada do provedor — mas também não vaza interno. É esse o
        // ramo que o filtro IsNullOrWhiteSpace cobre; o teste fixa o comportamento.
        var (status, detail) = AgentAuthController.DescribeChatStreamFailure(new HttpRequestException());

        Assert.That(status, Is.EqualTo(502));
        Assert.That(detail, Is.Not.Null);
        Assert.That(detail, Does.Not.Contain("   at "), "Nunca expor stack trace (B7).");
    }

    [Test]
    public void HttpRequestException_ComMensagemEmBranco_ContinuaGenerica()
    {
        var (status, detail) = AgentAuthController.DescribeChatStreamFailure(new HttpRequestException("   "));

        Assert.That(detail, Is.Null, "Mensagem em branco não é erro do provedor.");
        Assert.That(status, Is.EqualTo(500));
    }

    [Test]
    public void DetalheDoProvedor_E_TruncadoEm500Chars()
    {
        var gigante = new string('x', 5000);

        var (_, detail) = AgentAuthController.DescribeChatStreamFailure(new HttpRequestException(gigante));

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Length, Is.EqualTo(503), "500 chars + reticencias (mesmo teto do BuildProviderErrorMessage).");
        Assert.That(detail, Does.EndWith("..."));
    }

    [Test]
    public void DetalheCurto_NaoGanhaReticencias()
    {
        var (_, detail) = AgentAuthController.DescribeChatStreamFailure(new HttpRequestException("Provedor LLM retornou 429."));

        Assert.That(detail, Is.EqualTo("Provedor LLM retornou 429."));
    }
}
