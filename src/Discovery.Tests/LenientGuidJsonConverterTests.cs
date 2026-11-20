using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Serialization;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Contrato do envelope do chat de suporte com o AGENT.
///
/// BUG CORRIGIDO: o agent (Go) publicava <c>"ticketId":""</c> e
/// <c>"agentId":""</c> nas conversas AVULSAS (sem chamado). O contrato do
/// servidor declara <c>TicketId</c> como <c>Guid?</c> e <c>AgentId</c> como
/// <c>Guid</c>, então o System.Text.Json lançava
///
///   The JSON value could not be converted to System.Nullable`1[System.Guid]. Path: $.ticketId
///
/// o envelope era DESCARTADO no <c>HandleSupportChatMessageAsync</c> e a
/// mensagem do usuário final nunca era persistida — o operador não via nada no
/// navegador, embora o agent tivesse recebido PubAck do JetStream.
///
/// Estes testes fixam as duas defesas: o conversor tolerante do SERVIDOR (que
/// cobre toda máquina já instalada, publicando o formato antigo) e a
/// normalização do payload real que o agent enviava.
/// </summary>
[TestFixture]
public class LenientGuidJsonConverterTests
{
    /// <summary>Espelho das opções usadas em NatsAgentMessaging.</summary>
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new LenientGuidJsonConverter() }
        };
        return options;
    }

    [Test]
    public void Envelope_ComTicketIdVazio_DeserializaSemLancar()
    {
        // Payload EXATO do bug (byte position 87 apontado pelo log da API).
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"","agentId":"01a1eb40-eec7-74e3-8792-b11829bb7811","authorKind":"enduser","body":"Teste do chat","clientMessageId":"0d1b6a5f-9d3a-4a2e-9d55-1c2f9a2f7b41"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope, Is.Not.Null);
        Assert.That(envelope!.TicketId, Is.Null, "ticketId vazio deve virar null (conversa avulsa)");
        Assert.That(envelope.SessionId, Is.EqualTo(Guid.Parse("b066d11d-84dc-401f-ba23-ba907fb5c5d4")));
        Assert.That(envelope.EventType, Is.EqualTo(TicketChatEventTypes.Message));
        Assert.That(envelope.Body, Is.EqualTo("Teste do chat"));
        Assert.That(envelope.AuthorKind, Is.EqualTo("enduser"));
    }

    [Test]
    public void Envelope_ComAmbosOsCamposVazios_DeserializaSemLancar()
    {
        // O agent antigo emitia os DOIS vazios numa conversa avulsa.
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"","agentId":"","authorKind":"enduser","body":"oi"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope, Is.Not.Null);
        Assert.That(envelope!.TicketId, Is.Null);
        Assert.That(envelope.AgentId, Is.EqualTo(Guid.Empty), "agentId vazio vira Guid.Empty e é sobrescrito pelo id do subject");
        Assert.That(envelope.Body, Is.EqualTo("oi"));
    }

    [Test]
    public void Envelope_ComCamposAusentes_ContinuaDeserializando()
    {
        // Formato NOVO do agent (omitempty): os campos simplesmente não vêm.
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","authorKind":"enduser","body":"oi"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope, Is.Not.Null);
        Assert.That(envelope!.TicketId, Is.Null);
        Assert.That(envelope.AgentId, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void Envelope_ComTicketIdValido_PreservaChamado()
    {
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"1f0f3a52-5b3f-4a1e-9a1a-6b0f2f7c1d2e","agentId":"01a1eb40-eec7-74e3-8792-b11829bb7811","body":"oi"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope!.TicketId, Is.EqualTo(Guid.Parse("1f0f3a52-5b3f-4a1e-9a1a-6b0f2f7c1d2e")));
        Assert.That(envelope.AgentId, Is.EqualTo(Guid.Parse("01a1eb40-eec7-74e3-8792-b11829bb7811")));
    }

    [Test]
    public void Envelope_ComEspacosEmBranco_TrataComoAusente()
    {
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"   ","body":"oi"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope!.TicketId, Is.Null);
    }

    [Test]
    public void Envelope_ComGuidInvalido_AindaFalha()
    {
        // A tolerância é só para VAZIO: lixo continua sendo contrato violado.
        const string payload = """
        {"eventType":"Message","sessionId":"b066d11d-84dc-401f-ba23-ba907fb5c5d4","ticketId":"nao-e-guid","body":"oi"}
        """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options()));
    }

    [Test]
    public void SessionIdVazio_NaoDeserializaComoGuidValido()
    {
        // sessionId é Guid (não anulável): vazio vira Guid.Empty e o chamador
        // descarta o envelope com CONTRACT_VIOLATION — não persiste em sessão errada.
        const string payload = """
        {"eventType":"Message","sessionId":"","ticketId":"","body":"oi"}
        """;

        var envelope = JsonSerializer.Deserialize<TicketChatEnvelope>(payload, Options());

        Assert.That(envelope!.SessionId, Is.EqualTo(Guid.Empty));
    }

    [Test]
    public void Conversao_NaoAlteraSerializacaoDeSaida()
    {
        // O MESMO JsonOptions serializa o envelope rumo ao agent: o conversor não
        // pode mudar o formato de saída (Guid -> string, nulo -> null).
        var envelope = new TicketChatEnvelope
        {
            EventType = TicketChatEventTypes.Message,
            SessionId = Guid.Parse("b066d11d-84dc-401f-ba23-ba907fb5c5d4"),
            TicketId = null,
            AgentId = Guid.Parse("01a1eb40-eec7-74e3-8792-b11829bb7811"),
            Body = "oi"
        };

        var json = JsonSerializer.Serialize(envelope, Options());

        Assert.That(json, Does.Contain("\"ticketId\":null"), "conversa avulsa continua saindo com ticketId null (contrato do agent)");
        Assert.That(json, Does.Contain("\"agentId\":\"01a1eb40-eec7-74e3-8792-b11829bb7811\""));
    }
}
