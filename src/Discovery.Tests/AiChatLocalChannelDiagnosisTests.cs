using System.Text.Json;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// TESTE DE REGRESSÃO do bloco P2 — critério :184 do
/// PLANO_CORRECAO_CANAL_ADMIN_IPC.md:
/// "IA não afirma 'servidor fora do ar' para erro local_agent e não repete a
/// mesma chamada mutante com retryable=false".
///
/// Contexto: no incidente de 2026-10-17 o pipe admin do agente nunca subiu, o
/// envelope de erro não existia e o modelo concluiu que "a falha é do lado do
/// servidor de suporte", repetindo create_ticket 3 vezes (~38s). Estes testes
/// travam as quatro garantias que impedem a repetição:
///   1. o erro estruturado do agente é RECONHECIDO (scope/code/retryable);
///   2. o servidor NÃO reembrulha o erro com o hint genérico "tente novamente";
///   3. o prompt efetivo PROÍBE o diagnóstico "servidor fora do ar" e o retry;
///   4. a nota injetada no turno manda reiniciar o serviço e não repetir.
/// </summary>
[TestFixture]
public class AiChatLocalChannelDiagnosisTests
{
    /// <summary>
    /// Caminho REAL do pipe admin (igual a IPCPipeAdminName no agente Go):
    /// barra-barra-ponto-barra-barra-pipe. Escrito com verbatim + escapes para
    /// ficar inequívoco.
    /// </summary>
    private const string AdminPipe = "\\.\\pipe\\discovery-agent-ipc-admin";

    /// <summary>
    /// Monta o envelope EXATO emitido pelo agente Go (ipc_channel_error.go).
    /// Usa JsonSerializer para o detail, evitando qualquer escape manual — o
    /// escaping manual foi a causa de o teste falhar na primeira versão.
    /// </summary>
    private static string Envelope(string code, string scope, bool retryable, string? hint = null)
    {
        var detail = "canal de gravação local indisponível (pipe " + AdminPipe + "): Incorrect function.";
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["scope"] = scope,
            ["retryable"] = retryable,
            ["detail"] = detail,
            ["method"] = "support:agent_ticket_create",
            ["pipe"] = AdminPipe,
        };
        if (hint is not null) error["hint"] = hint;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["error"] = error });
    }

    // ── 1. Reconhecimento do envelope ──────────────────────────────────────

    [Test]
    public void InspectAgentToolError_ReconheceFalhaLocalNaoRetentavel()
    {
        var raw = Envelope(AiChatHelpers.LocalAgentWriteUnavailableCode, AiChatHelpers.LocalAgentErrorScope, retryable: false);
        var info = AiChatToolOrchestrator.InspectAgentToolError(raw);

        Assert.That(info.IsLocalAgentFailure, Is.True, "scope=local_agent precisa ser reconhecido");
        Assert.That(info.NonRetryable, Is.True, "retryable=false precisa bloquear o retry");
        Assert.That(info.Code, Is.EqualTo(AiChatHelpers.LocalAgentWriteUnavailableCode));
        Assert.That(info.Scope, Is.EqualTo("local_agent"));
        Assert.That(AiChatToolOrchestrator.IsNonRetryableLocalAgentWrite(raw), Is.True);
    }

    [Test]
    public void InspectAgentToolError_CircuitBreakerDoAgenteTambemBloqueia()
    {
        // Segunda mutação da sessão (P2.11 no agente): mesmo tratamento.
        var raw = Envelope(AiChatHelpers.LocalAgentWriteBlockedBySessionCode, "local_agent", false);
        var info = AiChatToolOrchestrator.InspectAgentToolError(raw);

        Assert.That(info.NonRetryable, Is.True);
        Assert.That(info.Code, Is.EqualTo(AiChatHelpers.LocalAgentWriteBlockedBySessionCode));
    }

    /// <summary>
    /// Erro RETENTÁVEL (ex.: falha transitória) NÃO pode ser tratado como
    /// definitivo — o modelo precisa poder corrigir e tentar de novo.
    /// </summary>
    [Test]
    public void InspectAgentToolError_NaoBloqueiaErroRetentavel()
    {
        var raw = Envelope("LOCAL_IPC_TRANSIENT", "local_agent", retryable: true);
        var info = AiChatToolOrchestrator.InspectAgentToolError(raw);

        Assert.That(info.IsLocalAgentFailure, Is.True);
        Assert.That(info.NonRetryable, Is.False);
        Assert.That(AiChatToolOrchestrator.IsNonRetryableLocalAgentWrite(raw), Is.False);
    }

    /// <summary>Falha do SERVIDOR de suporte segue o fluxo normal (não é local).</summary>
    [Test]
    public void InspectAgentToolError_EscopoRemotoNaoEhFalhaLocal()
    {
        var info = AiChatToolOrchestrator.InspectAgentToolError(
            Envelope("REMOTE_SERVICE_ERROR", "remote_service", retryable: false));

        Assert.That(info.IsLocalAgentFailure, Is.False);
        Assert.That(info.NonRetryable, Is.False, "escopo remoto não pode acionar o corte de mutações locais");
    }

    /// <summary>
    /// Agentes LEGADOS (texto cru) e resultados de sucesso nunca disparam o
    /// corte — a decisão não sai de heurística de substring.
    /// </summary>
    [TestCase("canal admin indisponível (serviço sem autenticação?)")]
    [TestCase("{\"error\":\"parametro obrigatorio ausente\"}")]
    [TestCase("{\"error\":{\"code\":\"X\"}}")]
    [TestCase("{\"ok\":true,\"data\":{\"ticketId\":\"1\"}}")]
    [TestCase("{\"error\":{")]
    [TestCase("")]
    [TestCase("[1,2,3]")]
    public void InspectAgentToolError_NaoDisparaComResultadoNormal(string raw)
    {
        Assert.That(AiChatToolOrchestrator.IsNonRetryableLocalAgentWrite(raw), Is.False);
    }

    // ── 2. WrapAgentToolError: sem hint de retry, sem perda de escopo ───────

    /// <summary>
    /// O servidor NÃO pode aplicar o hint genérico "corrija o problema e tente
    /// novamente" a um erro retryable=false: era esse reflexo que produzia as 3
    /// chamadas idênticas de create_ticket.
    /// </summary>
    [Test]
    public void WrapAgentToolError_NaoReembrulharComHintDeRetry()
    {
        var raw = Envelope(AiChatHelpers.LocalAgentWriteUnavailableCode, "local_agent", false);
        var wrapped = AiChatToolOrchestrator.WrapAgentToolError(raw, "create_ticket");

        Assert.That(wrapped, Does.Not.Contain("corrija o problema antes de tentar novamente"),
            "erro definitivo não pode receber hint de retry");
        Assert.That(wrapped, Does.Contain(AiChatHelpers.LocalAgentWriteUnavailableCode),
            "o code do agente precisa sobreviver ao wrapping");
        Assert.That(wrapped, Does.Contain("local_agent"), "o scope precisa sobreviver");
        Assert.That(wrapped, Does.Contain("retryable"), "retryable precisa estar no resultado");
    }

    /// <summary>
    /// O envelope do agente é preservado: code/scope/detail continuam legíveis
    /// para o modelo (é a base da regra anti-retry do prompt).
    /// </summary>
    [Test]
    public void WrapAgentToolError_PreservaOEnvelopeEstruturado()
    {
        var raw = Envelope(AiChatHelpers.LocalAgentWriteUnavailableCode, "local_agent", false);
        var wrapped = AiChatToolOrchestrator.WrapAgentToolError(raw, "create_ticket");

        using var doc = JsonDocument.Parse(wrapped);
        var error = doc.RootElement.GetProperty("error");
        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo(AiChatHelpers.LocalAgentWriteUnavailableCode));
        Assert.That(error.GetProperty("scope").GetString(), Is.EqualTo("local_agent"));
        Assert.That(error.GetProperty("retryable").GetBoolean(), Is.False);
        Assert.That(error.GetProperty("detail").GetString(), Does.Contain("Incorrect function"),
            "o erro de SO precisa chegar ao modelo (P2.12)");
    }

    /// <summary>Resultado de SUCESSO não pode ser alterado pelo wrapping.</summary>
    [Test]
    public void WrapAgentToolError_NaoAlteraResultadoDeSucesso()
    {
        const string ok = "{\"ok\":true,\"data\":{\"ticketId\":\"123\"}}";
        Assert.That(AiChatToolOrchestrator.WrapAgentToolError(ok, "create_ticket"), Is.EqualTo(ok));
    }

    // ── 3. Prompt efetivo: proíbe o diagnóstico errado e o retry ───────────

    /// <summary>
    /// A regra tem de estar no prompt EFETIVO do servidor (é ele que o modelo vê
    /// no fluxo multi-round — §7.1 do plano).
    /// </summary>
    [Test]
    public void PromptEfetivo_ProibeAfirmarQueOServidorEstaFora()
    {
        var prompt = BuildEffectivePrompt();

        Assert.That(prompt, Does.Contain("local_agent"), "o prompt precisa citar explicitamente o escopo local");
        Assert.That(prompt, Does.Contain("LOCAL_IPC_WRITE_UNAVAILABLE"), "o prompt precisa citar o código do erro");
        Assert.That(prompt, Does.Contain("PROIBIDO afirmar"), "a regra precisa ser imperativa");
        Assert.That(prompt, Does.Contain("fora do ar"));
        Assert.That(prompt, Does.Contain("servidor de suporte"));
    }

    [Test]
    public void PromptEfetivo_ProibeRepetirChamadaComRetryableFalse()
    {
        var prompt = BuildEffectivePrompt();

        Assert.That(prompt, Does.Contain("retryable = false"));
        Assert.That(prompt, Does.Contain("DEFINITIVA"), "o prompt precisa marcar a chamada como definitiva");
        Assert.That(prompt, Does.Contain("PROIBIDO repetir a chamada"), "a proibição de repetir tem de ser nominal");
    }

    [Test]
    public void PromptEfetivo_OrientaReiniciarOServico()
    {
        var prompt = BuildEffectivePrompt();

        Assert.That(prompt, Does.Contain("Reiniciar o serviço DiscoveryAgent").IgnoreCase,
            "o prompt precisa orientar reiniciar o serviço local");
        Assert.That(prompt, Does.Contain("as consultas continuam respondendo"),
            "o prompt precisa dar o argumento que desmente a falha de servidor");
    }

    // ── 4. Nota anti-retry injetada no turno ───────────────────────────────

    /// <summary>
    /// A nota do turno (P2.11 no servidor) precisa: (a) proibir o diagnóstico
    /// "servidor fora do ar", (b) proibir repetir a mutação, (c) orientar
    /// reiniciar o serviço.
    /// </summary>
    [Test]
    public void NotaAntiRetry_ContemAsTresObrigacoes()
    {
        var note = AiChatHelpers.LocalAgentWriteUnavailableNote;

        Assert.That(note, Does.StartWith("[SISTEMA]"));
        Assert.That(note, Does.Contain("PROIBIDO"), "a nota precisa ser imperativa");
        Assert.That(note, Does.Contain("fora do ar"), "a nota precisa nomear a afirmação proibida");
        Assert.That(note, Does.Contain("repetir"), "a nota precisa proibir o retry");
        Assert.That(note, Does.Contain("DiscoveryAgent"), "a nota precisa orientar reiniciar o serviço");
        Assert.That(note, Does.Contain("NÃO no servidor de suporte"), "a nota precisa desmentir a falha de servidor");
    }

    /// <summary>
    /// Contrato entre os dois repositórios: os códigos do servidor precisam
    /// bater com os do agente Go (ipc_channel_error.go).
    /// </summary>
    [Test]
    public void ContratoDeCodigos_ConfereComOAgenteGo()
    {
        Assert.That(AiChatHelpers.LocalAgentWriteUnavailableCode, Is.EqualTo("LOCAL_IPC_WRITE_UNAVAILABLE"));
        Assert.That(AiChatHelpers.LocalAgentWriteBlockedBySessionCode, Is.EqualTo("LOCAL_IPC_WRITE_BLOCKED_BY_SESSION"));
        Assert.That(AiChatHelpers.LocalAgentErrorScope, Is.EqualTo("local_agent"));
    }

    /// <summary>Monta o prompt pelo mesmo caminho do prompt default.</summary>
    private static string BuildEffectivePrompt()
    {
        var agent = new Discovery.Core.Entities.Agent { Id = Guid.NewGuid(), Hostname = "PC-TESTE" };
        return AiChatSystemPromptBuilder.BuildDefaultSystemPrompt(agent);
    }
}
